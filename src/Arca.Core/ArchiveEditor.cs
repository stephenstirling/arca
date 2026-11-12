using System.Runtime.InteropServices;
using System.Text;

namespace Arca.Core;

/// <summary>
/// Changes an existing archive in place: add files into a folder of it, delete, rename, new folder. Done by the
/// bundled 7za.exe, which writes the new archive beside the old one and swaps it in only when complete, and which
/// copies untouched files across without recompressing them. ZIP, 7z and plain TAR only: compressed TAR (.tar.gz
/// and the like) has to be rebuilt whole, RAR can only be written by WinRAR, and a multi-part archive would have
/// to be re-split.
/// </summary>
public static class ArchiveEditor
{
    /// <summary>Why <paramref name="listing"/> can't be changed in place, or null when it can.</summary>
    public static string? WhyNotEditable(ArchiveListing listing)
    {
        if (listing.Parts.Count > 1)
            return "Multi-part archives can't be changed. Extract it and make a new archive instead.";
        return listing.Format switch
        {
            ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Tar => null,
            ArchiveFormat.Rar4 or ArchiveFormat.Rar5 => "RAR archives can only be changed with WinRAR. Extract it and make a ZIP or 7Z instead.",
            ArchiveFormat.Gzip or ArchiveFormat.Bzip2 or ArchiveFormat.Xz =>
                "Compressed TAR archives (.tar.gz, .tar.bz2, .tar.xz) can't be changed in place. Extract it and make a new archive instead.",
            _ => $"{ArchiveFormats.DisplayName(listing.Format)} archives can't be changed.",
        };
    }

    /// <summary>
    /// The names a file or folder may have inside an archive: anything Windows could also extract it as.
    /// </summary>
    /// <exception cref="ArgumentException">Says what is wrong with <paramref name="name"/>, in plain words.</exception>
    public static string ValidateName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Type a name.");
        if (name is "." or "..") throw new ArgumentException("That name isn't allowed.");
        var bad = name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' });
        if (bad >= 0) throw new ArgumentException($"Names can't contain {name[bad]}");
        if (name.Any(char.IsControl)) throw new ArgumentException("Names can't contain control characters.");
        if (name.EndsWith('.')) throw new ArgumentException("Names can't end with a dot.");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            throw new ArgumentException($"{name} is reserved by Windows.");
        return name;
    }

    /// <summary>
    /// Entry names that adding <paramref name="sources"/> into <paramref name="intoFolder"/> would replace, so the
    /// person can be asked first.
    /// </summary>
    public static IReadOnlyList<string> WouldReplace(ArchiveListing listing, IReadOnlyList<string> sources, string intoFolder)
    {
        var plan = ArchiveWriter.Plan(sources, listing.ArchivePath, CancellationToken.None, out _);
        var existing = listing.Entries.Where(e => !e.IsDirectory).Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plan.Select(p => Join(intoFolder, p.EntryName)).Where(existing.Contains).ToList();
    }

    /// <summary>
    /// Adds <paramref name="sources"/> (files and folders, links skipped) into <paramref name="intoFolder"/> of the
    /// archive ("" for the top). An entry of the same name is replaced. New files are encrypted when a password is given.
    /// </summary>
    public static CreateResult Add(
        ArchiveListing listing,
        IReadOnlyList<string> sources,
        string intoFolder,
        string? password = null,
        IProgress<ExtractProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? sevenZip = null)
    {
        RequireEditable(listing);
        intoFolder = ArchivePaths.Normalize(intoFolder);
        var plan = ArchiveWriter.Plan(sources, listing.ArchivePath, cancellationToken, out var skipped);
        var result = new CreateResult { ArchivePath = listing.ArchivePath };
        result.Skipped.AddRange(skipped);
        if (plan.Count == 0) return result;

        var exe = sevenZip ?? SevenZipCli.DefaultPath;
        var args = Switches("a", listing, password);
        args.Add("-ssw");
        long totalBytes = plan.Sum(p => SafeLength(p.File));

        if (intoFolder.Length == 0)
        {
            foreach (var group in SevenZipCli.GroupByRoot(plan))
                WithListFile(group.Relative, list =>
                    SevenZipCli.Run(exe, group.Root, [.. args, listing.ArchivePath, "@" + list], password, totalBytes, result, progress, cancellationToken));
        }
        else
        {
            // 7za names each file by its path relative to where it runs, so lay the files out under the target
            // folder in a scratch folder first. Hard links make that free; a copy is the fallback across drives.
            var stage = Path.Combine(Path.GetTempPath(), "Arca", "Stage", Guid.NewGuid().ToString("N"));
            try
            {
                var relative = new List<string>();
                foreach (var (file, entryName) in plan)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Join(intoFolder, entryName).Replace('/', Path.DirectorySeparatorChar);
                    var staged = Path.Combine(stage, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    try
                    {
                        if (!TryHardLink(staged, file)) File.Copy(file, staged);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        result.Skipped.Add(new SkippedEntry(file, ex.Message));
                        continue;
                    }
                    relative.Add(name);
                }
                if (relative.Count > 0)
                    WithListFile(relative, list =>
                        SevenZipCli.Run(exe, stage, [.. args, listing.ArchivePath, "@" + list], password, totalBytes, result, progress, cancellationToken));
            }
            finally
            {
                try { Directory.Delete(stage, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        var skippedPaths = result.Skipped.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, _) in plan)
        {
            if (skippedPaths.Contains(file)) continue;
            result.FilesAdded++;
            result.BytesAdded += SafeLength(file);
        }
        return result;
    }

    /// <summary>Deletes these entries; a folder takes everything inside it with it.</summary>
    public static void Delete(
        ArchiveListing listing, IReadOnlyCollection<string> paths, string? password = null,
        CancellationToken cancellationToken = default, string? sevenZip = null)
    {
        RequireEditable(listing);
        var names = Expand(listing, paths).ToList();
        if (names.Count == 0) return;
        var args = Switches("d", listing, password);
        WithListFile(names.Select(Native).ToList(), list =>
            SevenZipCli.Execute(sevenZip ?? SevenZipCli.DefaultPath, Path.GetTempPath(), [.. args, listing.ArchivePath, "@" + list], password, cancellationToken));
    }

    /// <summary>Renames one entry (a folder renames everything inside it) to <paramref name="newName"/> in the same folder.</summary>
    /// <exception cref="ArgumentException">The new name isn't allowed, or something there already has it.</exception>
    public static void Rename(
        ArchiveListing listing, string path, string newName, string? password = null,
        CancellationToken cancellationToken = default, string? sevenZip = null)
    {
        RequireEditable(listing);
        path = ArchivePaths.Normalize(path);
        newName = ValidateName(newName);
        var target = Join(ArchivePaths.ParentOf(path), newName);
        if (string.Equals(target, path, StringComparison.Ordinal)) return;
        if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase)
            && listing.Entries.Any(e => string.Equals(e.Path, target, StringComparison.OrdinalIgnoreCase) || ArchivePaths.IsUnder(e.Path, target)))
            throw new ArgumentException($"There is already something called {newName} there.");

        var pairs = Expand(listing, new[] { path })
            .Select(old => (Old: old, New: target + old[path.Length..]))
            .ToList();
        if (pairs.Count == 0) return;

        // 7za takes the pairs as arguments; long folders are split so no command line passes Windows' limit.
        var exe = sevenZip ?? SevenZipCli.DefaultPath;
        var batch = new List<string>();
        int length = 0;
        void Flush()
        {
            if (batch.Count == 0) return;
            SevenZipCli.Execute(exe, Path.GetTempPath(), [.. Switches("rn", listing, password), listing.ArchivePath, .. batch], password, cancellationToken);
            batch.Clear();
            length = 0;
        }
        foreach (var (old, @new) in pairs)
        {
            if (length > 20_000) Flush();
            batch.Add(Native(old));
            batch.Add(Native(@new));
            length += old.Length + @new.Length + 6;
        }
        Flush();
    }

    /// <summary>Creates an empty folder <paramref name="name"/> inside <paramref name="parent"/> ("" for the top).</summary>
    public static string NewFolder(
        ArchiveListing listing, string parent, string name, string? password = null,
        CancellationToken cancellationToken = default, string? sevenZip = null)
    {
        RequireEditable(listing);
        name = ValidateName(name);
        var path = Join(ArchivePaths.Normalize(parent), name);
        if (listing.Entries.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase) || ArchivePaths.IsUnder(e.Path, path)))
            throw new ArgumentException($"There is already something called {name} there.");

        var stage = Path.Combine(Path.GetTempPath(), "Arca", "Stage", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(stage, Native(path)));
            var args = Switches("a", listing, password);
            WithListFile(new List<string> { Native(path) }, list =>
                SevenZipCli.Execute(sevenZip ?? SevenZipCli.DefaultPath, stage, [.. args, listing.ArchivePath, "@" + list], password, cancellationToken));
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return path;
    }

    /// <summary>
    /// Whether <paramref name="password"/> opens the archive's encrypted files, tried on the smallest one. Checked
    /// before adding, or the new files would be encrypted with a different password from the rest.
    /// </summary>
    public static bool PasswordOpens(ArchiveListing listing, string password)
    {
        var sample = listing.Entries
            .Where(e => e.IsEncrypted && !e.IsDirectory && e.UnsafeReason is null)
            .OrderBy(e => e.Size)
            .FirstOrDefault();
        if (sample is null) return true;
        var folder = Path.Combine(Path.GetTempPath(), "Arca", "Check", Guid.NewGuid().ToString("N"));
        try
        {
            var result = ArchiveService.Extract(listing.ArchivePath, folder,
                new ExtractOptions { Password = password, Only = new[] { sample.Path }, PropagateMarkOfTheWeb = false });
            return result.FilesExtracted == 1 && result.Skipped.Count == 0;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void RequireEditable(ArchiveListing listing)
    {
        if (WhyNotEditable(listing) is { } reason) throw new NotSupportedException(reason);
    }

    /// <summary>
    /// The switches every change uses. With a password, what is added is encrypted the way the archive already is:
    /// AES-256 for ZIP, and for a 7z hidden names too when its names were hidden already.
    /// </summary>
    private static List<string> Switches(string command, ArchiveListing listing, string? password)
    {
        // The type is named rather than guessed from the extension, which may not match what the file really is.
        var type = listing.Format switch { ArchiveFormat.Zip => "-tzip", ArchiveFormat.SevenZip => "-t7z", _ => "-ttar" };
        var args = new List<string> { command, type, "-scsUTF-8", "-sccUTF-8", "-spd", "-bso0", "-bsp1", "-bse2", "-y" };
        if (!string.IsNullOrEmpty(password))
        {
            args.Add("-p");
            if (command == "a")
            {
                if (listing.Format == ArchiveFormat.Zip) args.Add("-mem=AES256");
                else if (listing.Format == ArchiveFormat.SevenZip && NamesHidden(listing)) args.Add("-mhe=on");
            }
        }
        return args;
    }

    private static bool NamesHidden(ArchiveListing listing)
    {
        try
        {
            ArchiveService.List(listing.ArchivePath);
            return false;
        }
        catch (PasswordRequiredException)
        {
            return true;
        }
    }

    /// <summary>The entries <paramref name="paths"/> name, each folder followed by everything under it, as listed.</summary>
    private static IEnumerable<string> Expand(ArchiveListing listing, IEnumerable<string> paths)
    {
        var wanted = paths.Select(ArchivePaths.Normalize).Where(p => p.Length > 0).ToList();
        return listing.Entries
            .Select(e => e.Path)
            .Where(p => wanted.Any(w => string.Equals(p, w, StringComparison.OrdinalIgnoreCase) || ArchivePaths.IsUnder(p, w)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    private static string Native(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static void WithListFile(List<string> lines, Action<string> run)
    {
        var listFile = Path.Combine(Path.GetTempPath(), "arca-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllLines(listFile, lines, new UTF8Encoding(false));
            run(listFile);
        }
        finally
        {
            try { File.Delete(listFile); } catch (IOException) { }
        }
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool TryHardLink(string link, string existing)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return CreateHardLink(link, existing, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}

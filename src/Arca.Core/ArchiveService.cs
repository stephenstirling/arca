using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace Arca.Core;

/// <summary>
/// Lists and extracts archives. Nothing the archive says is trusted: names go through <see cref="EntryPathGuard"/>,
/// sizes are counted from the bytes actually written (not the sizes in the headers), and the limits in
/// <see cref="SafetyLimits"/> stop an extraction that is turning into a zip bomb.
/// </summary>
public static class ArchiveService
{
    /// <summary>Reads the table of contents without extracting anything, and collects safety warnings.</summary>
    /// <exception cref="NotSupportedException">The file isn't an archive Arca can read (the message says why).</exception>
    public static ArchiveListing List(string archivePath, string? password = null, SafetyLimits? limits = null)
    {
        limits ??= SafetyLimits.Default;
        var format = RequireReadable(archivePath);
        var archiveLength = new FileInfo(archivePath).Length;

        using var archive = ArchiveFactory.Open(archivePath, new ReaderOptions { Password = password });
        var entries = new List<ArchiveEntryInfo>();
        var warnings = new List<ArchiveWarning>();
        bool tooMany = false;

        foreach (var e in archive.Entries)
        {
            if (entries.Count >= limits.MaxEntries) { tooMany = true; break; }
            var name = EntryName(e, archivePath);
            var unsafeReason = EntryPathGuard.CheckName(name, out _);
            entries.Add(new ArchiveEntryInfo(name, e.IsDirectory, e.Size, e.CompressedSize, e.IsEncrypted, e.LastModifiedTime, unsafeReason));
        }

        if (tooMany)
            warnings.Add(new ArchiveWarning("too-many-entries", $"This archive holds more than {limits.MaxEntries:N0} entries. Only the first {limits.MaxEntries:N0} are shown, and extracting it will stop at that limit."));

        var unsafeEntries = entries.Where(e => e.UnsafeReason is not null).ToList();
        if (unsafeEntries.Count > 0)
            warnings.Add(new ArchiveWarning("unsafe-path",
                $"{unsafeEntries.Count:N0} {(unsafeEntries.Count == 1 ? "entry has" : "entries have")} an unsafe name and will not be extracted. For example \"{unsafeEntries[0].Path}\": {unsafeEntries[0].UnsafeReason}."));

        int encrypted = entries.Count(e => e.IsEncrypted);
        if (encrypted > 0)
            warnings.Add(new ArchiveWarning("encrypted", $"{encrypted:N0} {(encrypted == 1 ? "entry is" : "entries are")} password-protected."));

        long declaredTotal = entries.Where(e => !e.IsDirectory).Sum(e => Math.Max(0, e.Size));
        if (declaredTotal > limits.MaxTotalBytes)
            warnings.Add(new ArchiveWarning("too-large", $"This archive says it unpacks to {FormatBytes(declaredTotal)}, more than the {FormatBytes(limits.MaxTotalBytes)} limit."));
        else if (declaredTotal >= limits.MinBytesForRatioCheck && archiveLength > 0 && (double)declaredTotal / archiveLength > limits.MaxRatio)
            warnings.Add(new ArchiveWarning("high-ratio", $"This {FormatBytes(archiveLength)} archive says it unpacks to {FormatBytes(declaredTotal)}. That is a typical zip bomb pattern."));

        return new ArchiveListing
        {
            ArchivePath = archivePath,
            Format = format,
            Entries = entries,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Extracts the archive into <paramref name="destinationFolder"/> (or a new folder inside it, see <see cref="ExtractOptions.SmartFolder"/>).
    /// Unsafe or password-protected entries are skipped and reported in the result, not thrown.
    /// A safety limit stops the run and sets <see cref="ExtractResult.AbortReason"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">The file isn't an archive Arca can read.</exception>
    /// <exception cref="OperationCanceledException">Cancelled through <paramref name="cancellationToken"/>.</exception>
    public static ExtractResult Extract(
        string archivePath,
        string destinationFolder,
        ExtractOptions? options = null,
        IProgress<ExtractProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExtractOptions();
        var limits = options.Limits;
        RequireReadable(archivePath);

        var archiveLength = new FileInfo(archivePath).Length;
        var mark = options.PropagateMarkOfTheWeb ? MarkOfTheWeb.Read(archivePath) : null;
        var readerOptions = new ReaderOptions { Password = options.Password };

        var root = Path.GetFullPath(destinationFolder);
        if (options.SmartFolder && NeedsWrapperFolder(archivePath, readerOptions))
            root = UniquePath(Path.Combine(root, WrapperFolderName(archivePath)));
        Directory.CreateDirectory(root);

        var result = new ExtractResult { Destination = root };
        var buffer = new byte[81920];
        int seen = 0;

        // Returns false to stop the whole run.
        bool Handle(IEntry entry, Func<Stream> open)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++seen > limits.MaxEntries)
            {
                result.AbortReason = $"Stopped: the archive holds more than {limits.MaxEntries:N0} entries.";
                return false;
            }

            var name = EntryName(entry, archivePath);
            if (!EntryPathGuard.TryResolve(root, name, out var target, out var problem))
            {
                result.Skipped.Add(new SkippedEntry(name, problem));
                return true;
            }

            if (entry.IsDirectory)
            {
                try { Directory.CreateDirectory(target); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Skipped.Add(new SkippedEntry(name, ex.Message));
                }
                return true;
            }

            if (entry.IsEncrypted && string.IsNullOrEmpty(options.Password))
            {
                result.Skipped.Add(new SkippedEntry(name, "it is password-protected and no password was given"));
                return true;
            }

            if (File.Exists(target))
            {
                if (options.Overwrite == OverwriteMode.Skip)
                {
                    result.Skipped.Add(new SkippedEntry(name, "a file with that name already exists"));
                    return true;
                }
                if (options.Overwrite == OverwriteMode.Rename) target = UniquePath(target);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                bool aborted = false;
                using (var input = open())
                using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Count what is really coming out, whatever the headers claimed.
                        long next = result.BytesWritten + n;
                        if (next > limits.MaxTotalBytes)
                        {
                            result.AbortReason = $"Stopped: the archive unpacks to more than {FormatBytes(limits.MaxTotalBytes)}.";
                            aborted = true;
                            break;
                        }
                        if (next >= limits.MinBytesForRatioCheck && archiveLength > 0 && (double)next / archiveLength > limits.MaxRatio)
                        {
                            result.AbortReason = $"Stopped: the output is more than {limits.MaxRatio:N0} times the size of the archive, which looks like a zip bomb.";
                            aborted = true;
                            break;
                        }

                        output.Write(buffer, 0, n);
                        result.BytesWritten = next;
                    }
                }

                if (aborted)
                {
                    TryDelete(target); // don't leave the half-written file behind
                    return false;
                }

                if (entry.LastModifiedTime is DateTime modified)
                {
                    try { File.SetLastWriteTime(target, modified); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
                }
                if (mark is not null) MarkOfTheWeb.Apply(target, mark);

                result.FilesExtracted++;
                progress?.Report(new ExtractProgress(name, result.FilesExtracted, result.BytesWritten));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(target);
                result.Skipped.Add(new SkippedEntry(name, ex.Message));
            }
            return true;
        }

        using var archive = ArchiveFactory.Open(archivePath, readerOptions);
        if (archive.IsSolid || archive.Type == ArchiveType.Rar)
        {
            // Solid archives must be read front to back; jumping to single entries would re-unpack everything before them.
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                if (!Handle(entry, () => reader.OpenEntryStream())) break;
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                var current = entry;
                if (!Handle(current, () => current.OpenEntryStream())) break;
            }
        }

        return result;
    }

    private static ArchiveFormat RequireReadable(string archivePath)
    {
        var format = FormatDetector.DetectFile(archivePath);
        if (format == ArchiveFormat.Unknown)
            throw new NotSupportedException("Arca doesn't recognise this file as an archive.");
        if (!ArchiveFormats.CanRead(format))
            throw new NotSupportedException($"{ArchiveFormats.DisplayName(format)} archives aren't supported yet.");
        return format;
    }

    /// <summary>Single-file formats like .gz can come without a stored name; use the archive's own name minus its extension.</summary>
    private static string EntryName(IEntry entry, string archivePath)
    {
        var key = entry.Key;
        return string.IsNullOrEmpty(key) ? Path.GetFileNameWithoutExtension(archivePath) : key;
    }

    private static bool NeedsWrapperFolder(string archivePath, ReaderOptions readerOptions)
    {
        using var archive = ArchiveFactory.Open(archivePath, readerOptions);
        var topLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var key = entry.Key;
            if (string.IsNullOrEmpty(key)) continue;
            var first = key.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is null || first == ".") continue;
            topLevel.Add(first);
            if (topLevel.Count > 1) return true;
        }
        return false;
    }

    private static string WrapperFolderName(string archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        return name.Length == 0 ? "Extracted" : name;
    }

    /// <summary>"name.txt" becomes "name (1).txt", "name (2).txt", ... until nothing has that name.</summary>
    internal static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (int i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Couldn't find a free file name.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

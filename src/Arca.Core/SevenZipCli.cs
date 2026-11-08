using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Arca.Core;

/// <summary>
/// Makes .7z archives and password-protected (AES-256) archives with the 7za.exe that ships next to Arca
/// (see third-party/7-Zip/README.md), which SharpCompress cannot write. Only ever used for creating, never reading.
/// </summary>
internal static class SevenZipCli
{
    /// <summary>Where the release puts 7za.exe: a "7-Zip" folder beside the program.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "7-Zip", "7za.exe");

    private static readonly Regex Percent = new(@"(\d{1,3})%(?:\s+\d+)?(?:\s+\+\s+(?<name>[^\r\n\b]+))?", RegexOptions.CultureInvariant);

    /// <summary>
    /// Writes the files in <paramref name="plan"/> to <paramref name="archive"/> (a new file), each under its entry name.
    /// The plan's files, never the folders, are passed in, so links that <see cref="ArchiveWriter.Plan"/> left out stay out.
    /// </summary>
    public static void Create(
        string exe,
        string archive,
        CreateOptions options,
        IReadOnlyList<(string File, string EntryName)> plan,
        CreateResult result,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(exe))
            throw new IOException("Arca's copy of 7-Zip (7-Zip\\7za.exe) is missing, so it can't make 7z or password-protected archives. Reinstalling Arca from Integra puts it back.");
        if (plan.Count == 0)
            throw new IOException("There are no files to add.");

        long totalBytes = plan.Sum(p => SafeLength(p.File));

        // 7za stores each file under its path relative to the folder it runs in, so run it once per source folder.
        foreach (var group in GroupByRoot(plan))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listFile = Path.Combine(Path.GetTempPath(), "arca-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllLines(listFile, group.Relative, new UTF8Encoding(false));
                Run(exe, group.Root, Arguments(archive, options, listFile), options.Password, totalBytes, result, progress, cancellationToken);
            }
            finally
            {
                try { File.Delete(listFile); } catch (IOException) { }
            }
        }

        var skippedPaths = result.Skipped.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, _) in plan)
        {
            if (skippedPaths.Contains(file)) continue;
            result.FilesAdded++;
            result.BytesAdded += SafeLength(file);
        }
    }

    internal static List<string> Arguments(string archive, CreateOptions options, string listFile)
    {
        var args = new List<string>
        {
            "a",
            options.Format == CreateFormat.SevenZip ? "-t7z" : "-tzip",
            "-mx=" + options.Level switch { PackLevel.Store => "0", PackLevel.Fast => "1", PackLevel.Maximum => "9", _ => "5" },
            "-scsUTF-8",   // the list file is UTF-8
            "-sccUTF-8",   // and so is what goes through stdin and stdout, the password included
            "-spd",        // file names are names, never wildcards
            "-ssw",        // add files another program has open, as the ZIP writer does
            "-bso0", "-bsp1", "-bse2",
            "-y",
        };
        if (!string.IsNullOrEmpty(options.Password))
        {
            // A bare -p makes 7za read the password from stdin, so it never appears on a command line other programs can see.
            args.Add("-p");
            args.Add(options.Format == CreateFormat.SevenZip ? "-mhe=on" : "-mem=AES256");
        }
        args.Add("--");
        args.Add(archive);
        args.Add("@" + listFile);
        return args;
    }

    private static void Run(
        string exe, string workingDirectory, List<string> args, string? password, long totalBytes,
        CreateResult result, IProgress<ExtractProgress>? progress, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) start.ArgumentList.Add(a);

        using var process = Process.Start(start) ?? throw new IOException("7-Zip didn't start.");
        using var kill = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        });

        // "\n" alone: 7za ends the password at the newline, and a "\r" before it would become part of the password if stdin isn't in text mode.
        if (!string.IsNullOrEmpty(password)) process.StandardInput.Write(password + "\n");
        process.StandardInput.Close();

        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var buffer = new char[512];
        int n;
        while ((n = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
        {
            foreach (Match m in Percent.Matches(new string(buffer, 0, n)))
            {
                var pct = Math.Clamp(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 0, 100);
                var name = m.Groups["name"].Success ? m.Groups["name"].Value.Trim() : "";
                progress?.Report(new ExtractProgress(name, 0, totalBytes * pct / 100));
            }
        }
        process.WaitForExit();
        var stderr = errors.GetAwaiter().GetResult().Trim();

        cancellationToken.ThrowIfCancellationRequested();
        switch (process.ExitCode)
        {
            case 0:
                return;
            case 1:
                // A warning: some files couldn't be read (locked, gone). The archive is complete without them.
                foreach (var line in stderr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                {
                    var path = Path.IsPathRooted(line) ? line : Path.Combine(workingDirectory, line);
                    if (File.Exists(path) || Directory.Exists(path)) result.Skipped.Add(new SkippedEntry(path, FirstWarning(stderr)));
                }
                if (result.Skipped.Count == 0) result.Skipped.Add(new SkippedEntry(workingDirectory, FirstWarning(stderr)));
                return;
            case 8:
                throw new IOException("7-Zip ran out of memory. Try a lower compression level.");
            default:
                throw new IOException("7-Zip couldn't create the archive" + (stderr.Length > 0 ? ": " + stderr : $" (exit code {process.ExitCode})."));
        }
    }

    private static string FirstWarning(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase));
        return line is null ? "7-Zip couldn't read it" : line["WARNING".Length..].TrimStart(':', ' ');
    }

    /// <summary>Each file's folder outside the archive, and its path relative to that folder (which is its entry name).</summary>
    internal static List<(string Root, List<string> Relative)> GroupByRoot(IReadOnlyList<(string File, string EntryName)> plan)
    {
        var groups = new List<(string Root, List<string> Relative)>();
        foreach (var (file, entryName) in plan)
        {
            var relative = entryName.Replace('/', Path.DirectorySeparatorChar);
            if (!file.EndsWith(Path.DirectorySeparatorChar + relative, StringComparison.OrdinalIgnoreCase))
            {
                var top = entryName.Split('/')[0];
                throw new IOException($"Two of the chosen items have the same name, so the second was renamed to \"{top}\" inside the archive. " +
                    "7z and password-protected archives can't rename items while adding them: rename one of them first, or make a ZIP without a password.");
            }
            // Keeps its trailing separator, so a drive root stays "C:\" rather than "C:", which means "wherever C: last was".
            var root = file[..^relative.Length];
            var group = groups.FirstOrDefault(g => string.Equals(g.Root, root, StringComparison.OrdinalIgnoreCase));
            if (group.Relative is null)
            {
                group = (root, new List<string>());
                groups.Add(group);
            }
            group.Relative.Add(relative);
        }
        return groups;
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}

using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Tar;
using SharpCompress.Writers.Zip;

namespace Arca.Core;

/// <summary>The kinds of archive Arca can make.</summary>
public enum CreateFormat
{
    Zip,
    Tar,
    TarGz,
    TarBz2,
}

public enum PackLevel
{
    /// <summary>No compression: fastest, biggest. Good for photos and videos, which are already compressed.</summary>
    Store,
    Fast,
    Normal,
    Maximum,
}

public sealed record CreateOptions
{
    public CreateFormat Format { get; init; } = CreateFormat.Zip;
    public PackLevel Level { get; init; } = PackLevel.Normal;

    /// <summary>Replace an existing file at the archive path. Off by default: creating never destroys anything.</summary>
    public bool Overwrite { get; init; }
}

public sealed class CreateResult
{
    public string ArchivePath { get; init; } = "";
    public int FilesAdded { get; set; }
    public long BytesAdded { get; set; }
    public List<SkippedEntry> Skipped { get; } = new();
}

/// <summary>
/// Makes new archives from files and folders. The archive is written to a temporary file next to its final
/// place and only renamed into place once complete, so a failure or a cancel never leaves a half-written archive.
/// Links (shortcuts to folders, junctions, symlinks) are skipped, not followed, so a folder can't pull in
/// files from elsewhere on the PC or loop forever.
/// </summary>
public static class ArchiveWriter
{
    public static string Extension(CreateFormat format) => format switch
    {
        CreateFormat.Tar => ".tar",
        CreateFormat.TarGz => ".tar.gz",
        CreateFormat.TarBz2 => ".tar.bz2",
        _ => ".zip",
    };

    public static string DisplayName(CreateFormat format) => format switch
    {
        CreateFormat.Tar => "TAR",
        CreateFormat.TarGz => "TAR.GZ",
        CreateFormat.TarBz2 => "TAR.BZ2",
        _ => "ZIP",
    };

    /// <summary>
    /// A sensible archive name for these items: the single item's name, or the folder they share
    /// ("Photos" for two files in C:\Photos), plus the format's extension.
    /// </summary>
    public static string SuggestName(IReadOnlyList<string> sources, CreateFormat format)
    {
        string stem;
        if (sources.Count == 1)
        {
            var only = Path.TrimEndingDirectorySeparator(sources[0]);
            stem = Directory.Exists(only) ? Path.GetFileName(only) : Path.GetFileNameWithoutExtension(only);
        }
        else
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sources[0]));
            stem = string.IsNullOrEmpty(parent) ? "Archive" : Path.GetFileName(parent);
        }
        if (string.IsNullOrWhiteSpace(stem)) stem = "Archive";
        return stem + Extension(format);
    }

    /// <summary>
    /// Creates <paramref name="archivePath"/> from <paramref name="sources"/> (files and folders). Each item goes in under its
    /// own name, folders with everything inside them. Unreadable files and links are skipped and listed in the result.
    /// </summary>
    /// <exception cref="IOException">The archive already exists (and <see cref="CreateOptions.Overwrite"/> is off), or can't be written.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; nothing is left behind.</exception>
    public static CreateResult Create(
        string archivePath,
        IReadOnlyList<string> sources,
        CreateOptions? options = null,
        IProgress<ExtractProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CreateOptions();
        if (sources.Count == 0) throw new ArgumentException("Choose at least one file or folder.", nameof(sources));

        var target = Path.GetFullPath(archivePath);
        if (File.Exists(target) && !options.Overwrite)
            throw new IOException($"{Path.GetFileName(target)} already exists.");
        if (Directory.Exists(target))
            throw new IOException($"There is a folder called {Path.GetFileName(target)} there.");

        var plan = Plan(sources, target, cancellationToken, out var skipped);
        var result = new CreateResult { ArchivePath = target };
        result.Skipped.AddRange(skipped);

        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N")[..8] + ".arca-tmp");

        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = OpenWriter(output, options))
            {
                foreach (var (file, entryName) in plan)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileStream input;
                    FileInfo info;
                    try
                    {
                        info = new FileInfo(file);
                        input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Locked or no permission: nothing was written for it yet, so the archive stays whole.
                        result.Skipped.Add(new SkippedEntry(file, ex.Message));
                        continue;
                    }

                    // A read failing part-way through would leave a broken entry, so that stops the whole run
                    // (and the temporary file is removed) instead of being skipped.
                    using (input)
                        writer.Write(entryName, input, info.LastWriteTime);
                    result.FilesAdded++;
                    result.BytesAdded += info.Length;
                    progress?.Report(new ExtractProgress(entryName, result.FilesAdded, result.BytesAdded));
                }
            }

            File.Move(temp, target, overwrite: options.Overwrite);
            return result;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Works out every file to add and the name it gets inside the archive. Nothing is opened yet.</summary>
    internal static List<(string File, string EntryName)> Plan(
        IReadOnlyList<string> sources, string archivePath, CancellationToken cancellationToken, out List<SkippedEntry> skipped)
    {
        skipped = new List<SkippedEntry>();
        var plan = new List<(string, string)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archiveFull = Path.GetFullPath(archivePath);

        foreach (var raw in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));

            if (IsLink(source))
            {
                skipped.Add(new SkippedEntry(source, "it is a link (shortcut, junction or symlink), so it isn't followed"));
                continue;
            }

            if (File.Exists(source))
            {
                if (string.Equals(source, archiveFull, StringComparison.OrdinalIgnoreCase)) continue;
                plan.Add((source, Unique(Path.GetFileName(source), usedNames)));
            }
            else if (Directory.Exists(source))
            {
                var top = Unique(Path.GetFileName(source), usedNames);
                AddFolder(source, top, plan, skipped, archiveFull, cancellationToken);
            }
            else
            {
                skipped.Add(new SkippedEntry(source, "it no longer exists"));
            }
        }
        return plan;
    }

    private static void AddFolder(
        string folder, string entryPrefix, List<(string, string)> plan, List<SkippedEntry> skipped, string archiveFull, CancellationToken ct)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateFileSystemEntries(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped.Add(new SkippedEntry(folder, ex.Message));
            return;
        }

        foreach (var child in children)
        {
            ct.ThrowIfCancellationRequested();
            var name = entryPrefix + "/" + Path.GetFileName(child);
            if (IsLink(child))
            {
                skipped.Add(new SkippedEntry(child, "it is a link (shortcut, junction or symlink), so it isn't followed"));
            }
            else if (Directory.Exists(child))
            {
                AddFolder(child, name, plan, skipped, archiveFull, ct);
            }
            else if (!string.Equals(Path.GetFullPath(child), archiveFull, StringComparison.OrdinalIgnoreCase))
            {
                plan.Add((child, name));
            }
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            // LinkTarget is set for symlinks and junctions, but not for OneDrive's cloud files and folders,
            // which are also "reparse points" and must still be added.
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // the caller finds out when it tries to read it
        }
    }

    /// <summary>"a.txt", then "a (1).txt", "a (2).txt"... when two chosen items share a name.</summary>
    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static IWriter OpenWriter(Stream output, CreateOptions options)
    {
        if (options.Format == CreateFormat.Zip)
        {
            var zip = new ZipWriterOptions(options.Level == PackLevel.Store ? CompressionType.None : CompressionType.Deflate)
            {
                UseZip64 = true,
                DeflateCompressionLevel = options.Level switch
                {
                    PackLevel.Fast => SharpCompress.Compressors.Deflate.CompressionLevel.BestSpeed,
                    PackLevel.Maximum => SharpCompress.Compressors.Deflate.CompressionLevel.BestCompression,
                    _ => SharpCompress.Compressors.Deflate.CompressionLevel.Default,
                },
            };
            return new ZipWriter(output, zip);
        }

        var compression = options.Format switch
        {
            CreateFormat.TarGz => CompressionType.GZip,
            CreateFormat.TarBz2 => CompressionType.BZip2,
            _ => CompressionType.None,
        };
        return new TarWriter(output, new TarWriterOptions(compression, finalizeArchiveOnClose: true));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

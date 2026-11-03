namespace Arca.Core;

/// <summary>Ceilings that stop a "zip bomb" (a tiny file that unpacks into terabytes) from filling the disk.</summary>
public sealed record SafetyLimits
{
    public static SafetyLimits Default { get; } = new();

    /// <summary>Most bytes one extraction may write. Default 20 GiB.</summary>
    public long MaxTotalBytes { get; init; } = 20L * 1024 * 1024 * 1024;

    /// <summary>Most entries one archive may hold. Default 1,000,000.</summary>
    public int MaxEntries { get; init; } = 1_000_000;

    /// <summary>Output-to-archive-size ratio that counts as suspicious (default 1000:1), checked only once <see cref="MinBytesForRatioCheck"/> has been written.</summary>
    public double MaxRatio { get; init; } = 1000;

    /// <summary>Small highly-compressible files (a big text file of zeros) are fine; only check the ratio past this many bytes. Default 256 MiB.</summary>
    public long MinBytesForRatioCheck { get; init; } = 256L * 1024 * 1024;
}

public enum OverwriteMode
{
    /// <summary>Keep the existing file and write the new one as "name (1).ext".</summary>
    Rename,
    /// <summary>Leave the existing file alone and don't extract that entry.</summary>
    Skip,
    /// <summary>Replace the existing file.</summary>
    Overwrite,
}

public sealed record ExtractOptions
{
    public SafetyLimits Limits { get; init; } = SafetyLimits.Default;

    /// <summary>When the archive has several top-level items, put them in a new folder named after the archive. A single top-level item is extracted as it is.</summary>
    public bool SmartFolder { get; init; } = true;

    public OverwriteMode Overwrite { get; init; } = OverwriteMode.Rename;

    /// <summary>Copy the archive's "downloaded from the internet" mark (Mark-of-the-Web) onto every extracted file, as Windows' own extractor does.</summary>
    public bool PropagateMarkOfTheWeb { get; init; } = true;

    public string? Password { get; init; }
}

public sealed record ArchiveEntryInfo(
    string Path,
    bool IsDirectory,
    long Size,
    long CompressedSize,
    bool IsEncrypted,
    DateTime? LastModified,
    string? UnsafeReason);

public sealed record ArchiveWarning(string Kind, string Message);

public sealed class ArchiveListing
{
    public required string ArchivePath { get; init; }
    public required ArchiveFormat Format { get; init; }
    public required IReadOnlyList<ArchiveEntryInfo> Entries { get; init; }
    public required IReadOnlyList<ArchiveWarning> Warnings { get; init; }

    public long TotalSize => Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
    public int FileCount => Entries.Count(e => !e.IsDirectory);
}

public sealed record SkippedEntry(string Path, string Reason);

public sealed record ExtractProgress(string CurrentEntry, int FilesExtracted, long BytesWritten);

public sealed class ExtractResult
{
    /// <summary>The folder the files went into (the chosen folder, or the new smart folder inside it).</summary>
    public string Destination { get; init; } = "";
    public int FilesExtracted { get; set; }
    public long BytesWritten { get; set; }
    public List<SkippedEntry> Skipped { get; } = new();

    /// <summary>Set when extraction stopped early (a safety limit was hit). Files already written stay where they are.</summary>
    public string? AbortReason { get; set; }

    public bool Completed => AbortReason is null;
}

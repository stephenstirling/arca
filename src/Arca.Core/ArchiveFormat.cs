namespace Arca.Core;

/// <summary>The archive and compression formats Arca recognises by looking at the file's first bytes.</summary>
public enum ArchiveFormat
{
    Unknown,
    Zip,
    SevenZip,
    Rar4,
    Rar5,
    Tar,
    Gzip,
    Bzip2,
    Xz,
    Zstd,
    Cab,
    Iso,
}

public static class ArchiveFormats
{
    /// <summary>True for the formats the reader in this build can open. The rest are recognised so Arca can say so plainly.</summary>
    public static bool CanRead(ArchiveFormat format) => format is
        ArchiveFormat.Zip or ArchiveFormat.SevenZip or ArchiveFormat.Rar4 or ArchiveFormat.Rar5 or
        ArchiveFormat.Tar or ArchiveFormat.Gzip or ArchiveFormat.Bzip2 or ArchiveFormat.Xz;

    public static string DisplayName(ArchiveFormat format) => format switch
    {
        ArchiveFormat.Zip => "ZIP",
        ArchiveFormat.SevenZip => "7z",
        ArchiveFormat.Rar4 => "RAR",
        ArchiveFormat.Rar5 => "RAR5",
        ArchiveFormat.Tar => "TAR",
        ArchiveFormat.Gzip => "GZip",
        ArchiveFormat.Bzip2 => "BZip2",
        ArchiveFormat.Xz => "XZ",
        ArchiveFormat.Zstd => "Zstandard",
        ArchiveFormat.Cab => "CAB",
        ArchiveFormat.Iso => "ISO",
        _ => "unknown",
    };
}

/// <summary>Works out the format from the file's content, not its extension (an extension is only a last resort for plain TAR).</summary>
public static class FormatDetector
{
    public static ArchiveFormat DetectFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Detect(stream, path);
    }

    /// <summary>Reads from <paramref name="stream"/> (which must be seekable) and leaves its position unspecified.</summary>
    public static ArchiveFormat Detect(Stream stream, string? fileName = null)
    {
        var head = ReadAt(stream, 0, 512);

        if (StartsWith(head, 0x50, 0x4B, 0x03, 0x04) ||     // normal ZIP
            StartsWith(head, 0x50, 0x4B, 0x05, 0x06) ||     // empty ZIP
            StartsWith(head, 0x50, 0x4B, 0x07, 0x08))       // spanned ZIP
            return ArchiveFormat.Zip;
        if (StartsWith(head, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return ArchiveFormat.SevenZip;
        if (StartsWith(head, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00)) return ArchiveFormat.Rar5;
        if (StartsWith(head, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00)) return ArchiveFormat.Rar4;
        if (StartsWith(head, 0x1F, 0x8B)) return ArchiveFormat.Gzip;
        if (StartsWith(head, 0x42, 0x5A, 0x68)) return ArchiveFormat.Bzip2;
        if (StartsWith(head, 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00)) return ArchiveFormat.Xz;
        if (StartsWith(head, 0x28, 0xB5, 0x2F, 0xFD)) return ArchiveFormat.Zstd;
        if (StartsWith(head, 0x4D, 0x53, 0x43, 0x46)) return ArchiveFormat.Cab;   // "MSCF"

        // TAR has no magic at the start: "ustar" sits at byte 257 of the first header block.
        if (head.Length >= 262 && head[257] == 'u' && head[258] == 's' && head[259] == 't' && head[260] == 'a' && head[261] == 'r')
            return ArchiveFormat.Tar;

        // ISO 9660: "CD001" at byte 32769.
        var iso = ReadAt(stream, 32769, 5);
        if (iso.Length == 5 && iso[0] == 'C' && iso[1] == 'D' && iso[2] == '0' && iso[3] == '0' && iso[4] == '1')
            return ArchiveFormat.Iso;

        // Very old TAR files have no "ustar" marker; trust the extension for those only.
        if (fileName is not null && fileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            return ArchiveFormat.Tar;

        return ArchiveFormat.Unknown;
    }

    private static bool StartsWith(byte[] data, params byte[] prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i]) return false;
        return true;
    }

    /// <summary>Reads up to <paramref name="count"/> bytes at <paramref name="offset"/>; fewer (or none) near or past the end of the stream.</summary>
    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (!stream.CanSeek || offset >= stream.Length) return Array.Empty<byte>();
        stream.Position = offset;
        var buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        if (total == count) return buffer;
        var trimmed = new byte[total];
        Array.Copy(buffer, trimmed, total);
        return trimmed;
    }
}

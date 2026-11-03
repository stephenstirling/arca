using Arca.Core;
using Xunit;

namespace Arca.Core.Tests;

public class FormatDetectorTests
{
    private static ArchiveFormat Detect(byte[] bytes, string? name = null)
    {
        using var stream = new MemoryStream(bytes);
        return FormatDetector.Detect(stream, name);
    }

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0, 0 }, ArchiveFormat.Zip)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x05, 0x06, 0, 0 }, ArchiveFormat.Zip)]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4 }, ArchiveFormat.SevenZip)]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0xCF }, ArchiveFormat.Rar4)]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, ArchiveFormat.Rar5)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08, 0 }, ArchiveFormat.Gzip)]
    [InlineData(new byte[] { 0x42, 0x5A, 0x68, 0x39 }, ArchiveFormat.Bzip2)]
    [InlineData(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, ArchiveFormat.Xz)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0 }, ArchiveFormat.Zstd)]
    [InlineData(new byte[] { 0x4D, 0x53, 0x43, 0x46, 0 }, ArchiveFormat.Cab)]
    public void Recognises_formats_by_their_first_bytes(byte[] bytes, ArchiveFormat expected)
    {
        Assert.Equal(expected, Detect(bytes));
    }

    [Fact]
    public void Recognises_tar_by_the_ustar_marker()
    {
        var bytes = new byte[512];
        "ustar"u8.CopyTo(bytes.AsSpan(257));
        Assert.Equal(ArchiveFormat.Tar, Detect(bytes));
    }

    [Fact]
    public void Recognises_iso_by_its_volume_descriptor()
    {
        var bytes = new byte[33_000];
        "CD001"u8.CopyTo(bytes.AsSpan(32769));
        Assert.Equal(ArchiveFormat.Iso, Detect(bytes));
    }

    [Fact]
    public void Trusts_content_over_extension()
    {
        // A ZIP renamed to .rar is still a ZIP.
        Assert.Equal(ArchiveFormat.Zip, Detect(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "photos.rar"));
    }

    [Fact]
    public void Old_tar_without_marker_falls_back_to_extension()
    {
        Assert.Equal(ArchiveFormat.Tar, Detect(new byte[600], "old.tar"));
    }

    [Fact]
    public void Plain_data_and_empty_files_are_unknown()
    {
        Assert.Equal(ArchiveFormat.Unknown, Detect(new byte[] { 1, 2, 3, 4, 5 }, "notes.txt"));
        Assert.Equal(ArchiveFormat.Unknown, Detect(Array.Empty<byte>()));
    }

    [Fact]
    public void Reader_support_matches_what_the_service_can_open()
    {
        Assert.True(ArchiveFormats.CanRead(ArchiveFormat.Rar5));
        Assert.True(ArchiveFormats.CanRead(ArchiveFormat.SevenZip));
        Assert.False(ArchiveFormats.CanRead(ArchiveFormat.Zstd));
        Assert.False(ArchiveFormats.CanRead(ArchiveFormat.Unknown));
    }
}

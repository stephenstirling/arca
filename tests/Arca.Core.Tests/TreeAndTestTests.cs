using System.IO.Compression;
using System.Text;
using Arca.Core;
using Xunit;

namespace Arca.Core.Tests;

public sealed class TreeAndTestTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "arca-tree-" + Guid.NewGuid().ToString("N"));

    public TreeAndTestTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private static ArchiveEntryInfo File(string path, long size, long packed = 0, string? unsafeReason = null, DateTime? modified = null) =>
        new(path, false, size, packed, false, modified, unsafeReason);

    private static ArchiveEntryInfo Dir(string path, DateTime? modified = null) =>
        new(path, true, 0, 0, false, modified, null);

    private string MakeZip(string fileName, CompressionLevel level, params (string Entry, string Text)[] files)
    {
        var path = Path.Combine(_work, fileName);
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryName, text) in files)
        {
            var entry = zip.CreateEntry(entryName, level);
            using var s = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(text);
            s.Write(bytes, 0, bytes.Length);
        }
        return path;
    }

    // ---- CRC-32 ----

    [Fact]
    public void Crc32_matches_the_standard_check_value()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    // ---- Paths ----

    [Theory]
    [InlineData("a\\b\\c.txt", "a/b/c.txt")]
    [InlineData("./a//b/", "a/b")]
    [InlineData(".", "")]
    [InlineData("", "")]
    [InlineData("../x", "../x")]   // left for the safety checks to refuse
    public void Normalize_cleans_separators(string input, string expected)
    {
        Assert.Equal(expected, ArchivePaths.Normalize(input));
    }

    [Fact]
    public void IsUnder_matches_folder_and_contents_but_not_lookalikes()
    {
        Assert.True(ArchivePaths.IsUnder("photos/a.jpg", "photos"));
        Assert.True(ArchivePaths.IsUnder("Photos", "photos"));
        Assert.True(ArchivePaths.IsUnder("anything", ""));
        Assert.False(ArchivePaths.IsUnder("photos-old/a.jpg", "photos"));
    }

    // ---- Tree ----

    [Fact]
    public void Builds_folders_from_flat_names_including_implied_ones()
    {
        var root = ArchiveTree.Build(new[]
        {
            File("readme.txt", 10, 5),
            File("photos/2026/a.jpg", 100, 90),
            File("photos/2026/b.jpg", 200, 180),
            File("photos\\cover.png", 50, 40),
        });

        Assert.Equal(2, root.Children.Count);
        var photos = root.Children.Single(c => c.IsFolder);
        Assert.Equal("photos", photos.Name);
        Assert.Equal(350, photos.Size);
        Assert.Equal(310, photos.PackedSize);
        Assert.Equal(3, photos.FileCount);
        Assert.Equal(4, root.FileCount);

        var year = ArchiveTree.FindFolder(root, "photos/2026");
        Assert.NotNull(year);
        Assert.Equal(2, year!.Children.Count);
        Assert.Equal("photos/2026/a.jpg", year.Children[0].FullPath);
        Assert.Same(photos, year.Parent);
    }

    [Fact]
    public void Explicit_folder_entries_keep_their_own_time_and_are_not_duplicated()
    {
        var folderTime = new DateTime(2020, 1, 1);
        var fileTime = new DateTime(2026, 5, 5);
        var root = ArchiveTree.Build(new[]
        {
            Dir("docs/", folderTime),
            File("docs/a.txt", 1, modified: fileTime),
            Dir("docs"),
        });

        var docs = Assert.Single(root.Children);
        Assert.Equal(folderTime, docs.Modified);
        Assert.Single(docs.Children);
    }

    [Fact]
    public void Implied_folders_take_the_newest_time_inside_them()
    {
        var root = ArchiveTree.Build(new[]
        {
            File("x/a.txt", 1, modified: new DateTime(2025, 1, 1)),
            File("x/b.txt", 1, modified: new DateTime(2026, 3, 3)),
        });
        Assert.Equal(new DateTime(2026, 3, 3), root.Children[0].Modified);
    }

    [Fact]
    public void Unsafe_entries_sit_at_the_top_under_their_raw_name()
    {
        var root = ArchiveTree.Build(new[]
        {
            File("ok/a.txt", 1),
            File("../../evil.dll", 5, unsafeReason: "it climbs out"),
        });

        var evil = root.Children.Single(c => c.UnsafeReason is not null);
        Assert.Equal("../../evil.dll", evil.Name);
        Assert.False(evil.IsFolder);
        Assert.Null(ArchiveTree.FindFolder(root, ".."));
    }

    [Fact]
    public void Tree_from_a_real_listing_has_crc_values()
    {
        var zip = MakeZip("c.zip", CompressionLevel.Optimal, ("a.txt", "123456789"));
        var root = ArchiveTree.Build(ArchiveService.List(zip).Entries);
        Assert.Equal(0xCBF43926L, root.Children[0].Crc);
    }

    // ---- Test ----

    [Fact]
    public void Test_passes_a_good_archive()
    {
        var zip = MakeZip("good.zip", CompressionLevel.Optimal, ("a.txt", "hello"), ("b/c.txt", "world"));

        var result = ArchiveService.Test(zip);

        Assert.True(result.AllGood);
        Assert.Equal(2, result.FilesOk);
        Assert.Equal(10, result.BytesRead);
    }

    [Fact]
    public void Test_finds_a_damaged_file()
    {
        // Stored (not compressed), so flipping a data byte leaves the archive readable but the checksum wrong.
        var zip = MakeZip("bad.zip", CompressionLevel.NoCompression, ("a.txt", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        var bytes = System.IO.File.ReadAllBytes(zip);
        int at = Encoding.ASCII.GetString(bytes).IndexOf("AAAAAAAA", StringComparison.Ordinal);
        Assert.True(at > 0);
        bytes[at + 3] = (byte)'B';
        System.IO.File.WriteAllBytes(zip, bytes);

        var result = ArchiveService.Test(zip);

        Assert.False(result.AllGood);
        Assert.Single(result.Failed);
        Assert.Equal("a.txt", result.Failed[0].Path);
    }

    [Fact]
    public void Test_stops_on_a_zip_bomb_ratio()
    {
        var path = Path.Combine(_work, "bomb.zip");
        using (var stream = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("zeros.bin", CompressionLevel.Optimal).Open();
            s.Write(new byte[2_000_000]);
        }

        var result = ArchiveService.Test(path, limits: new SafetyLimits { MaxRatio = 10, MinBytesForRatioCheck = 100_000 });

        Assert.NotNull(result.AbortReason);
        Assert.False(result.AllGood);
    }

    // ---- Extracting a selection ----

    [Fact]
    public void Only_extracts_the_selected_folder_keeping_its_path()
    {
        var zip = MakeZip("sel.zip", CompressionLevel.Optimal, ("a.txt", "A"), ("photos/b.jpg", "B"), ("photos-old/c.jpg", "C"));
        var output = Path.Combine(_work, "out1");

        var result = ArchiveService.Extract(zip, output, new ExtractOptions { Only = new[] { "photos" } });

        Assert.Equal(output, result.Destination);   // no smart folder for a selection
        Assert.Equal(1, result.FilesExtracted);
        Assert.True(System.IO.File.Exists(Path.Combine(output, "photos", "b.jpg")));
        Assert.False(System.IO.File.Exists(Path.Combine(output, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(output, "photos-old")));
    }

    [Fact]
    public void StripPrefix_drops_the_current_folder_from_the_front()
    {
        var zip = MakeZip("strip.zip", CompressionLevel.Optimal, ("photos/b.jpg", "B"), ("photos/sub/c.jpg", "C"));
        var output = Path.Combine(_work, "out2");

        ArchiveService.Extract(zip, output, new ExtractOptions { Only = new[] { "photos/b.jpg", "photos/sub" }, StripPrefix = "photos" });

        Assert.Equal("B", System.IO.File.ReadAllText(Path.Combine(output, "b.jpg")));
        Assert.Equal("C", System.IO.File.ReadAllText(Path.Combine(output, "sub", "c.jpg")));
        Assert.False(Directory.Exists(Path.Combine(output, "photos")));
    }

    [Fact]
    public void Listing_reports_archive_size_and_packed_total()
    {
        var zip = MakeZip("sizes.zip", CompressionLevel.Optimal, ("a.txt", new string('x', 1000)));

        var listing = ArchiveService.List(zip);

        Assert.Equal(new FileInfo(zip).Length, listing.ArchiveSize);
        Assert.Equal(1000, listing.TotalSize);
        Assert.True(listing.TotalPacked > 0 && listing.TotalPacked < 1000);
        Assert.False(listing.IsSolid);
    }
}

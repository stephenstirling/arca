using System.IO.Compression;
using System.Text;
using Arca.Core;
using Xunit;

namespace Arca.Core.Tests;

public sealed class ArchiveServiceTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "arca-tests-" + Guid.NewGuid().ToString("N"));

    public ArchiveServiceTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    /// <summary>Builds a real .zip with the given (entry name, content) pairs. Entry names are used exactly as given.</summary>
    private string MakeZip(string fileName, params (string Entry, byte[] Content)[] files)
    {
        var path = Path.Combine(_work, fileName);
        using var stream = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entryName, content) in files)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using var s = entry.Open();
            s.Write(content, 0, content.Length);
        }
        return path;
    }

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    private string OutDir(string name = "out")
    {
        var dir = Path.Combine(_work, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Lists_entries_and_sizes()
    {
        var zip = MakeZip("a.zip", ("one.txt", Text("hello")), ("sub/two.txt", Text("hello world")));

        var listing = ArchiveService.List(zip);

        Assert.Equal(ArchiveFormat.Zip, listing.Format);
        Assert.Equal(2, listing.FileCount);
        Assert.Equal(16, listing.TotalSize);
        Assert.Empty(listing.Warnings);
    }

    [Fact]
    public void Smart_extract_wraps_several_top_level_items_in_a_folder_named_after_the_archive()
    {
        var zip = MakeZip("photos.zip", ("a.txt", Text("A")), ("b.txt", Text("B")));
        var output = OutDir();

        var result = ArchiveService.Extract(zip, output);

        Assert.True(result.Completed);
        Assert.Equal(Path.Combine(output, "photos"), result.Destination);
        Assert.Equal("A", File.ReadAllText(Path.Combine(output, "photos", "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(output, "photos", "b.txt")));
        Assert.Equal(2, result.FilesExtracted);
    }

    [Fact]
    public void Smart_extract_leaves_a_single_top_level_folder_alone()
    {
        var zip = MakeZip("project.zip", ("proj/a.txt", Text("A")), ("proj/b.txt", Text("B")));
        var output = OutDir();

        var result = ArchiveService.Extract(zip, output);

        Assert.Equal(output, result.Destination);
        Assert.True(File.Exists(Path.Combine(output, "proj", "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(output, "project")));
    }

    [Fact]
    public void Path_traversal_entries_are_skipped_and_reported_and_nothing_escapes()
    {
        var zip = MakeZip("slip.zip",
            ("ok.txt", Text("fine")),
            ("../evil.txt", Text("bad")),
            ("..\\evil2.txt", Text("bad")));
        var output = OutDir();

        var result = ArchiveService.Extract(zip, output, new ExtractOptions { SmartFolder = false });

        Assert.True(result.Completed);
        Assert.Equal(1, result.FilesExtracted);
        Assert.Equal(2, result.Skipped.Count);
        Assert.True(File.Exists(Path.Combine(output, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(_work, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_work, "evil2.txt")));
        Assert.False(File.Exists(Path.Combine(output, "evil.txt")));
    }

    [Fact]
    public void Listing_warns_about_unsafe_names()
    {
        var zip = MakeZip("slip.zip", ("ok.txt", Text("fine")), ("../evil.txt", Text("bad")));

        var listing = ArchiveService.List(zip);

        Assert.Contains(listing.Warnings, w => w.Kind == "unsafe-path");
        Assert.Contains(listing.Entries, e => e.Path == "../evil.txt" && e.UnsafeReason is not null);
    }

    [Fact]
    public void Stops_when_the_output_passes_the_total_size_limit_and_removes_the_partial_file()
    {
        var zip = MakeZip("big.zip", ("big.bin", new byte[100_000]));
        var output = OutDir();
        var options = new ExtractOptions { SmartFolder = false, Limits = new SafetyLimits { MaxTotalBytes = 1_000 } };

        var result = ArchiveService.Extract(zip, output, options);

        Assert.False(result.Completed);
        Assert.NotNull(result.AbortReason);
        Assert.False(File.Exists(Path.Combine(output, "big.bin")));
        Assert.Equal(0, result.FilesExtracted);
    }

    [Fact]
    public void Stops_on_a_zip_bomb_ratio_even_when_the_total_is_under_the_size_limit()
    {
        // 2 MB of zeros compresses to a few KB: far more than 10:1.
        var zip = MakeZip("bomb.zip", ("zeros.bin", new byte[2_000_000]));
        var output = OutDir();
        var limits = new SafetyLimits { MaxRatio = 10, MinBytesForRatioCheck = 100_000 };

        var result = ArchiveService.Extract(zip, output, new ExtractOptions { SmartFolder = false, Limits = limits });

        Assert.False(result.Completed);
        Assert.Contains("zip bomb", result.AbortReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(output, "zeros.bin")));
    }

    [Fact]
    public void Small_highly_compressible_files_are_not_flagged()
    {
        var zip = MakeZip("zeros.zip", ("zeros.bin", new byte[2_000_000]));
        var output = OutDir();

        var result = ArchiveService.Extract(zip, output, new ExtractOptions { SmartFolder = false });

        Assert.True(result.Completed);   // below the default 256 MiB ratio-check threshold
        Assert.Equal(2_000_000, result.BytesWritten);
    }

    [Fact]
    public void Existing_files_are_kept_by_default_and_the_new_one_is_renamed()
    {
        var zip = MakeZip("a.zip", ("a.txt", Text("new")));
        var output = OutDir();
        File.WriteAllText(Path.Combine(output, "a.txt"), "old");

        ArchiveService.Extract(zip, output, new ExtractOptions { SmartFolder = false });

        Assert.Equal("old", File.ReadAllText(Path.Combine(output, "a.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(output, "a (1).txt")));
    }

    [Fact]
    public void Skip_mode_leaves_the_existing_file_and_reports_it()
    {
        var zip = MakeZip("a.zip", ("a.txt", Text("new")));
        var output = OutDir();
        File.WriteAllText(Path.Combine(output, "a.txt"), "old");

        var result = ArchiveService.Extract(zip, output, new ExtractOptions { SmartFolder = false, Overwrite = OverwriteMode.Skip });

        Assert.Equal("old", File.ReadAllText(Path.Combine(output, "a.txt")));
        Assert.Single(result.Skipped);
        Assert.Equal(0, result.FilesExtracted);
    }

    [Fact]
    public void Overwrite_mode_replaces_the_existing_file()
    {
        var zip = MakeZip("a.zip", ("a.txt", Text("new")));
        var output = OutDir();
        File.WriteAllText(Path.Combine(output, "a.txt"), "old");

        ArchiveService.Extract(zip, output, new ExtractOptions { SmartFolder = false, Overwrite = OverwriteMode.Overwrite });

        Assert.Equal("new", File.ReadAllText(Path.Combine(output, "a.txt")));
        Assert.False(File.Exists(Path.Combine(output, "a (1).txt")));
    }

    [Fact]
    public void Reports_progress_for_each_file()
    {
        var zip = MakeZip("p.zip", ("a.txt", Text("A")), ("b.txt", Text("B")), ("c.txt", Text("C")));
        var reports = new List<ExtractProgress>();

        ArchiveService.Extract(zip, OutDir(), new ExtractOptions { SmartFolder = false }, new SyncProgress<ExtractProgress>(reports.Add));

        Assert.Equal(3, reports.Count);
        Assert.Equal(3, reports[^1].FilesExtracted);
    }

    [Fact]
    public void A_cancelled_token_stops_extraction()
    {
        var zip = MakeZip("c.zip", ("a.txt", Text("A")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ArchiveService.Extract(zip, OutDir(), cancellationToken: cts.Token));
    }

    [Fact]
    public void Files_that_are_not_archives_are_refused_with_a_plain_message()
    {
        var path = Path.Combine(_work, "notes.txt");
        File.WriteAllText(path, "just text");

        var ex = Assert.Throws<NotSupportedException>(() => ArchiveService.List(path));
        Assert.Contains("recognise", ex.Message);
    }

    [Fact]
    public void Recognised_but_unsupported_formats_say_so()
    {
        var path = Path.Combine(_work, "data.zst");
        File.WriteAllBytes(path, new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0, 0, 0, 0 });

        var ex = Assert.Throws<NotSupportedException>(() => ArchiveService.List(path));
        Assert.Contains("Zstandard", ex.Message);
    }

    [Fact]
    public void Unique_path_counts_up()
    {
        var first = Path.Combine(_work, "x.txt");
        File.WriteAllText(first, "1");
        File.WriteAllText(Path.Combine(_work, "x (1).txt"), "2");

        Assert.Equal(Path.Combine(_work, "x (2).txt"), ArchiveService.UniquePath(first));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1_572_864, "1.5 MB")]
    public void Formats_byte_counts(long bytes, string expected)
    {
        Assert.Equal(expected, ArchiveService.FormatBytes(bytes));
    }

    /// <summary>Progress&lt;T&gt; posts to a thread pool thread; this one reports on the calling thread so tests can count without waiting.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}

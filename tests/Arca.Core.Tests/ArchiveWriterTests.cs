using System.Text;
using Arca.Core;
using Xunit;

namespace Arca.Core.Tests;

public sealed class ArchiveWriterTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "arca-writer-" + Guid.NewGuid().ToString("N"));

    public ArchiveWriterTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_work, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Photos/a.txt, Photos/sub/b.txt and notes.txt, in a "src" folder.</summary>
    private (string Folder, string Loose) MakeSources()
    {
        Write("src/Photos/a.txt", "alpha");
        Write("src/Photos/sub/b.txt", "bravo");
        var loose = Write("src/notes.txt", "notes");
        return (Path.Combine(_work, "src", "Photos"), loose);
    }

    private static Dictionary<string, string> ReadBack(string archive, string into)
    {
        var result = ArchiveService.Extract(archive, into, new ExtractOptions { SmartFolder = false });
        Assert.True(result.Completed);
        Assert.Empty(result.Skipped);
        return Directory.EnumerateFiles(into, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(into, p).Replace('\\', '/'), File.ReadAllText);
    }

    [Theory]
    [InlineData(CreateFormat.Zip, ArchiveFormat.Zip)]
    [InlineData(CreateFormat.Tar, ArchiveFormat.Tar)]
    [InlineData(CreateFormat.TarGz, ArchiveFormat.Gzip)]
    [InlineData(CreateFormat.TarBz2, ArchiveFormat.Bzip2)]
    public void Creates_each_format_and_reads_it_back(CreateFormat format, ArchiveFormat detected)
    {
        var (folder, loose) = MakeSources();
        var archive = Path.Combine(_work, "out" + ArchiveWriter.Extension(format));

        var result = ArchiveWriter.Create(archive, new[] { folder, loose }, new CreateOptions { Format = format });

        Assert.Equal(3, result.FilesAdded);
        Assert.Empty(result.Skipped);
        Assert.Equal(15, result.BytesAdded);
        Assert.Equal(detected, FormatDetector.DetectFile(archive));

        var files = ReadBack(archive, Path.Combine(_work, "back-" + format));
        Assert.Equal("alpha", files["Photos/a.txt"]);
        Assert.Equal("bravo", files["Photos/sub/b.txt"]);
        Assert.Equal("notes", files["notes.txt"]);
        Assert.Equal(3, files.Count);
    }

    [Fact]
    public void Test_passes_on_a_new_zip()
    {
        var (folder, _) = MakeSources();
        var archive = Path.Combine(_work, "t.zip");
        ArchiveWriter.Create(archive, new[] { folder });

        var test = ArchiveService.Test(archive);
        Assert.True(test.AllGood);
        Assert.Equal(2, test.FilesOk);
    }

    [Fact]
    public void Store_is_bigger_than_maximum_for_compressible_data()
    {
        Write("big/repeat.txt", string.Concat(Enumerable.Repeat("the same line again and again\n", 20_000)));
        var source = Path.Combine(_work, "big");
        var stored = Path.Combine(_work, "stored.zip");
        var packed = Path.Combine(_work, "packed.zip");

        ArchiveWriter.Create(stored, new[] { source }, new CreateOptions { Level = PackLevel.Store });
        ArchiveWriter.Create(packed, new[] { source }, new CreateOptions { Level = PackLevel.Maximum });

        Assert.True(new FileInfo(packed).Length * 10 < new FileInfo(stored).Length);
    }

    [Fact]
    public void Never_replaces_an_existing_file_unless_asked()
    {
        var (_, loose) = MakeSources();
        var archive = Write("existing.zip", "keep me");

        Assert.Throws<IOException>(() => ArchiveWriter.Create(archive, new[] { loose }));
        Assert.Equal("keep me", File.ReadAllText(archive));

        ArchiveWriter.Create(archive, new[] { loose }, new CreateOptions { Overwrite = true });
        Assert.Equal(ArchiveFormat.Zip, FormatDetector.DetectFile(archive));
    }

    [Fact]
    public void An_archive_saved_inside_the_folder_being_added_does_not_add_itself()
    {
        var (folder, _) = MakeSources();
        var archive = Path.Combine(folder, "Photos.zip");

        var result = ArchiveWriter.Create(archive, new[] { folder });

        Assert.Equal(2, result.FilesAdded);
        var names = ArchiveService.List(archive).Entries.Select(e => e.Path).ToList();
        Assert.DoesNotContain(names, n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Two_items_with_the_same_name_both_go_in()
    {
        var one = Write("x/report.txt", "one");
        var two = Write("y/report.txt", "two");
        var archive = Path.Combine(_work, "same.zip");

        ArchiveWriter.Create(archive, new[] { one, two });

        var files = ReadBack(archive, Path.Combine(_work, "same-out"));
        Assert.Equal("one", files["report.txt"]);
        Assert.Equal("two", files["report (1).txt"]);
    }

    [Fact]
    public void Links_are_skipped_not_followed()
    {
        var (folder, _) = MakeSources();
        var outside = Path.Combine(_work, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "not for the archive");
        try { Directory.CreateSymbolicLink(Path.Combine(folder, "link"), outside); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // this machine won't let the test make a link; nothing to check
        }

        var archive = Path.Combine(_work, "links.zip");
        var result = ArchiveWriter.Create(archive, new[] { folder });

        Assert.Equal(2, result.FilesAdded);
        Assert.Single(result.Skipped);
        Assert.DoesNotContain(ArchiveService.List(archive).Entries, e => e.Path.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancelling_leaves_nothing_behind()
    {
        var (folder, _) = MakeSources();
        var archive = Path.Combine(_work, "cancelled.zip");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveWriter.Create(archive, new[] { folder }, cancellationToken: cts.Token));

        Assert.False(File.Exists(archive));
        Assert.Empty(Directory.EnumerateFiles(_work, "*.arca-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Missing_items_are_reported_not_fatal()
    {
        var (_, loose) = MakeSources();
        var archive = Path.Combine(_work, "missing.zip");

        var result = ArchiveWriter.Create(archive, new[] { loose, Path.Combine(_work, "gone.txt") });

        Assert.Equal(1, result.FilesAdded);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void Non_ascii_names_survive_the_round_trip()
    {
        var file = Write("src2/Café menü 東京.txt", "ok");
        var archive = Path.Combine(_work, "names.zip");

        ArchiveWriter.Create(archive, new[] { file });

        var files = ReadBack(archive, Path.Combine(_work, "names-out"));
        Assert.True(files.ContainsKey("Café menü 東京.txt"));
    }

    [Theory]
    [InlineData(new[] { "C:/a/Photos" }, true, "Photos.zip")]
    [InlineData(new[] { "C:/a/report.pdf" }, false, "report.zip")]
    public void Suggests_a_name_from_the_items(string[] sources, bool isFolder, string expected)
    {
        // Paths that don't exist count as files, so make the folder case real.
        if (isFolder)
        {
            var real = Path.Combine(_work, "Photos");
            Directory.CreateDirectory(real);
            sources = new[] { real };
        }
        Assert.Equal(expected, ArchiveWriter.SuggestName(sources, CreateFormat.Zip));
    }

    [Fact]
    public void Several_items_are_named_after_their_folder()
    {
        var a = Write("Holiday/a.jpg", "a");
        var b = Write("Holiday/b.jpg", "b");
        Assert.Equal("Holiday.tar.gz", ArchiveWriter.SuggestName(new[] { a, b }, CreateFormat.TarGz));
    }
}

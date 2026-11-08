using Arca.Core;
using Xunit;

namespace Arca.Core.Tests;

public sealed class MultiPartAndSevenZipTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "arca-multi-" + Guid.NewGuid().ToString("N"));

    public MultiPartAndSevenZipTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    /// <summary>A folder "Docs" with a.txt, sub/b.txt and a larger c.bin, so archives are big enough to split.</summary>
    private string MakeSource()
    {
        var docs = Path.Combine(_work, "src", "Docs");
        Directory.CreateDirectory(Path.Combine(docs, "sub"));
        File.WriteAllText(Path.Combine(docs, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(docs, "sub", "b.txt"), "bravo");
        var random = new byte[200_000];
        new Random(42).NextBytes(random);
        File.WriteAllBytes(Path.Combine(docs, "c.bin"), random);
        return docs;
    }

    /// <summary>Cuts <paramref name="archive"/> into numbered pieces of <paramref name="size"/> bytes (name.001, name.002 …), as 7-Zip's split does, and deletes the original.</summary>
    private static List<string> Split(string archive, int size)
    {
        var bytes = File.ReadAllBytes(archive);
        var parts = new List<string>();
        for (int i = 0, offset = 0; offset < bytes.Length; i++, offset += size)
        {
            var part = $"{archive}.{i + 1:000}";
            File.WriteAllBytes(part, bytes.AsSpan(offset, Math.Min(size, bytes.Length - offset)).ToArray());
            parts.Add(part);
        }
        File.Delete(archive);
        return parts;
    }

    private static void AssertDocs(string folder)
    {
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(folder, "Docs", "a.txt")));
        Assert.Equal("bravo", File.ReadAllText(Path.Combine(folder, "Docs", "sub", "b.txt")));
        Assert.Equal(200_000, new FileInfo(Path.Combine(folder, "Docs", "c.bin")).Length);
    }

    [Theory]
    [InlineData(CreateFormat.Zip)]
    [InlineData(CreateFormat.TarGz)]
    public void A_split_archive_opens_from_any_piece(CreateFormat format)
    {
        var archive = Path.Combine(_work, "out" + ArchiveWriter.Extension(format));
        ArchiveWriter.Create(archive, new[] { MakeSource() }, new CreateOptions { Format = format, Level = PackLevel.Store });
        var parts = Split(archive, 70_000);
        Assert.True(parts.Count >= 3);

        var listing = ArchiveService.List(parts[1]);
        Assert.Equal(parts.Count, listing.Parts.Count);
        Assert.Equal(3, listing.FileCount);
        Assert.Equal(parts.Sum(p => new FileInfo(p).Length), listing.ArchiveSize);

        var into = Path.Combine(_work, "x");
        var result = ArchiveService.Extract(parts[^1], into, new ExtractOptions { SmartFolder = false });
        Assert.True(result.Completed);
        Assert.Empty(result.Skipped);
        AssertDocs(into);

        Assert.Empty(ArchiveService.Test(parts[0]).Failed);
    }

    [Fact]
    public void A_split_archive_with_a_missing_piece_says_which()
    {
        var archive = Path.Combine(_work, "out.zip");
        ArchiveWriter.Create(archive, new[] { MakeSource() }, new CreateOptions { Level = PackLevel.Store });
        var parts = Split(archive, 70_000);
        File.Delete(parts[1]);

        var ex = Assert.Throws<MissingPartException>(() => ArchiveService.List(parts[2]));
        Assert.Equal("out.zip.002", ex.PartName);
    }

    // The rest run 7za.exe, a Windows program; CI runs them on Windows.

    [Fact]
    public void Creates_a_7z_and_reads_it_back()
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = Path.Combine(_work, "out.7z");

        var result = ArchiveWriter.Create(archive, new[] { MakeSource() }, new CreateOptions { Format = CreateFormat.SevenZip });

        Assert.Equal(3, result.FilesAdded);
        Assert.Equal(ArchiveFormat.SevenZip, FormatDetector.DetectFile(archive));
        var into = Path.Combine(_work, "x");
        Assert.True(ArchiveService.Extract(archive, into, new ExtractOptions { SmartFolder = false }).Completed);
        AssertDocs(into);
        Assert.Empty(Directory.EnumerateFiles(_work, "*.arca-tmp"));
    }

    [Fact]
    public void A_7z_with_a_password_hides_its_names_until_the_password_is_given()
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = Path.Combine(_work, "secret.7z");
        const string password = "Pässwörd \"quoted\" 🔒";
        ArchiveWriter.Create(archive, new[] { MakeSource() }, new CreateOptions { Format = CreateFormat.SevenZip, Password = password });

        Assert.False(Assert.Throws<PasswordRequiredException>(() => ArchiveService.List(archive)).WrongPassword);
        Assert.True(Assert.Throws<PasswordRequiredException>(() => ArchiveService.List(archive, "wrong")).WrongPassword);

        var listing = ArchiveService.List(archive, password);
        Assert.Equal(3, listing.FileCount);
        Assert.All(listing.Entries.Where(e => !e.IsDirectory), e => Assert.True(e.IsEncrypted));

        var into = Path.Combine(_work, "x");
        var result = ArchiveService.Extract(archive, into, new ExtractOptions { SmartFolder = false, Password = password });
        Assert.Empty(result.Skipped);
        AssertDocs(into);
    }

    [Fact]
    public void A_zip_with_a_password_is_aes_encrypted_and_extracts_with_it()
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = Path.Combine(_work, "secret.zip");
        ArchiveWriter.Create(archive, new[] { MakeSource() }, new CreateOptions { Format = CreateFormat.Zip, Password = "letmein" });

        var listing = ArchiveService.List(archive);
        Assert.Equal(3, listing.FileCount);
        Assert.All(listing.Entries.Where(e => !e.IsDirectory), e => Assert.True(e.IsEncrypted));

        var without = ArchiveService.Extract(archive, Path.Combine(_work, "none"), new ExtractOptions { SmartFolder = false });
        Assert.Equal(3, without.Skipped.Count);

        var into = Path.Combine(_work, "x");
        var result = ArchiveService.Extract(archive, into, new ExtractOptions { SmartFolder = false, Password = "letmein" });
        Assert.Empty(result.Skipped);
        AssertDocs(into);
    }

    [Fact]
    public void A_split_7z_opens_from_any_piece()
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = Path.Combine(_work, "big.7z");
        ArchiveWriter.Create(archive, new[] { MakeSource() }, new CreateOptions { Format = CreateFormat.SevenZip, Level = PackLevel.Store });
        var parts = Split(archive, 70_000);

        var into = Path.Combine(_work, "x");
        Assert.True(ArchiveService.Extract(parts[^1], into, new ExtractOptions { SmartFolder = false }).Completed);
        AssertDocs(into);
    }

    [Fact]
    public void A_tar_cannot_have_a_password()
    {
        Assert.Throws<ArgumentException>(() => ArchiveWriter.Create(
            Path.Combine(_work, "x.tar"), new[] { MakeSource() }, new CreateOptions { Format = CreateFormat.Tar, Password = "x" }));
    }

    [Fact]
    public void The_password_never_goes_on_7zas_command_line()
    {
        var args = SevenZipCli.Arguments("out.7z", new CreateOptions { Format = CreateFormat.SevenZip, Password = "hunter2" }, "list.txt");
        Assert.DoesNotContain(args, a => a.Contains("hunter2"));
        Assert.Contains("-p", args);
        Assert.Contains("-mhe=on", args);
    }

    [Fact]
    public void Files_are_grouped_by_the_folder_they_were_chosen_from()
    {
        var sep = Path.DirectorySeparatorChar;
        var root = Path.Combine(_work, "src") + sep;
        var plan = new List<(string, string)>
        {
            (root + "Docs" + sep + "a.txt", "Docs/a.txt"),
            (root + "notes.txt", "notes.txt"),
        };
        var groups = SevenZipCli.GroupByRoot(plan);
        var group = Assert.Single(groups);
        Assert.Equal(root, group.Root);
        Assert.Equal(new[] { "Docs" + sep + "a.txt", "notes.txt" }, group.Relative);
    }
}

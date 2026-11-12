using Arca.Core;
using Xunit;

namespace Arca.Core.Tests;

public sealed class EditingAndExplorerTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "arca-edit-" + Guid.NewGuid().ToString("N"));

    public EditingAndExplorerTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, true); } catch (IOException) { }
    }

    private string Folder(string name, params (string Path, string Text)[] files)
    {
        var root = Path.Combine(_work, "src", name);
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Directory.CreateDirectory(root);
        return root;
    }

    private string MakeArchive(CreateFormat format, string? password = null)
    {
        var docs = Folder("Docs", ("a.txt", "alpha"), ("sub/b.txt", "bravo"));
        var archive = Path.Combine(_work, "box" + ArchiveWriter.Extension(format));
        ArchiveWriter.Create(archive, new[] { docs }, new CreateOptions { Format = format, Password = password });
        return archive;
    }

    private static HashSet<string> Files(string archive, string? password = null) =>
        ArchiveService.List(archive, password).Entries.Where(e => !e.IsDirectory).Select(e => e.Path).ToHashSet();

    private string ReadBack(string archive, string entry, string? password = null)
    {
        var into = Path.Combine(_work, "out-" + Guid.NewGuid().ToString("N"));
        ArchiveService.Extract(archive, into, new ExtractOptions { SmartFolder = false, Password = password, Only = new[] { entry } });
        return File.ReadAllText(Path.Combine(into, entry.Replace('/', Path.DirectorySeparatorChar)));
    }

    // Names and what can be edited: run anywhere.

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("what?")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("com1.txt")]
    public void Unusable_names_are_refused(string name)
    {
        Assert.Throws<ArgumentException>(() => ArchiveEditor.ValidateName(name));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("  spaced  ")]
    [InlineData("console.log")]
    [InlineData("Ünïcødé 📁")]
    public void Ordinary_names_are_allowed(string name)
    {
        Assert.Equal(name.Trim(), ArchiveEditor.ValidateName(name));
    }

    [Fact]
    public void Only_zip_7z_and_plain_tar_can_be_edited()
    {
        var zip = ArchiveService.List(MakeArchive(CreateFormat.Zip));
        Assert.Null(ArchiveEditor.WhyNotEditable(zip));
        var tgz = ArchiveService.List(MakeArchive(CreateFormat.TarGz));
        Assert.NotNull(ArchiveEditor.WhyNotEditable(tgz));
    }

    [Fact]
    public void Own_folder_is_named_after_the_archive_and_never_reused()
    {
        var archive = MakeArchive(CreateFormat.TarGz);
        Assert.Equal(Path.Combine(_work, "box"), ArchiveService.OwnFolderFor(archive));
        Directory.CreateDirectory(Path.Combine(_work, "box"));
        Assert.Equal(Path.Combine(_work, "box (1)"), ArchiveService.OwnFolderFor(archive));
    }

    [Fact]
    public void Adding_reports_what_it_would_replace()
    {
        var listing = ArchiveService.List(MakeArchive(CreateFormat.Zip));
        var newer = Folder("Newer", ("a.txt", "new alpha"), ("c.txt", "charlie"));
        var sources = Directory.GetFiles(newer).OrderBy(f => f).ToList();
        Assert.Equal(new[] { "Docs/a.txt" }, ArchiveEditor.WouldReplace(listing, sources, "Docs"));
        Assert.Empty(ArchiveEditor.WouldReplace(listing, sources, ""));
    }

    [Fact]
    public void Explorer_menu_runs_arca_with_a_verb_and_the_quoted_path()
    {
        const string exe = @"C:\Users\me\AppData\Local\Integra\apps\arca\0.4.0\Arca.exe";
        var plan = FileAssociations.ContextMenuPlan(exe);
        Assert.Contains(plan, e => e.Key.EndsWith(@"\.zip\shell\Arca\shell\1ExtractHere\command") && e.Value == $"\"{exe}\" e \"%1\"");
        Assert.Contains(plan, e => e.Key.EndsWith(@"\.7z\shell\Arca\shell\2ExtractTo\command") && e.Value == $"\"{exe}\" x \"%1\"");
        Assert.Contains(plan, e => e.Key == @"Software\Classes\Directory\shell\ArcaAdd\command" && e.Value == $"\"{exe}\" a \"%1\"");
        Assert.All(plan, e => Assert.True(FileAssociations.ContextMenuKeys.Any(k => e.Key.StartsWith(k)), e.Key));
    }

    // Changing archives runs 7za.exe, a Windows program; CI runs these on Windows.

    [Theory]
    [InlineData(CreateFormat.Zip)]
    [InlineData(CreateFormat.SevenZip)]
    [InlineData(CreateFormat.Tar)]
    public void Adds_into_a_folder_inside_the_archive(CreateFormat format)
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = MakeArchive(format);
        var extra = Folder("Extra", ("c.txt", "charlie"));
        var result = ArchiveEditor.Add(ArchiveService.List(archive), new[] { Path.Combine(extra, "c.txt"), extra }, "Docs/sub");

        Assert.Equal(2, result.FilesAdded);
        var files = Files(archive);
        Assert.Contains("Docs/sub/c.txt", files);
        Assert.Contains("Docs/sub/Extra/c.txt", files);
        Assert.Contains("Docs/a.txt", files);
        Assert.Equal("charlie", ReadBack(archive, "Docs/sub/c.txt"));
    }

    [Fact]
    public void Adding_replaces_a_file_of_the_same_name()
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = MakeArchive(CreateFormat.Zip);
        var newer = Folder("Newer", ("a.txt", "new alpha"));
        ArchiveEditor.Add(ArchiveService.List(archive), new[] { Path.Combine(newer, "a.txt") }, "Docs");
        Assert.Equal("new alpha", ReadBack(archive, "Docs/a.txt"));
        Assert.Equal(2, Files(archive).Count);
    }

    [Theory]
    [InlineData(CreateFormat.Zip)]
    [InlineData(CreateFormat.SevenZip)]
    public void Deletes_files_and_whole_folders(CreateFormat format)
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = MakeArchive(format);
        ArchiveEditor.Delete(ArchiveService.List(archive), new[] { "Docs/sub" });
        Assert.Equal(new HashSet<string> { "Docs/a.txt" }, Files(archive));
    }

    [Theory]
    [InlineData(CreateFormat.Zip)]
    [InlineData(CreateFormat.SevenZip)]
    public void Renames_a_folder_with_everything_in_it(CreateFormat format)
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = MakeArchive(format);
        ArchiveEditor.Rename(ArchiveService.List(archive), "Docs/sub", "Inner");
        Assert.Equal(new HashSet<string> { "Docs/a.txt", "Docs/Inner/b.txt" }, Files(archive));
        Assert.Equal("bravo", ReadBack(archive, "Docs/Inner/b.txt"));
    }

    [Fact]
    public void Renaming_onto_an_existing_name_is_refused()
    {
        var listing = ArchiveService.List(MakeArchive(CreateFormat.Zip));
        Assert.Throws<ArgumentException>(() => ArchiveEditor.Rename(listing, "Docs/a.txt", "sub"));
    }

    [Fact]
    public void Creates_an_empty_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = MakeArchive(CreateFormat.Zip);
        var path = ArchiveEditor.NewFolder(ArchiveService.List(archive), "Docs", "Empty");
        Assert.Equal("Docs/Empty", path);
        var listing = ArchiveService.List(archive);
        Assert.Contains(listing.Entries, e => e.IsDirectory && e.Path.TrimEnd('/') == "Docs/Empty");
        Assert.NotNull(ArchiveTree.FindFolder(ArchiveTree.Build(listing.Entries), "Docs/Empty"));
    }

    [Fact]
    public void Files_added_to_a_hidden_names_7z_stay_hidden()
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = MakeArchive(CreateFormat.SevenZip, "secret");
        var extra = Folder("Extra", ("c.txt", "charlie"));
        var listing = ArchiveService.List(archive, "secret");
        Assert.True(ArchiveEditor.PasswordOpens(listing, "secret"));
        Assert.False(ArchiveEditor.PasswordOpens(listing, "wrong"));

        ArchiveEditor.Add(listing, new[] { Path.Combine(extra, "c.txt") }, "", "secret");

        Assert.Throws<PasswordRequiredException>(() => ArchiveService.List(archive));
        Assert.Equal("charlie", ReadBack(archive, "c.txt", "secret"));
    }

    [Fact]
    public void Files_added_to_an_encrypted_zip_are_encrypted_too()
    {
        if (!OperatingSystem.IsWindows()) return;
        var archive = MakeArchive(CreateFormat.Zip, "secret");
        var extra = Folder("Extra", ("c.txt", "charlie"));
        ArchiveEditor.Add(ArchiveService.List(archive), new[] { Path.Combine(extra, "c.txt") }, "", "secret");

        var added = ArchiveService.List(archive).Entries.Single(e => e.Path == "c.txt");
        Assert.True(added.IsEncrypted);
        Assert.Equal("charlie", ReadBack(archive, "c.txt", "secret"));
    }

    [Fact]
    public void Rar_and_multi_part_archives_are_never_handed_to_7za()
    {
        var listing = ArchiveService.List(MakeArchive(CreateFormat.Zip));
        var parts = new ArchiveListing
        {
            ArchivePath = listing.ArchivePath, Format = ArchiveFormat.Zip, Entries = listing.Entries, Warnings = listing.Warnings,
            Parts = new[] { "a.zip.001", "a.zip.002" },
        };
        Assert.Throws<NotSupportedException>(() => ArchiveEditor.Delete(parts, new[] { "Docs/a.txt" }));
    }
}

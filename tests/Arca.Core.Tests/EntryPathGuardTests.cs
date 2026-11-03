using Arca.Core;
using Xunit;

namespace Arca.Core.Tests;

public class EntryPathGuardTests
{
    [Theory]
    [InlineData("readme.txt")]
    [InlineData("folder/readme.txt")]
    [InlineData("folder\\sub\\readme.txt")]
    [InlineData("folder/")]
    [InlineData("./folder/file.txt")]
    [InlineData("a//b.txt")]
    [InlineData("photos/2026/IMG_0001.jpg")]
    [InlineData("name with spaces.txt")]
    [InlineData("console.log")]       // "console" is not the reserved name "CON"
    public void Accepts_ordinary_names(string name)
    {
        Assert.Null(EntryPathGuard.CheckName(name, out var relative));
        Assert.NotEqual("", relative);
        Assert.DoesNotContain("..", relative);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("folder/../../evil.txt")]
    [InlineData("folder\\..\\..\\evil.txt")]
    [InlineData("..")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32\\x.dll")]
    [InlineData("C:\\Windows\\x.dll")]
    [InlineData("c:evil.txt")]
    [InlineData("file.txt:hidden")]       // alternate data stream
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData("folder/NUL.log")]
    [InlineData("com1")]
    [InlineData("trailingdot.")]
    [InlineData("trailingspace ")]
    [InlineData("bad<name>.txt")]
    [InlineData("what?.txt")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("./")]
    public void Refuses_unsafe_names(string name)
    {
        Assert.NotNull(EntryPathGuard.CheckName(name, out _));
    }

    [Fact]
    public void Refuses_null_character_and_overlong_segments()
    {
        Assert.NotNull(EntryPathGuard.CheckName("a\0b.txt", out _));
        Assert.NotNull(EntryPathGuard.CheckName(new string('a', 300) + ".txt", out _));
    }

    [Fact]
    public void Resolves_inside_the_destination()
    {
        var root = Path.Combine(Path.GetTempPath(), "arca-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(EntryPathGuard.TryResolve(root, "a/b/c.txt", out var full, out var problem), problem);
            Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, full);
            Assert.EndsWith(Path.Combine("a", "b", "c.txt"), full);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Refuses_to_resolve_an_escape()
    {
        var root = Path.Combine(Path.GetTempPath(), "arca-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(EntryPathGuard.TryResolve(root, "../outside.txt", out var full, out var problem));
            Assert.Equal("", full);
            Assert.NotEqual("", problem);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Refuses_to_write_through_a_symlinked_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "arca-guard-" + Guid.NewGuid().ToString("N"));
        var elsewhere = Path.Combine(Path.GetTempPath(), "arca-elsewhere-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(elsewhere);
        try
        {
            try { Directory.CreateSymbolicLink(Path.Combine(root, "link"), elsewhere); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return; // this machine won't let the test make a symlink (Windows without developer mode); nothing to check
            }

            Assert.False(EntryPathGuard.TryResolve(root, "link/payload.txt", out _, out var problem));
            Assert.Contains("link", problem, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
            Directory.Delete(elsewhere, true);
        }
    }
}

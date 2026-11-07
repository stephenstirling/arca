using Arca.Core;
using Xunit;

namespace Arca.Core.Tests;

public class FileAssociationsTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Integra\apps\arca\0.2.0\Arca.exe";

    [Fact]
    public void Open_command_quotes_both_the_exe_and_the_file()
    {
        var plan = FileAssociations.Plan(Exe);
        var command = plan.Single(e => e.Key == FileAssociations.ProgIdKey + @"\shell\open\command").Value;
        Assert.Equal($"\"{Exe}\" \"%1\"", command);
    }

    [Fact]
    public void Every_extension_is_offered_in_open_with_and_default_apps()
    {
        var plan = FileAssociations.Plan(Exe);
        foreach (var ext in FileAssociations.Extensions)
        {
            Assert.Contains(new RegistryEntry(FileAssociations.OpenWithProgidsKey(ext), FileAssociations.ProgId, ""), plan);
            Assert.Contains(new RegistryEntry(FileAssociations.CapabilitiesKey + @"\FileAssociations", ext, FileAssociations.ProgId), plan);
        }
    }

    [Fact]
    public void Registered_application_points_at_the_capabilities_key()
    {
        var plan = FileAssociations.Plan(Exe);
        Assert.Contains(new RegistryEntry(FileAssociations.RegisteredApplicationsKey, "Arca", FileAssociations.CapabilitiesKey), plan);
    }

    [Fact]
    public void Writes_only_under_the_current_user_software_key()
    {
        Assert.All(FileAssociations.Plan(Exe), e => Assert.StartsWith(@"Software\", e.Key));
    }

    [Fact]
    public void Extensions_are_lower_case_and_dotted()
    {
        Assert.All(FileAssociations.Extensions, ext =>
        {
            Assert.StartsWith(".", ext);
            Assert.Equal(ext.ToLowerInvariant(), ext);
        });
        Assert.Equal(FileAssociations.Extensions.Count, FileAssociations.Extensions.Distinct().Count());
    }
}

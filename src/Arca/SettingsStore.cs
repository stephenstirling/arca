using System.Text.Json;

namespace Arca;

/// <summary>Arca's few settings. Saved in %LOCALAPPDATA%\Arca\settings.json, which updates and uninstalls leave alone.</summary>
public sealed class ArcaSettings
{
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Extract all: put several top-level items in a new folder named after the archive.</summary>
    public bool SmartFolder { get; set; } = true;

    /// <summary>Copy the archive's "downloaded from the internet" mark onto extracted files.</summary>
    public bool KeepInternetMark { get; set; } = true;
}

public static class SettingsStore
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arca");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static ArcaSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ArcaSettings>(File.ReadAllText(FilePath)) ?? new ArcaSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new ArcaSettings();
    }

    /// <summary>Writes to a temporary file first, so a crash mid-save never leaves a broken settings file.</summary>
    public static void Save(ArcaSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

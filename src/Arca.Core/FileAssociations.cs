namespace Arca.Core;

/// <summary>One registry value under HKEY_CURRENT_USER. An empty <see cref="Name"/> is the key's default value.</summary>
public sealed record RegistryEntry(string Key, string Name, string Value);

/// <summary>
/// What Arca writes so Windows offers it for archives: in "Open with", and under Settings → Default apps.
/// Everything is per user (HKCU), so no administrator prompt. Windows 11 never lets an app make itself the
/// default; registering only puts Arca on the list the user chooses from.
/// </summary>
public static class FileAssociations
{
    public const string ProgId = "Arca.Archive";
    public const string RegisteredName = "Arca";
    public const string ExeName = "Arca.exe";

    public const string ProgIdKey = @"Software\Classes\" + ProgId;
    public const string ApplicationKey = @"Software\Classes\Applications\" + ExeName;
    public const string CapabilitiesKey = @"Software\Arca\Capabilities";
    public const string RegisteredApplicationsKey = @"Software\RegisteredApplications";

    /// <summary>Only formats <see cref="ArchiveFormats.CanRead"/> accepts: offering Arca for a file it then refuses would be worse than not offering it.</summary>
    public static readonly IReadOnlyList<string> Extensions =
    [
        ".zip", ".7z", ".rar",
        ".tar", ".gz", ".tgz", ".bz2", ".tbz", ".tbz2", ".xz", ".txz",
    ];

    public static string OpenWithProgidsKey(string extension) => $@"Software\Classes\{extension}\OpenWithProgids";

    /// <summary>The values to write for the Arca.exe at <paramref name="exePath"/> (a full path).</summary>
    public static IReadOnlyList<RegistryEntry> Plan(string exePath)
    {
        string command = $"\"{exePath}\" \"%1\"";
        string icon = $"\"{exePath}\",0";

        var entries = new List<RegistryEntry>
        {
            new(ProgIdKey, "", "Archive"),
            new(ProgIdKey + @"\DefaultIcon", "", icon),
            new(ProgIdKey + @"\shell\open\command", "", command),

            new(ApplicationKey, "FriendlyAppName", RegisteredName),
            new(ApplicationKey + @"\DefaultIcon", "", icon),
            new(ApplicationKey + @"\shell\open\command", "", command),

            new(CapabilitiesKey, "ApplicationName", RegisteredName),
            new(CapabilitiesKey, "ApplicationDescription", "Open, test and extract ZIP, 7z, RAR and TAR archives."),
            new(CapabilitiesKey, "ApplicationIcon", icon),
            new(RegisteredApplicationsKey, RegisteredName, CapabilitiesKey),
        };
        foreach (var ext in Extensions)
        {
            entries.Add(new(OpenWithProgidsKey(ext), ProgId, ""));
            entries.Add(new(ApplicationKey + @"\SupportedTypes", ext, ""));
            entries.Add(new(CapabilitiesKey + @"\FileAssociations", ext, ProgId));
        }
        return entries;
    }

    /// <summary>
    /// The right-click menu entries in File Explorer, which run Arca with a verb and one path (Explorer starts one
    /// Arca per selected item; Arca gathers them into one window). An "Arca" submenu on archives, "Add to archive"
    /// on every file and folder. On Windows 11 these sit under "Show more options".
    /// </summary>
    public static IReadOnlyList<RegistryEntry> ContextMenuPlan(string exePath)
    {
        string icon = $"\"{exePath}\",0";
        string Run(string verb) => $"\"{exePath}\" {verb} \"%1\"";
        var entries = new List<RegistryEntry>();

        foreach (var ext in Extensions)
        {
            var menu = ArchiveMenuKey(ext);
            entries.Add(new(menu, "MUIVerb", "Arca"));
            entries.Add(new(menu, "Icon", icon));
            entries.Add(new(menu, "SubCommands", ""));
            AddVerb(menu + @"\shell\1ExtractHere", "Extract here", Run("e"));
            AddVerb(menu + @"\shell\2ExtractTo", "Extract to a folder", Run("x"));
            AddVerb(menu + @"\shell\3Open", "Open in Arca", $"\"{exePath}\" \"%1\"", multi: false);
        }
        AddVerb(AddToArchiveKeys[0], "Add to archive with Arca…", Run("a"));
        AddVerb(AddToArchiveKeys[1], "Add to archive with Arca…", Run("a"));
        return entries;

        void AddVerb(string key, string text, string command, bool multi = true)
        {
            entries.Add(new(key, "MUIVerb", text));
            entries.Add(new(key, "Icon", icon));
            // "Player" lifts Explorer's 15-item limit on how many selected files a static verb runs for.
            if (multi) entries.Add(new(key, "MultiSelectModel", "Player"));
            entries.Add(new(key + @"\command", "", command));
        }
    }

    /// <summary>Every key <see cref="ContextMenuPlan"/> creates at its top, for removing the menu again.</summary>
    public static IReadOnlyList<string> ContextMenuKeys =>
        [.. Extensions.Select(ArchiveMenuKey), .. AddToArchiveKeys];

    /// <summary>SystemFileAssociations, so the submenu shows whichever program the person made the default for that type.</summary>
    private static string ArchiveMenuKey(string extension) => $@"Software\Classes\SystemFileAssociations\{extension}\shell\Arca";

    private static readonly string[] AddToArchiveKeys =
    [
        @"Software\Classes\*\shell\ArcaAdd",
        @"Software\Classes\Directory\shell\ArcaAdd",
    ];
}

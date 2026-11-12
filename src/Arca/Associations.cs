using System.Runtime.InteropServices;
using Arca.Core;
using Microsoft.Win32;

namespace Arca;

/// <summary>
/// Writes <see cref="FileAssociations.Plan"/> to the registry. Runs on every start because Integra installs each
/// version into its own folder: re-registering points Windows at the copy that is running now.
/// </summary>
internal static class Associations
{
    /// <summary>Registers this Arca.exe. Best-effort: a failure leaves Arca working, just not offered by Windows.</summary>
    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        try
        {
            bool changed = false;
            var plan = FileAssociations.Plan(exe).ToList();
            if (SettingsStore.Load().ExplorerMenu) plan.AddRange(FileAssociations.ContextMenuPlan(exe));
            else changed = RemoveExplorerMenu();
            foreach (var entry in plan)
            {
                using var key = Registry.CurrentUser.CreateSubKey(entry.Key, writable: true);
                if (key.GetValue(entry.Name) as string == entry.Value) continue;
                key.SetValue(entry.Name, entry.Value, RegistryValueKind.String);
                changed = true;
            }
            // Explorer caches associations; without this the new entry appears only after sign-out.
            if (changed) NotifyShell();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }
    }

    /// <summary>Takes Arca out of File Explorer's right-click menu; true when there was something to remove.</summary>
    public static bool RemoveExplorerMenu()
    {
        bool removed = false;
        foreach (var key in FileAssociations.ContextMenuKeys)
        {
            using (var existing = Registry.CurrentUser.OpenSubKey(key)) removed |= existing is not null;
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
        return removed;
    }

    /// <summary>Applies the Explorer-menu setting now, rather than at the next start.</summary>
    public static void ApplyExplorerMenu(bool on)
    {
        try
        {
            if (on) Register();
            else if (RemoveExplorerMenu()) NotifyShell();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
        }
    }

    /// <summary>Removes everything <see cref="Register"/> wrote. Leaves the user's own default choice alone, which Windows owns.</summary>
    public static void Unregister()
    {
        var hkcu = Registry.CurrentUser;
        RemoveExplorerMenu();
        hkcu.DeleteSubKeyTree(FileAssociations.ProgIdKey, throwOnMissingSubKey: false);
        hkcu.DeleteSubKeyTree(FileAssociations.ApplicationKey, throwOnMissingSubKey: false);
        hkcu.DeleteSubKeyTree(FileAssociations.CapabilitiesKey, throwOnMissingSubKey: false);
        using (var arca = hkcu.OpenSubKey(@"Software\Arca"))
        {
            bool empty = arca is not null && arca.SubKeyCount == 0 && arca.ValueCount == 0;
            if (empty) hkcu.DeleteSubKey(@"Software\Arca", throwOnMissingSubKey: false);
        }
        using (var registered = hkcu.OpenSubKey(FileAssociations.RegisteredApplicationsKey, writable: true))
            registered?.DeleteValue(FileAssociations.RegisteredName, throwOnMissingValue: false);
        foreach (var ext in FileAssociations.Extensions)
        {
            using var key = hkcu.OpenSubKey(FileAssociations.OpenWithProgidsKey(ext), writable: true);
            key?.DeleteValue(FileAssociations.ProgId, throwOnMissingValue: false);
        }
        NotifyShell();
    }

    /// <summary>The Default apps page for Arca, where the user picks it for each archive type.</summary>
    public static Uri DefaultAppsPage => new($"ms-settings:defaultapps?registeredAppUser={FileAssociations.RegisteredName}");

    private const int ShcneAssocChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell() => SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero);
}

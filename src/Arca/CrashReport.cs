using System.Runtime.InteropServices;

namespace Arca;

/// <summary>
/// When something goes badly wrong, Arca says so instead of vanishing: the error is written to
/// %LOCALAPPDATA%\Arca\crash.log and shown in a plain Windows message box (which works even when the window never appeared).
/// </summary>
internal static class CrashReport
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_ICONERROR = 0x10;

    public static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arca", "crash.log");

    private static int _shown;

    public static void Show(string where, Exception? ex)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  Arca {SafeVersion()}  ({where})\r\n{ex}\r\n\r\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, text);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }

        // One box per run is enough; later errors are still logged. The build's startup check sets
        // ARCA_NO_CRASH_DIALOG so a box can't sit there waiting for a click nobody will make.
        if (Environment.GetEnvironmentVariable("ARCA_NO_CRASH_DIALOG") == "1") return;
        if (Interlocked.Exchange(ref _shown, 1) == 1) return;
        var summary = ex is null ? "Unknown error" : $"{ex.GetType().Name}: {ex.Message}";
        MessageBoxW(IntPtr.Zero,
            $"Arca hit an error and may need to close.\n\n{summary}\n\nThe details were saved to:\n{LogPath}",
            "Arca", MB_ICONERROR);
    }

    private static string SafeVersion()
    {
        try { return Cli.Version; }
        catch (Exception) { return "?"; }
    }
}

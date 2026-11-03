namespace Arca.Core;

/// <summary>
/// Windows marks files that came from the internet with a hidden "Zone.Identifier" stream. That mark is what
/// makes SmartScreen and Office's Protected View check a file before it runs. An extractor that drops the mark
/// turns a downloaded archive into a way to launder files past those checks, so Arca copies it onto what it extracts.
/// Does nothing off Windows or on a file system without alternate data streams.
/// </summary>
public static class MarkOfTheWeb
{
    private const string StreamName = ":Zone.Identifier";

    /// <summary>The mark on <paramref name="path"/>, or null when it has none (or this isn't Windows).</summary>
    public static string? Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var text = File.ReadAllText(path + StreamName);
            return text.Contains("ZoneId=", StringComparison.OrdinalIgnoreCase) ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes a mark read by <see cref="Read"/> onto <paramref name="path"/>. Failure is silent: it must never stop an extraction.</summary>
    public static void Apply(string path, string mark)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { File.WriteAllText(path + StreamName, mark); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }
}

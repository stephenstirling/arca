namespace Arca.Core;

/// <summary>
/// Decides whether an entry name from inside an archive is safe to write to disk, and where it would go.
/// Archives are untrusted: names can climb out of the destination ("../x"), be absolute ("C:\x"), point at
/// Windows device names ("CON"), alternate data streams ("a.txt:evil") or, through a link already in the
/// destination folder, at somewhere else entirely. Every one of those is refused here, not "fixed up".
/// </summary>
public static class EntryPathGuard
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] BadChars = { '<', '>', ':', '"', '|', '?', '*' };

    /// <summary>
    /// Checks the name on its own. Returns null when it is fine, otherwise a short plain-language reason.
    /// <paramref name="relativePath"/> is the cleaned path (current platform's separators) when the name is fine.
    /// Both "/" and "\" count as separators, whatever the platform, because the archive may be extracted on Windows.
    /// </summary>
    public static string? CheckName(string? entryName, out string relativePath)
    {
        relativePath = "";
        if (string.IsNullOrWhiteSpace(entryName)) return "the name is empty";
        if (entryName.IndexOf('\0') >= 0) return "the name contains a null character";
        if (entryName[0] == '/' || entryName[0] == '\\') return "it is an absolute path";
        if (entryName.Length >= 2 && char.IsLetter(entryName[0]) && entryName[1] == ':') return "it is an absolute path (drive letter)";

        var parts = new List<string>();
        foreach (var segment in entryName.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") return "it climbs out of the destination folder (\"..\")";
            if (segment.Length > 255) return "a name in the path is too long";
            foreach (var c in segment)
                if (c < 32 || Array.IndexOf(BadChars, c) >= 0)
                    return "the name has characters Windows doesn't allow (or an alternate data stream)";
            if (segment[^1] == '.' || segment[^1] == ' ') return "a name ends with a dot or space, which Windows silently changes";
            var stem = segment;
            var dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem[..dot];
            if (ReservedNames.Contains(stem.TrimEnd())) return $"\"{segment}\" is a reserved Windows device name";
            parts.Add(segment);
        }
        if (parts.Count == 0) return "the name is empty";

        relativePath = string.Join(Path.DirectorySeparatorChar, parts);
        return null;
    }

    /// <summary>
    /// Works out the full path <paramref name="entryName"/> would be written to under <paramref name="destinationRoot"/>.
    /// Returns true with <paramref name="fullPath"/> set when it is safe; otherwise false with <paramref name="problem"/> saying why.
    /// </summary>
    public static bool TryResolve(string destinationRoot, string? entryName, out string fullPath, out string problem)
    {
        fullPath = "";
        var nameProblem = CheckName(entryName, out var relative);
        if (nameProblem is not null)
        {
            problem = nameProblem;
            return false;
        }

        var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));

        // Belt and braces: the name checks above should already make this impossible.
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            problem = "it would be written outside the destination folder";
            return false;
        }

        if (PassesThroughLink(root, full))
        {
            problem = "it would be written through a link (shortcut or junction) in the destination folder";
            return false;
        }

        fullPath = full;
        problem = "";
        return true;
    }

    /// <summary>True when a folder below the root, or the target file itself, already exists as a symlink or junction.</summary>
    private static bool PassesThroughLink(string root, string full)
    {
        try
        {
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return true;

            var rootWithSeparator = root + Path.DirectorySeparatorChar;
            var dir = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir) && dir.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return true;
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // can't tell, so don't write
        }
    }
}

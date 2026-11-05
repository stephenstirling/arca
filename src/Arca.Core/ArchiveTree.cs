namespace Arca.Core;

/// <summary>One file or folder in the archive, as the file list shows it.</summary>
public sealed class ArchiveNode
{
    public string Name { get; internal set; } = "";

    /// <summary>Path inside the archive with "/" separators and no trailing slash; "" for the root.</summary>
    public string FullPath { get; internal set; } = "";

    public bool IsFolder { get; internal init; }

    /// <summary>Unpacked size; for a folder, everything under it.</summary>
    public long Size { get; internal set; }

    /// <summary>Packed size; for a folder, everything under it.</summary>
    public long PackedSize { get; internal set; }

    public DateTime? Modified { get; internal set; }

    /// <summary>The CRC-32 the archive stores for this file, or 0 when it has none.</summary>
    public long Crc { get; internal init; }

    public bool IsEncrypted { get; internal init; }

    /// <summary>Set when the name is unsafe (see <see cref="EntryPathGuard"/>); such entries sit at the top level and are never extracted.</summary>
    public string? UnsafeReason { get; internal init; }

    public ArchiveNode? Parent { get; internal set; }

    /// <summary>1 for a file; for a folder, the files at any depth under it.</summary>
    public int FileCount { get; internal set; }

    public List<ArchiveNode> Children { get; } = new();

    internal bool HasOwnTime { get; set; }

    public bool IsRoot => Parent is null;

    /// <summary>Every node under this one, depth first.</summary>
    public IEnumerable<ArchiveNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            if (child.IsFolder)
                foreach (var d in child.Descendants()) yield return d;
        }
    }
}

public static class ArchivePaths
{
    /// <summary>"\" becomes "/", leading "./" and repeated or trailing slashes go. ".." is left alone so the safety checks still see it.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var s = name.Replace('\\', '/');
        while (s.Contains("//", StringComparison.Ordinal)) s = s.Replace("//", "/", StringComparison.Ordinal);
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        if (s == ".") s = "";
        return s.Trim('/');
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it ("" contains everything). Case is ignored, as on Windows.</summary>
    public static bool IsUnder(string path, string folder) =>
        folder.Length == 0 ||
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    public static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}

/// <summary>Turns the archive's flat list of names into folders and files.</summary>
public static class ArchiveTree
{
    public static ArchiveNode Build(IEnumerable<ArchiveEntryInfo> entries)
    {
        var root = new ArchiveNode { IsFolder = true };
        var folders = new Dictionary<string, ArchiveNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };

        ArchiveNode GetFolder(string path)
        {
            if (folders.TryGetValue(path, out var existing)) return existing;
            var parentPath = ArchivePaths.ParentOf(path);
            var parent = GetFolder(parentPath);
            var folder = new ArchiveNode
            {
                IsFolder = true,
                Name = parentPath.Length == 0 ? path : path[(parentPath.Length + 1)..],
                FullPath = path,
                Parent = parent,
            };
            parent.Children.Add(folder);
            folders[path] = folder;
            return folder;
        }

        foreach (var e in entries)
        {
            var path = ArchivePaths.Normalize(e.Path);

            // Unsafe names are shown at the top, under their raw name, so nothing is hidden; they are never extracted.
            if (e.UnsafeReason is not null)
            {
                root.Children.Add(new ArchiveNode
                {
                    Name = e.Path,
                    FullPath = path,
                    Size = Math.Max(0, e.Size),
                    PackedSize = Math.Max(0, e.CompressedSize),
                    Modified = e.LastModified,
                    Crc = e.Crc,
                    IsEncrypted = e.IsEncrypted,
                    UnsafeReason = e.UnsafeReason,
                    Parent = root,
                    FileCount = e.IsDirectory ? 0 : 1,
                });
                continue;
            }
            if (path.Length == 0) continue;

            if (e.IsDirectory)
            {
                var folder = GetFolder(path);
                if (e.LastModified is not null)
                {
                    folder.Modified = e.LastModified;
                    folder.HasOwnTime = true;
                }
                continue;
            }

            var parentPath = ArchivePaths.ParentOf(path);
            var parentFolder = GetFolder(parentPath);
            parentFolder.Children.Add(new ArchiveNode
            {
                Name = parentPath.Length == 0 ? path : path[(parentPath.Length + 1)..],
                FullPath = path,
                Size = Math.Max(0, e.Size),
                PackedSize = Math.Max(0, e.CompressedSize),
                Modified = e.LastModified,
                Crc = e.Crc,
                IsEncrypted = e.IsEncrypted,
                Parent = parentFolder,
                FileCount = 1,
            });
        }

        Aggregate(root);
        return root;
    }

    /// <summary>Finds the folder at <paramref name="path"/>, or null.</summary>
    public static ArchiveNode? FindFolder(ArchiveNode root, string path)
    {
        if (path.Length == 0) return root;
        var node = root;
        foreach (var part in path.Split('/'))
        {
            node = node.Children.FirstOrDefault(c => c.IsFolder && c.UnsafeReason is null && c.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
            if (node is null) return null;
        }
        return node;
    }

    private static void Aggregate(ArchiveNode folder)
    {
        long size = 0, packed = 0;
        int files = 0;
        DateTime? latest = null;
        foreach (var child in folder.Children)
        {
            if (child.IsFolder) Aggregate(child);
            size += child.Size;
            packed += child.PackedSize;
            files += child.FileCount;
            if (child.Modified is DateTime m && (latest is null || m > latest)) latest = m;
        }
        folder.Size = size;
        folder.PackedSize = packed;
        folder.FileCount = files;
        if (!folder.HasOwnTime) folder.Modified = latest;
    }
}

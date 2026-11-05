using Arca.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Arca;

/// <summary>One line in the file list: a file, a folder, or ".." (up one level).</summary>
public sealed class EntryRow
{
    private EntryRow() { }

    public static EntryRow Up() => new()
    {
        IsParent = true,
        Name = "..",
        TypeText = ShellInfo.TypeName("", folder: true),
        Icon = ShellInfo.Icon("", folder: true),
        ToolTip = "Up one level",
    };

    public static EntryRow From(ArchiveNode node, bool showFullPath = false)
    {
        bool folder = node.IsFolder;
        return new EntryRow
        {
            Node = node,
            Name = showFullPath && node.UnsafeReason is null ? node.FullPath.Replace('/', '\\') : node.Name,
            SizeText = node.Size.ToString("N0"),
            PackedText = node.PackedSize.ToString("N0"),
            TypeText = ShellInfo.TypeName(node.Name, folder),
            ModifiedText = node.Modified?.ToString("g") ?? "",
            CrcText = folder || node.Crc == 0 ? "" : ((uint)node.Crc).ToString("X8"),
            Icon = ShellInfo.Icon(node.Name, folder),
            LockVisibility = node.IsEncrypted ? Visibility.Visible : Visibility.Collapsed,
            WarnVisibility = node.UnsafeReason is not null ? Visibility.Visible : Visibility.Collapsed,
            ToolTip = node.UnsafeReason is not null ? "Unsafe name, will not be extracted: " + node.UnsafeReason
                    : node.IsEncrypted ? "Password-protected"
                    : node.FullPath.Replace('/', '\\'),
        };
    }

    public ArchiveNode? Node { get; private init; }
    public bool IsParent { get; private init; }
    public bool IsFolder => IsParent || (Node?.IsFolder ?? false);

    public string Name { get; private init; } = "";
    public string SizeText { get; private init; } = "";
    public string PackedText { get; private init; } = "";
    public string TypeText { get; private init; } = "";
    public string ModifiedText { get; private init; } = "";
    public string CrcText { get; private init; } = "";
    public ImageSource? Icon { get; private init; }
    public Visibility LockVisibility { get; private init; } = Visibility.Collapsed;
    public Visibility WarnVisibility { get; private init; } = Visibility.Collapsed;
    public string ToolTip { get; private init; } = "";
}

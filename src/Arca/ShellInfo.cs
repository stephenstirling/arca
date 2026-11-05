using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Arca;

/// <summary>
/// Asks Windows for the type name ("Text Document") and icon it uses for a file name, the same ones File Explorer shows.
/// Only the name's extension is used (SHGFI_USEFILEATTRIBUTES): nothing on disk is read and nothing in the archive is run.
/// Results are cached per extension. Call on the UI thread.
/// </summary>
internal static class ShellInfo
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_TYPENAME = 0x400;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string name, bool folder) => folder ? "<folder>" : Path.GetExtension(name);

    private static (string Path, uint Attributes) Probe(string name, bool folder) =>
        folder ? ("folder", FILE_ATTRIBUTE_DIRECTORY) : ("file" + Path.GetExtension(name), FILE_ATTRIBUTE_NORMAL);

    public static string TypeName(string name, bool folder)
    {
        var key = Key(name, folder);
        if (TypeNames.TryGetValue(key, out var cached)) return cached;

        var (path, attributes) = Probe(name, folder);
        var info = new SHFILEINFO();
        string result = "";
        try
        {
            if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES) != IntPtr.Zero)
                result = info.szTypeName ?? "";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }

        if (string.IsNullOrWhiteSpace(result))
        {
            var ext = Path.GetExtension(name);
            result = folder ? "File folder" : ext.Length > 1 ? ext[1..].ToUpperInvariant() + " File" : "File";
        }
        TypeNames[key] = result;
        return result;
    }

    public static ImageSource? Icon(string name, bool folder)
    {
        var key = Key(name, folder);
        if (Icons.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        var (path, attributes) = Probe(name, folder);
        var info = new SHFILEINFO();
        try
        {
            if (SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_USEFILEATTRIBUTES) != IntPtr.Zero
                && info.hIcon != IntPtr.Zero)
            {
                try
                {
                    using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
                    using var bitmap = icon.ToBitmap();
                    using var png = new MemoryStream();
                    bitmap.Save(png, ImageFormat.Png);
                    png.Position = 0;
                    var bitmapImage = new BitmapImage();
                    bitmapImage.SetSource(png.AsRandomAccessStream());
                    image = bitmapImage;
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            image = null; // no icon is better than a crash
        }

        Icons[key] = image;
        return image;
    }
}

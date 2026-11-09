using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nexus.App.Services;

/// <summary>Shell icons by file extension, cached (one lookup per extension, never per file).</summary>
public sealed class IconProvider
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new();

    public ImageSource? Get(string extension, bool isDirectory) =>
        _cache.GetOrAdd(isDirectory ? "<dir>" : extension, _ => Load(extension, isDirectory));

    private static ImageSource? Load(string ext, bool isDirectory)
    {
        var info = new Native.SHFILEINFO();
        string fake = isDirectory ? "folder" : ext.Length == 0 ? "file" : "file." + ext;
        uint attrs = isDirectory ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
        Native.SHGetFileInfo(fake, attrs, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info),
            Native.SHGFI_ICON | Native.SHGFI_SMALLICON | Native.SHGFI_USEFILEATTRIBUTES);
        if (info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { Native.DestroyIcon(info.hIcon); }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nexus.Core.Indexing;
using Nexus.Search;

namespace Nexus.App.Widget;

/// <summary>
/// The applications of the Start menu, as Windows lists them: desktop programs, Store apps and game shortcuts, each with a
/// launch target that works. Read on a background thread; searching it costs nothing.
/// </summary>
public sealed class AppCatalog(INexusLog log)
{
    private const string AppsFolder = @"shell:AppsFolder";
    private const long RefreshAfterMs = 5 * 60 * 1000;

    private readonly ConcurrentDictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<LaunchableApp> _apps = [];
    private long _loadedTick = long.MinValue / 2;
    private int _loading;

    public IReadOnlyList<LaunchableApp> Apps => _apps;

    /// <summary>Never blocks: the list on hand is used until the new one is ready.</summary>
    public void RefreshIfStale()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _loadedTick) < RefreshAfterMs) return;
        if (Interlocked.Exchange(ref _loading, 1) != 0) return;
        var thread = new Thread(Load) { IsBackground = true, Name = "NexusAppCatalog", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA); // the Shell objects require it
        thread.Start();
    }

    private void Load()
    {
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic items = ((dynamic)shell!).Namespace(AppsFolder).Items();
            int count = items.Count;
            var list = new List<LaunchableApp>(count);
            for (int i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                string name = item.Name, target = item.Path;
                if (AppMatcher.IsLaunchable(name, target)) list.Add(new LaunchableApp(name, target));
            }
            _apps = list;
            Interlocked.Exchange(ref _loadedTick, Environment.TickCount64);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or ArgumentNullException)
        {
            log.Warn("Application list unavailable: " + ex.Message);
        }
        finally
        {
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
            Volatile.Write(ref _loading, 0);
        }
    }

    public static void Launch(LaunchableApp app)
    {
        bool direct = app.Target.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(app.Target);
        var start = direct
            ? new ProcessStartInfo(app.Target) { UseShellExecute = true }
            : new ProcessStartInfo("explorer.exe", $"{AppsFolder}\\{app.Target}") { UseShellExecute = true };
        Process.Start(start);
    }

    /// <summary>Only the icons of the rows actually shown are ever loaded.</summary>
    public Task<ImageSource?> GetIconAsync(LaunchableApp app) =>
        _icons.TryGetValue(app.Target, out var cached)
            ? Task.FromResult(cached)
            : Task.Run(() => _icons.GetOrAdd(app.Target, _ => LoadIcon(app.Target)));

    // ------------------------------------------------------------ icon of a Shell item

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr context, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr handle, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);

    private const int SIIGBF_BIGGERSIZEOK = 1, SIIGBF_ICONONLY = 4;

    private static ImageSource? LoadIcon(string target)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            var id = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName($"{AppsFolder}\\{target}", IntPtr.Zero, ref id, out factory);
            if (factory.GetImage(new SIZE { cx = 32, cy = 32 }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out bitmap) != 0) return null;
            if (GetObjectW(bitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmBits == IntPtr.Zero || info.bmBitsPixel != 32) return null;

            // A device-independent section: its rows are stored bottom-up when the height is positive.
            int stride = info.bmWidthBytes, height = Math.Abs(info.bmHeight);
            var pixels = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                int source = info.bmHeight > 0 ? height - 1 - y : y;
                Marshal.Copy(info.bmBits + source * stride, pixels, y * stride, stride);
            }
            var image = BitmapSource.Create(info.bmWidth, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or ArgumentException or UnauthorizedAccessException)
        {
            return null; // the row then keeps the generic program icon
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }
}

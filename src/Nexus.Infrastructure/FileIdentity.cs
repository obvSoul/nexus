using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Infrastructure;

/// <summary>NTFS identity of a file: two paths with the same identity are hard links to one single copy of the data.</summary>
public static class FileIdentity
{
    private const uint FILE_READ_ATTRIBUTES = 0x80, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    /// <summary>Returns (volume, file index), or null if the file cannot be opened. Only reads attributes.</summary>
    public static (uint Volume, ulong Index)? TryGet(string path)
    {
        string p = path.Length >= 260 && !path.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\?\" + path : path;
        using var h = CreateFile(p, FILE_READ_ATTRIBUTES, 7 /*share all*/, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid || !GetFileInformationByHandle(h, out var i)) return null;
        return (i.VolumeSerialNumber, ((ulong)i.FileIndexHigh << 32) | i.FileIndexLow);
    }
}

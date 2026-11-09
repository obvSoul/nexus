using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Infrastructure;

public enum DiskKind { Unknown, Ssd, Hdd }

/// <summary>Detects SSD vs HDD with the "seek penalty" storage property. Needs no administrator rights.</summary>
public static class DiskTypeProbe
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery { public int PropertyId; public int QueryType; public byte AdditionalParameters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyDescriptor { public uint Version; public uint Size; [MarshalAs(UnmanagedType.U1)] public bool IncursSeekPenalty; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref StoragePropertyQuery inBuf, int inSize,
        out SeekPenaltyDescriptor outBuf, int outSize, out int returned, IntPtr overlapped);

    /// <param name="driveLetter">e.g. 'C'</param>
    public static DiskKind Probe(char driveLetter)
    {
        try
        {
            using var h = CreateFile($@"\\.\{char.ToUpperInvariant(driveLetter)}:", 0, 3 /*share r/w*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
            if (h.IsInvalid) return DiskKind.Unknown;
            var query = new StoragePropertyQuery { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = 0 };
            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, ref query, Marshal.SizeOf<StoragePropertyQuery>(),
                    out var d, Marshal.SizeOf<SeekPenaltyDescriptor>(), out _, IntPtr.Zero)) return DiskKind.Unknown;
            return d.IncursSeekPenalty ? DiskKind.Hdd : DiskKind.Ssd;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { return DiskKind.Unknown; }
    }
}

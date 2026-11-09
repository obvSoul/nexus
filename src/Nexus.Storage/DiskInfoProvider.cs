using Nexus.Infrastructure;

namespace Nexus.Storage;

public sealed record DiskInfoItem(string Root, string Label, string FileSystem, DiskKind Kind, long Total, long Free)
{
    public long Used => Total - Free;
    public double PercentUsed => Total == 0 ? 0 : 100.0 * Used / Total;
}

public static class DiskInfoProvider
{
    /// <summary>Fixed (internal) drives that are ready. Root is like "C:".</summary>
    public static List<DiskInfoItem> GetFixedDisks()
    {
        var list = new List<DiskInfoItem>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                list.Add(new DiskInfoItem(d.Name.TrimEnd('\\'), d.VolumeLabel, d.DriveFormat,
                    DiskTypeProbe.Probe(d.Name[0]), d.TotalSize, d.AvailableFreeSpace));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }
}

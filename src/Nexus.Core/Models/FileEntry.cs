namespace Nexus.Core.Models;

/// <summary>
/// One indexed file or folder. Times are UTC ticks (0 = unknown).
/// <see cref="Directory"/> never ends with a backslash (a drive root is stored as "C:").
/// </summary>
public sealed record FileEntry(
    long Id,
    string Directory,
    string Name,
    string Extension,
    long Size,
    long CreatedTicks,
    long ModifiedTicks,
    long AccessedTicks,
    bool IsDirectory,
    bool IsReparsePoint = false)
{
    public string FullPath => Directory + "\\" + Name;

    public DateTime? ModifiedLocal => ModifiedTicks <= 0 ? null : new DateTime(ModifiedTicks, DateTimeKind.Utc).ToLocalTime();

    /// <summary>Lower-case extension without dot ("" for none).</summary>
    public static string ExtensionOf(string name)
    {
        int i = name.LastIndexOf('.');
        return i <= 0 || i == name.Length - 1 ? "" : name[(i + 1)..].ToLowerInvariant();
    }
}

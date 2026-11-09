namespace Nexus.Core;

/// <summary>User preferences, stored as JSON in the database.</summary>
public sealed record NexusSettings
{
    public bool ThrottleIndexer { get; init; } = true;
    public bool WatchChanges { get; init; } = true;
    public bool ScanAtStartup { get; init; } = true;
    public bool ContentIndexEnabled { get; init; } = true;
    public bool UseWindowsSearch { get; init; } = true;

    public WidgetSettings Widget { get; init; } = new();

    /// <summary>Folders never indexed: a full path, or a bare folder name that applies everywhere.</summary>
    public List<string> Exclusions { get; init; } = [];

    /// <summary>Folders whose file contents are indexed. Null = the default user folders.</summary>
    public List<string>? ContentFolders { get; init; }

    public static IReadOnlyList<string> DefaultContentFolders =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    ];
}

namespace Nexus.AppAnalyzer;

public enum AppSource { Registry, Store, Steam, Epic, ProgramFiles }

/// <summary>How a size was obtained. A UI must never present Estimated or Declared as an exact measurement.</summary>
public enum SizeKind
{
    Unknown,
    /// <summary>Sum of the files of the folder, from the index.</summary>
    Measured,
    /// <summary>Value written by the installer or game launcher (can be wrong or outdated).</summary>
    Declared,
    /// <summary>Inferred (folder guessed from the name, folder shared with another app…).</summary>
    Estimated,
}

public enum Confidence { High, Possible }

/// <summary>What a detector found, before any size is attached.</summary>
public sealed record DetectedApp(
    string Id,
    string Name,
    string Publisher,
    string Version,
    DateTime? InstallDate,
    string? InstallLocation,
    bool LocationInferred,
    string? ExePath,
    long? DeclaredSize,
    AppSource Source,
    bool IsSystem,
    string? StoreFamily = null);

public sealed record DataFolder(string Path, long Size, Confidence Confidence, string Reason);

public sealed record AppInfo(
    string Id,
    string Name,
    string Publisher,
    string Version,
    DateTime? InstallDate,
    bool InstallDateFromFolder,
    string? InstallPath,
    AppSource Source,
    bool IsSystem,
    long? DeclaredSize,
    long ProgramSize,
    SizeKind ProgramKind,
    IReadOnlyList<DataFolder> DataFolders,
    long DataSize,
    long CacheSize,
    long PossibleDataSize,
    long TotalEstimated,
    IReadOnlyList<string> Notes);

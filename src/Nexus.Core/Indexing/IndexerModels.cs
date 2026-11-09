namespace Nexus.Core.Indexing;

public enum IndexerState { Idle, Scanning, Paused, Stopped }

/// <summary>Immutable snapshot of the indexer, safe to hand to the UI.</summary>
public sealed record IndexerStatus(
    IndexerState State,
    long IndexedFiles,
    long DirsScanned,
    long DirsSkipped,
    long AccessDenied,
    string CurrentPath,
    bool Watching,
    DateTime? LastScanUtc,
    string Message)
{
    public static IndexerStatus Initial { get; } = new(IndexerState.Stopped, 0, 0, 0, 0, "", false, null, "");
}

public sealed record ActivityItem(DateTime TimeUtc, string Text);

/// <summary>Technical log sink (file in %LOCALAPPDATA%\Nexus\logs in the app).</summary>
public interface INexusLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

public sealed class NullLog : INexusLog
{
    public static NullLog Instance { get; } = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

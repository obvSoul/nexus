using Nexus.Core.Indexing;

namespace Nexus.Infrastructure;

/// <summary>Thread-safe rolling text log (2 MB per file, 2 archives).</summary>
public sealed class FileLog : INexusLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _path;

    public FileLog(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "nexus.log");
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus", "logs");

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes) Rotate();
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { /* logging must never break the app */ }
        catch (UnauthorizedAccessException) { }
    }

    private void Rotate()
    {
        string a1 = Path.ChangeExtension(_path, ".1.log"), a2 = Path.ChangeExtension(_path, ".2.log");
        if (File.Exists(a2)) File.Delete(a2);
        if (File.Exists(a1)) File.Move(a1, a2);
        File.Move(_path, a1);
    }
}

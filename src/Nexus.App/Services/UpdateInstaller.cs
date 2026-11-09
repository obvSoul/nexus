using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace Nexus.App.Services;

/// <summary>
/// Updates from a NEXUS installer chosen by the user. NEXUS works without any network access and has no update server, so
/// nothing is downloaded or checked online. The installer replaces the program in place and keeps the index and preferences.
/// </summary>
public static class UpdateInstaller
{
    public static Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static (Version? Version, string? Error) Inspect(string path)
    {
        FileVersionInfo info;
        try { info = FileVersionInfo.GetVersionInfo(path); }
        catch (FileNotFoundException) { return (null, "Fichier introuvable."); }
        return Nexus.Core.UpdateCheck.Evaluate(info.ProductName, info.ProductVersion, CurrentVersion);
    }

    public static void RunAndExit(string path)
    {
        Process.Start(new ProcessStartInfo(path, "/SILENT /SP- /NOCANCEL") { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}

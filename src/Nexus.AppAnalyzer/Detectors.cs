using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Nexus.AppAnalyzer;

/// <summary>Locations that must never be taken as "the folder of one application".</summary>
public static class KnownPaths
{
    public static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    public static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string LocalLow => Path.Combine(Profile, "AppData", "LocalLow");
    public static string UserPrograms => Path.Combine(Local, "Programs");

    public static string Clean(string? p) => (p ?? "").Trim().Trim('"').TrimEnd('\\', '/').Replace('/', '\\');

    public static bool IsGenericLocation(string? path)
    {
        string p = Clean(path);
        if (p.Length <= 3) return true; // empty or drive root
        string[] generic = [ProgramFiles, ProgramFilesX86, Windows, ProgramData, Profile, Roaming, Local, LocalLow, UserPrograms,
            Path.Combine(Windows, "System32"), Path.Combine(Windows, "Installer"), Path.Combine(Windows, "SysWOW64")];
        if (generic.Any(g => g.Length > 0 && p.Equals(Clean(g), StringComparison.OrdinalIgnoreCase))) return true;
        return p.StartsWith(Windows + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Classic installed programs: the Uninstall registry keys (machine 64-bit, machine 32-bit, current user).</summary>
public static class RegistryAppDetector
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly Regex UpdateName = new(@"^(Security Update|Update|Hotfix|Service Pack)\b|\bKB\d{6,}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static List<DetectedApp> Detect()
    {
        var list = new List<DetectedApp>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(UninstallPath);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if (k is null) continue;
                    var app = FromKey(name, k);
                    if (app is not null) list.Add(app);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return list;
    }

    private static DetectedApp? FromKey(string keyName, RegistryKey k)
    {
        string? name = (k.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(name) || UpdateName.IsMatch(name)) return null;
        if (k.GetValue("ParentKeyName") is not null) return null; // an update of another product
        if (keyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) return null; // the Steam detector knows these better

        bool system = k.GetValue("SystemComponent") is int sc && sc == 1;
        string publisher = (k.GetValue("Publisher") as string)?.Trim() ?? "";
        string version = (k.GetValue("DisplayVersion") as string)?.Trim() ?? "";
        DateTime? date = ParseInstallDate(k.GetValue("InstallDate") as string);

        long? declared = k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;

        string? exe = ExeOf(k.GetValue("DisplayIcon") as string) ?? ExeOf(k.GetValue("UninstallString") as string);
        string loc = KnownPaths.Clean(k.GetValue("InstallLocation") as string);
        bool inferred = false;
        // The launcher declares the parent folder that also holds every game; its own files are in "Launcher".
        if (loc.EndsWith(@"\Epic Games", StringComparison.OrdinalIgnoreCase) && Directory.Exists(loc + @"\Launcher")) loc += @"\Launcher";
        if (loc.Length == 0 || !Path.IsPathRooted(loc) || KnownPaths.IsGenericLocation(loc))
        {
            loc = "";
            string? dir = exe is null ? null : Path.GetDirectoryName(exe);
            // The folder of the app's own executable is a good guess, unless it is a shared/system folder or an installer cache.
            if (dir is not null && !KnownPaths.IsGenericLocation(dir) && !dir.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase))
            {
                loc = KnownPaths.Clean(dir);
                inferred = true;
            }
        }
        return new DetectedApp("reg:" + keyName, name, publisher, version, date, loc.Length == 0 ? null : loc, inferred, exe, declared,
            AppSource.Registry, system);
    }

    public static DateTime? ParseInstallDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParseExact(s.Trim(), ["yyyyMMdd", "yyyy/MM/dd", "yyyy-MM-dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>First executable path in a DisplayIcon / UninstallString value ("C:\x\a.exe,0", "\"C:\x\u.exe\" /S").</summary>
    public static string? ExeOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        var m = Regex.Match(v, "^\"([^\"]+)\"|^([^,]+?\\.(?:exe|ico|dll))", RegexOptions.IgnoreCase);
        string? p = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
        if (p is null) return null;
        p = Environment.ExpandEnvironmentVariables(p).Split(',')[0].Trim();
        return Path.IsPathRooted(p) ? p : null;
    }
}

/// <summary>Microsoft Store (MSIX/Appx) packages, read from the package repository in the registry.</summary>
public static class StoreAppDetector
{
    private const string RepoPath = @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    private static readonly Regex Framework = new(
        @"VCLibs|\.NET\.Native|NETFramework|UI\.Xaml|WindowsAppRuntime|WinAppRuntime|Microsoft\.Services\.Store|DirectXRuntime|\.Runtime\b|Framework|Microsoft\.WinJS|Microsoft\.MicrosoftEdge\.Stable|Microsoft\.VCLibs",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, uint outputLength, IntPtr reserved);

    public static List<DetectedApp> Detect()
    {
        var list = new List<DetectedApp>();
        // The per-user repository (HKCU) is the one that lists the packages; HKLM holds almost nothing.
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var root = baseKey.OpenSubKey(RepoPath);
                if (root is null) continue;
                foreach (var fullName in root.GetSubKeyNames())
                {
                    var app = FromPackage(fullName, root);
                    if (app is not null) list.Add(app);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return list;
    }

    /// <summary>Package full name: Name_Version_Arch_ResourceId_PublisherId. Returns the parts, or null if malformed.</summary>
    public static (string Name, string Version, string ResourceId, string PublisherId)? SplitFullName(string full)
    {
        var parts = full.Split('_');
        return parts.Length == 5 ? (parts[0], parts[1], parts[3], parts[4]) : null;
    }

    /// <summary>System packages live under C:\Windows, or carry well-known technical names.</summary>
    public static bool IsSystemPackage(string name, string? root) =>
        (root is not null && root.StartsWith(KnownPaths.Windows + "\\", StringComparison.OrdinalIgnoreCase))
        || SystemName.IsMatch(name);

    private static readonly Regex SystemName = new(@"^(Windows\.|MicrosoftWindows\.|Microsoft\.AAD\.|NcsiUwpApp|Microsoft\.LanguageExperiencePack|[0-9a-f]{8}-)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static DetectedApp? FromPackage(string fullName, RegistryKey root)
    {
        var parts = SplitFullName(fullName);
        if (parts is null) return null;
        var (name, version, resourceId, pubId) = parts.Value;
        if (resourceId.Length > 0 || Framework.IsMatch(name)) return null; // language/scale packs and shared frameworks are not apps

        using var k = root.OpenSubKey(fullName);
        string? path = (k?.GetValue("PackageRootFolder") as string) is { Length: > 0 } r ? r : k?.GetValue("Path") as string;
        string display = Resolve(k?.GetValue("DisplayName") as string, fullName, name) ?? NameNormalizer.Humanize(name);
        // The prefix of the package name is the only publisher hint; many are technical ids ("5319275A") and are not shown.
        string publisher = name.Contains('.') ? name[..name.IndexOf('.')] : "";
        if (publisher.Length > 0 && (char.IsDigit(publisher[0]) || publisher.Contains('-'))) publisher = "";
        string? location = string.IsNullOrWhiteSpace(path) ? null : KnownPaths.Clean(path);
        return new DetectedApp("store:" + fullName, display, publisher, version, null, location, false, null, null, AppSource.Store,
            IsSystemPackage(name, location), StoreFamily: name + "_" + pubId);
    }

    /// <summary>Turns "ms-resource:AppStoreName" into the real localized name, when Windows can resolve it.</summary>
    private static string? Resolve(string? value, string fullName, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) && !value.StartsWith('@')) return value;

        var attempts = new List<string>();
        if (value.StartsWith('@')) attempts.Add(value);
        else
        {
            string key = value["ms-resource:".Length..].TrimStart('/');
            attempts.Add($"@{{{fullName}?ms-resource://{name}/Resources/{key}}}");
            attempts.Add($"@{{{fullName}?ms-resource://{name}/{key}}}");
        }
        foreach (var source in attempts)
        {
            try
            {
                var sb = new StringBuilder(256);
                if (SHLoadIndirectString(source, sb, (uint)sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0) return sb.ToString();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }
        return null;
    }
}

/// <summary>Steam games: libraries from libraryfolders.vdf, one appmanifest_*.acf per installed game.</summary>
public static class SteamDetector
{
    public static string? FindSteamPath()
    {
        foreach (var (hive, sub, value) in new[] { (RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamPath"), (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") })
        {
            try
            {
                using var k = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(sub);
                if (k?.GetValue(value) is string s && Directory.Exists(s)) return KnownPaths.Clean(s);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static List<DetectedApp> Detect(string? steamPath = null)
    {
        var list = new List<DetectedApp>();
        steamPath ??= FindSteamPath();
        if (steamPath is null) return list;
        var libraries = new List<string> { steamPath };
        try
        {
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf)) libraries.AddRange(ParseLibraries(File.ReadAllText(vdf)));
        }
        catch (IOException) { }
        foreach (var lib in libraries.Select(KnownPaths.Clean).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in SafeFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var app = ParseManifest(File.ReadAllText(acf), apps);
                    if (app is not null) list.Add(app);
                }
                catch (IOException) { }
            }
        }
        return list;
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.GetFiles(dir, pattern); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static List<string> ParseLibraries(string vdfText)
    {
        var root = Vdf.Parse(vdfText);
        var folders = Vdf.GetObject(root, "libraryfolders");
        var paths = new List<string>();
        if (folders is null) return paths;
        foreach (var entry in folders.Values)
        {
            if (entry is Dictionary<string, object> d && d.TryGetValue("path", out var p) && p is string s && s.Length > 0) paths.Add(s);
            else if (entry is string legacy && Path.IsPathRooted(legacy)) paths.Add(legacy); // very old format: "1" "D:\\Games"
        }
        return paths;
    }

    public static DetectedApp? ParseManifest(string acfText, string steamappsDir)
    {
        var state = Vdf.GetObject(Vdf.Parse(acfText), "AppState");
        if (state is null) return null;
        string? id = Vdf.GetString(state, "appid"), name = Vdf.GetString(state, "name"), dir = Vdf.GetString(state, "installdir");
        if (id is null || name is null || dir is null) return null;
        long? size = long.TryParse(Vdf.GetString(state, "SizeOnDisk"), out var sz) && sz > 0 ? sz : null;
        return new DetectedApp("steam:" + id, name, "Steam", Vdf.GetString(state, "buildid") is { } b ? "build " + b : "", null,
            Path.Combine(steamappsDir, "common", dir), false, null, size, AppSource.Steam, false);
    }
}

/// <summary>Epic Games Store games: one .item JSON manifest per installed game.</summary>
public static class EpicDetector
{
    public static List<DetectedApp> Detect(string? manifestDir = null)
    {
        var list = new List<DetectedApp>();
        manifestDir ??= Path.Combine(KnownPaths.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        string[] files = [];
        try { if (Directory.Exists(manifestDir)) files = Directory.GetFiles(manifestDir, "*.item"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var f in files)
        {
            try
            {
                var app = ParseManifest(File.ReadAllText(f), Path.GetFileNameWithoutExtension(f));
                if (app is not null) list.Add(app);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        list.AddRange(FromFolders(new[] { KnownPaths.ProgramFiles, KnownPaths.ProgramFilesX86 }.Select(r => Path.Combine(r, "Epic Games")), list));
        return list;
    }

    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
        { "Launcher", "DirectXRedist", "GameInputRedist", "Epic Online Services" };

    /// <summary>Games sitting in an "Epic Games" folder without a manifest (the manifests can be missing).</summary>
    public static List<DetectedApp> FromFolders(IEnumerable<string> epicRoots, IReadOnlyList<DetectedApp> known)
    {
        var found = new List<DetectedApp>();
        foreach (var root in epicRoots)
        {
            string[] dirs;
            try { if (!Directory.Exists(root)) continue; dirs = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var d in dirs)
            {
                if (NotGames.Contains(Path.GetFileName(d))) continue;
                if (known.Any(k => string.Equals(k.InstallLocation, d, StringComparison.OrdinalIgnoreCase))) continue;
                found.Add(new DetectedApp("epic-folder:" + d, Path.GetFileName(d), "Epic Games", "", null, d, false, null, null, AppSource.Epic, false));
            }
        }
        return found;
    }

    public static DetectedApp? ParseManifest(string json, string id)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? name = Str(r, "DisplayName"), loc = Str(r, "InstallLocation");
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(loc)) return null;
        if (r.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True) return null;
        long? size = r.TryGetProperty("InstallSize", out var s) && s.ValueKind == JsonValueKind.Number && s.GetInt64() > 0 ? s.GetInt64() : null;
        return new DetectedApp("epic:" + id, name, "Epic Games", Str(r, "AppVersionString") ?? "", null, KnownPaths.Clean(loc), false, null, size, AppSource.Epic, false);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Sub-folders of Program Files that no registered application claims (portable apps, leftovers).</summary>
public static class ProgramFilesDetector
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Common Files", "WindowsApps", "ModifiableWindowsApps", "Windows NT", "Windows Defender", "Windows Defender Advanced Threat Protection",
        "Windows Mail", "Windows Media Player", "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar",
        "Internet Explorer", "WindowsPowerShell", "Uninstall Information", "Microsoft Update Health Tools", "MSBuild", "Reference Assemblies",
        "desktop.ini", "Application Verifier",
    };

    /// <param name="claimed">Install folders already attached to a detected application.</param>
    public static List<DetectedApp> Detect(IEnumerable<string> claimed)
    {
        var taken = claimed.Select(KnownPaths.Clean).ToList();
        var list = new List<DetectedApp>();
        foreach (var root in new[] { KnownPaths.ProgramFiles, KnownPaths.ProgramFilesX86, KnownPaths.UserPrograms }.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var d in dirs)
            {
                string name = Path.GetFileName(d);
                if (Skip.Contains(name)) continue;
                // already owned by an app (same folder, or an app installed inside / above it)
                if (taken.Any(t => t.Equals(d, StringComparison.OrdinalIgnoreCase)
                                   || t.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)
                                   || d.StartsWith(t + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new DetectedApp("pf:" + d, name, "", "", null, d, false, null, null, AppSource.ProgramFiles, false));
            }
        }
        return list;
    }
}

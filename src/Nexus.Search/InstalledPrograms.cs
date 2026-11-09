using System.Diagnostics;
using System.IO;
using System.Text;

namespace Nexus.Search;

/// <summary>
/// Programs missing from the Start menu because their installer created no shortcut. A program folder holds many
/// executables, so only the one named after its folder counts: VirtualBox.exe, not VBoxManage.exe.
/// </summary>
public static class InstalledPrograms
{
    private const int MaxDepth = 3;
    private const int GuiSubsystem = 2;
    private static readonly string[] SkippedFolders = ["Common Files", "WindowsApps"];
    private static readonly string[] AuxiliaryWords = ["unins", "setup", "install", "update", "crash", "helper", "report", "repair", "service", "config", "container", "broker", "monitor", "driver"];
    private static readonly EnumerationOptions Listing = new() { IgnoreInaccessible = true, RecurseSubdirectories = false };

    private sealed record Search(HashSet<string> Names, HashSet<string> KnownWords, HashSet<string> Files, List<string> Declared, List<LaunchableApp> Found);

    /// <summary>
    /// The main program of each folder under <paramref name="roots"/> that <paramref name="known"/> does not already list.
    /// <paramref name="declared"/> holds the names in the Windows list of installed programs: "cedarls.exe" is the program
    /// of "CEDARLS 1.5 beta" even in a folder named "CEDAR Logic".
    /// </summary>
    public static List<LaunchableApp> Find(IEnumerable<string> roots, IReadOnlyList<LaunchableApp> known, IEnumerable<string>? declared = null)
    {
        var search = new Search(
            known.Select(a => Normalize(a.Name)).ToHashSet(),
            known.SelectMany(a => Words(a.Name)).Where(w => w.Length >= 4).ToHashSet(),
            known.Select(a => FileName(a.Target)).Where(f => f.EndsWith(".exe", StringComparison.Ordinal)).ToHashSet(),
            (declared ?? []).Select(Normalize).ToList(),
            []);
        foreach (string root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string folder in Directory.EnumerateDirectories(root, "*", Listing))
                Visit(folder, null, 1, search);
        return search.Found;
    }

    private static void Visit(string folder, string? parent, int depth, Search search)
    {
        string name = Path.GetFileName(folder);
        if (SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

        string? exe = MainProgram(folder, name, parent, search.Declared);
        if (exe is not null && search.Files.Add(FileName(exe)))
        {
            string stem = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            string title = Title(exe, name);
            if (!search.KnownWords.Contains(stem) && search.Names.Add(Normalize(title))) search.Found.Add(new LaunchableApp(title, exe));
        }
        if (depth < MaxDepth)
            foreach (string child in Directory.EnumerateDirectories(folder, "*", Listing))
                Visit(child, name, depth + 1, search);
    }

    private static string? MainProgram(string folder, string name, string? parent, List<string> declared)
    {
        string? best = null;
        int bestScore = int.MaxValue;
        foreach (string exe in Directory.EnumerateFiles(folder, "*.exe", Listing))
        {
            string stem = Normalize(Path.GetFileNameWithoutExtension(exe));
            if (AuxiliaryWords.Any(w => stem.Contains(w, StringComparison.Ordinal))) continue;
            int score = Math.Min(Score(stem, name), parent is null ? int.MaxValue : Score(stem, parent));
            if (score == int.MaxValue && stem.Length >= 4 && declared.Any(d => d.StartsWith(stem, StringComparison.Ordinal))) score = 2;
            if (score == int.MaxValue || score > bestScore || (score == bestScore && exe.Length >= best!.Length)) continue;
            if (!IsWindowProgram(exe)) continue;
            best = exe;
            bestScore = score;
        }
        return best;
    }

    private static int Score(string stem, string folder)
    {
        string whole = Normalize(folder);
        if (stem == whole) return 0;
        return stem.Length >= 4 && Words(folder).Contains(stem) ? 1 : int.MaxValue;
    }

    /// <summary>The name the program gives itself when it is recognizably this program, its file name otherwise.</summary>
    private static string Title(string exe, string folder)
    {
        string stem = Path.GetFileNameWithoutExtension(exe);
        string? product = null;
        try { product = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim(); }
        catch (FileNotFoundException) { }
        if (string.IsNullOrEmpty(product)) return stem;
        string normalized = Normalize(product);
        return normalized.Contains(Normalize(stem), StringComparison.Ordinal) || normalized.Contains(Normalize(folder), StringComparison.Ordinal) ? product : stem;
    }

    /// <summary>Reads the Windows subsystem in the executable's header: command-line tools have no window to open.</summary>
    private static bool IsWindowProgram(string exe)
    {
        try
        {
            using var file = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[4096];
            int read = file.Read(header, 0, header.Length);
            if (read < 0x40 || header[0] != 'M' || header[1] != 'Z') return false;
            int pe = BitConverter.ToInt32(header, 0x3C);
            if (pe < 0 || pe + 94 > read || header[pe] != 'P' || header[pe + 1] != 'E') return false;
            return BitConverter.ToUInt16(header, pe + 92) == GuiSubsystem;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FileName(string target) => target[(target.LastIndexOfAny(['\\', '/']) + 1)..].ToLowerInvariant();

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static HashSet<string> Words(string text) =>
        text.ToLowerInvariant().Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
}

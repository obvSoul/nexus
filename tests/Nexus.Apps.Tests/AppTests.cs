using Nexus.AppAnalyzer;
using Nexus.Core.Models;
using Nexus.Database;
using Nexus.Storage;
using Xunit;

namespace Nexus.Apps.Tests;

public class ParsingTests
{
    [Theory]
    [InlineData("7-Zip 23.01 (x64)", "7zip")]
    [InlineData("Git version 2.43.0", "git")]
    [InlineData("Mozilla Firefox (x64 fr)", "mozillafirefox")]
    [InlineData("Office 365", "office365")]
    [InlineData("Node.js", "nodejs")]
    [InlineData("", "")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, NameNormalizer.Normalize(input));

    [Fact]
    public void Publisher_and_product()
    {
        Assert.Equal("google", NameNormalizer.NormalizePublisher("Google LLC"));
        Assert.Equal("microsoft", NameNormalizer.NormalizePublisher("Microsoft Corporation"));
        Assert.Equal("chrome", NameNormalizer.Product("googlechrome", "google"));
        Assert.Equal("firefox", NameNormalizer.Product("firefox", "mozilla"));
    }

    [Fact]
    public void Humanize_store_names() => Assert.Equal("Windows Calculator", NameNormalizer.Humanize("Microsoft.WindowsCalculator"));

    [Fact]
    public void Steam_library_file()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0" { "path" "C:\\Program Files (x86)\\Steam" "apps" { "228980" "1" } }
                "1" { "path" "D:\\Jeux" }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\Jeux"], SteamDetector.ParseLibraries(vdf));
    }

    [Fact]
    public void Steam_manifest()
    {
        const string acf = """
            "AppState" { "appid" "730" "name" "Counter-Strike 2" "installdir" "Counter-Strike Global Offensive" "SizeOnDisk" "1234567" "buildid" "42" }
            """;
        var a = SteamDetector.ParseManifest(acf, @"D:\Jeux\steamapps")!;
        Assert.Equal("Counter-Strike 2", a.Name);
        Assert.Equal(@"D:\Jeux\steamapps\common\Counter-Strike Global Offensive", a.InstallLocation);
        Assert.Equal(1234567, a.DeclaredSize);
        Assert.Null(SteamDetector.ParseManifest("\"Other\" { }", "x"));
    }

    [Fact]
    public void Epic_manifest()
    {
        var a = EpicDetector.ParseManifest("""{"DisplayName":"Fortnite","InstallLocation":"D:\\Epic\\Fortnite\\","InstallSize":1000,"AppVersionString":"1.2"}""", "abc")!;
        Assert.Equal(@"D:\Epic\Fortnite", a.InstallLocation);
        Assert.Equal(1000, a.DeclaredSize);
        Assert.Null(EpicDetector.ParseManifest("""{"DisplayName":"X","InstallLocation":"D:\\X","bIsIncompleteInstall":true}""", "b"));
    }

    [Theory]
    [InlineData(@"C:\Foo\app.exe,0", @"C:\Foo\app.exe")]
    [InlineData("\"C:\\Foo Bar\\unins000.exe\" /SILENT", @"C:\Foo Bar\unins000.exe")]
    [InlineData("MsiExec.exe /X{ABC}", null)]
    [InlineData(null, null)]
    public void Exe_extraction(string? value, string? expected) => Assert.Equal(expected, RegistryAppDetector.ExeOf(value));

    [Fact]
    public void Install_dates_and_package_names()
    {
        Assert.Equal(new DateTime(2024, 3, 9), RegistryAppDetector.ParseInstallDate("20240309"));
        Assert.Null(RegistryAppDetector.ParseInstallDate("garbage"));
        var p = StoreAppDetector.SplitFullName("Microsoft.WindowsCalculator_11.2.0.0_x64__8wekyb3d8bbwe")!.Value;
        Assert.Equal("Microsoft.WindowsCalculator", p.Name);
        Assert.Equal("8wekyb3d8bbwe", p.PublisherId);
        Assert.Null(StoreAppDetector.SplitFullName("nonsense"));
    }

    [Fact]
    public void Shared_system_folders_are_not_application_folders()
    {
        Assert.True(KnownPaths.IsGenericLocation(KnownPaths.ProgramFiles));
        Assert.True(KnownPaths.IsGenericLocation(@"C:\"));
        Assert.True(KnownPaths.IsGenericLocation(Path.Combine(KnownPaths.Windows, "Foo")));
        Assert.False(KnownPaths.IsGenericLocation(Path.Combine(KnownPaths.ProgramFiles, "Foo")));
    }

    [Fact]
    public void Merge_removes_duplicates_and_prefers_launchers()
    {
        DetectedApp R(string id, string name, string? loc, string v = "1") => new(id, name, "", v, null, loc, false, null, null, AppSource.Registry, false);
        var steam = new DetectedApp("steam:1", "Game", "Steam", "", null, @"D:\S\Game", false, null, 5, AppSource.Steam, false);
        var merged = AppAnalyzerService.Merge([R("a", "Game launcher entry", @"D:\S\Game"), R("b", "Tool", null), R("c", "Tool", @"C:\Tool")], [], [steam], []);
        Assert.Equal(["steam:1", "c"], merged.Select(m => m.Id).ToList());
    }

    [Fact]
    public void Real_registry_can_be_read_without_admin_rights()
    {
        Assert.NotEmpty(RegistryAppDetector.Detect());
        var store = StoreAppDetector.Detect();
        Assert.Contains(store, a => !a.IsSystem);
        Assert.DoesNotContain(store, a => a.Name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Resource_packs_are_visible_in_the_package_name_and_system_packages_are_flagged()
    {
        Assert.Equal("split.scale-125", StoreAppDetector.SplitFullName("A.B_1.0.0.0_neutral_split.scale-125_abc")!.Value.ResourceId);
        Assert.True(StoreAppDetector.IsSystemPackage("Foo", Path.Combine(KnownPaths.Windows, "SystemApps", "Foo")));
        Assert.True(StoreAppDetector.IsSystemPackage("Windows.CBSPreview", null));
        Assert.False(StoreAppDetector.IsSystemPackage("Microsoft.Windows.Photos", @"C:\Program Files\WindowsApps\Photos"));
    }

    [Fact]
    public void Epic_games_without_manifest_are_found_by_folder()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexus-epic-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var d in new[] { "Fortnite", "Launcher", "DirectXRedist", "Known" }) Directory.CreateDirectory(Path.Combine(root, d));
            var known = new DetectedApp("epic:k", "Known", "Epic Games", "", null, Path.Combine(root, "Known"), false, null, null, AppSource.Epic, false);
            Assert.Equal("Fortnite", EpicDetector.FromFolders([root], [known]).Single().Name);
        }
        finally { Directory.Delete(root, true); }
    }
}

public sealed class SizerTests : IDisposable
{
    private const string Roaming = @"C:\U\Roaming", Local = @"C:\U\Local", PF = @"C:\PF";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-app-" + Guid.NewGuid().ToString("N"));
    private readonly NexusDatabase _db;
    private readonly AppSizer _sizer;

    public SizerTests()
    {
        _db = new NexusDatabase(Path.Combine(_dir, "a.db"));
        _db.Initialize();
        var rows = new List<FileEntry>();
        void Tree(string path, long size)
        {
            // creates every folder level of `path`, then one file of `size` inside it
            var parts = path.Split('\\');
            for (int i = 1; i < parts.Length; i++)
                rows.Add(new FileEntry(0, string.Join('\\', parts[..i]), parts[i], "", 0, 100, 100, 100, true));
            rows.Add(new FileEntry(0, path, "f.bin", "bin", size, 100, 100, 100, false));
        }
        Tree(@"C:\PF\Google\Chrome", 1000);
        Tree(@"C:\U\Local\Google\Chrome\Cache", 200);
        Tree(@"C:\U\Local\Google\Chrome\Profile", 300);
        Tree(@"C:\U\Local\Google\Earth", 7000);        // same vendor, other product
        Tree(@"C:\U\Roaming\7-Zip", 50);
        Tree(@"C:\PF\7-Zip", 500);
        Tree(@"C:\U\Roaming\Microsoft\Teams", 900);    // inside a generic vendor folder
        Tree(@"C:\U\Roaming\Microsoft\Windows", 800);
        Tree(@"C:\U\Roaming\SharedTool", 70);
        Tree(@"C:\U\Local\Discord\app", 4000);         // installed in Local: data must not be counted twice
        Tree(@"C:\U\Roaming\FooBarExtras", 30);        // only resembles "Foo Bar"
        Tree(@"C:\PF\Foo Bar", 10);
        Tree(@"C:\PF\Guessed App", 600);
        Tree(@"C:\PF\Launcher\bin", 100);
        Tree(@"C:\PF\Launcher\games\One", 9000);
        Tree(@"C:\PF\Launcher\games\One\Mod", 1);      // an app nested in a nested app must not be subtracted twice
        Tree(@"C:\U\Local\Packages\Acme.Notes_abc", 77);
        new FileRepository(_db).UpsertBatch(rows.DistinctBy(r => (r.Directory, r.Name)));
        new DirectoryStatsBuilder(_db).Rebuild();
        _sizer = new AppSizer(new StorageQueries(_db), new TempCacheAnalyzer(_db))
        {
            DataRoots = [Roaming, Local], ProgramRoots = [PF], PackagesRoot = @"C:\U\Local\Packages",
        };
    }

    private static DetectedApp App(string name, string pub = "", string? loc = null, long? declared = null, bool inferred = false,
        AppSource src = AppSource.Registry, string? family = null) =>
        new("id:" + name, name, pub, "1", null, loc, inferred, null, declared, src, false, family);

    [Fact]
    public void Publisher_product_folder_is_attributed_and_caches_are_a_subset()
    {
        var a = _sizer.Build([App("Google Chrome", "Google LLC", @"C:\PF\Google\Chrome")]).Single();
        Assert.Equal(SizeKind.Measured, a.ProgramKind);
        Assert.Equal(1000, a.ProgramSize);
        Assert.Equal(500, a.DataSize);       // not Local\Google as a whole (Earth is another product)
        Assert.Equal(200, a.CacheSize);
        Assert.Equal(1500, a.TotalEstimated); // caches are inside data: not added again
        Assert.Equal(new DateTime(100, DateTimeKind.Utc).ToLocalTime(), a.InstallDate);
        Assert.True(a.InstallDateFromFolder);
    }

    [Fact]
    public void Version_in_the_name_does_not_break_the_match()
    {
        var a = _sizer.Build([App("7-Zip 23.01 (x64)", "Igor Pavlov", @"C:\PF\7-Zip")]).Single();
        Assert.Equal(500, a.ProgramSize);
        Assert.Equal(50, a.DataSize);
    }

    [Fact]
    public void Generic_vendor_folders_are_never_added_blindly()
    {
        var a = _sizer.Build([App("Microsoft Paint Helper", "Microsoft Corporation", @"C:\PF\Guessed App")]).Single();
        Assert.Equal(0, a.DataSize);
        Assert.DoesNotContain(a.DataFolders, f => f.Path.EndsWith("Microsoft"));
    }

    [Fact]
    public void A_folder_claimed_by_two_apps_is_counted_for_none()
    {
        var list = _sizer.Build([App("Alpha", "", @"C:\Z\SharedTool"), App("Beta", "", @"C:\Y\SharedTool")]);
        Assert.All(list, a => Assert.Equal(0, a.DataSize));
        Assert.All(list, a => Assert.Contains(a.Notes, n => n.Contains("plusieurs applications")));
    }

    [Fact]
    public void Data_inside_the_install_folder_is_not_counted_twice()
    {
        var a = _sizer.Build([App("Discord", "Discord Inc.", @"C:\U\Local\Discord")]).Single();
        Assert.Equal(4000, a.ProgramSize);
        Assert.Equal(0, a.DataSize);
        Assert.Equal(4000, a.TotalEstimated);
    }

    [Fact]
    public void Weak_resemblance_is_shown_but_stays_out_of_the_total()
    {
        var a = _sizer.Build([App("Foo Bar", "", @"C:\PF\Foo Bar")]).Single();
        Assert.Equal(0, a.DataSize);
        Assert.Equal(30, a.PossibleDataSize);
        Assert.Equal(10, a.TotalEstimated);
        Assert.Single(a.DataFolders, f => f.Confidence == Confidence.Possible);
    }

    [Fact]
    public void Declared_size_is_labelled_declared_never_measured()
    {
        var a = _sizer.Build([App("Absent", "", @"D:\NotIndexed\Absent", declared: 12345)]).Single();
        Assert.Equal(SizeKind.Declared, a.ProgramKind);
        Assert.Equal(12345, a.ProgramSize);
    }

    [Fact]
    public void Measured_wins_over_declared_and_both_stay_available()
    {
        var a = _sizer.Build([App("7-Zip", "", @"C:\PF\7-Zip", declared: 999999)]).Single();
        Assert.Equal(SizeKind.Measured, a.ProgramKind);
        Assert.Equal(500, a.ProgramSize);
        Assert.Equal(999999, a.DeclaredSize);
    }

    [Fact]
    public void Inferred_or_guessed_or_shared_locations_are_estimates()
    {
        Assert.Equal(SizeKind.Estimated, _sizer.Build([App("X", "", @"C:\PF\7-Zip", inferred: true)]).Single().ProgramKind);

        var guessed = _sizer.Build([App("Guessed App")]).Single();
        Assert.Equal(SizeKind.Estimated, guessed.ProgramKind);
        Assert.Equal(600, guessed.ProgramSize);

        var shared = _sizer.Build([App("A one", "", @"C:\PF\7-Zip"), App("B two", "", @"C:\PF\7-Zip")]);
        Assert.All(shared, s => Assert.Equal(SizeKind.Estimated, s.ProgramKind));
    }

    [Fact]
    public void Apps_installed_inside_another_app_are_not_counted_twice()
    {
        var list = _sizer.Build([App("Launcher", "", @"C:\PF\Launcher"), App("Game One", "", @"C:\PF\Launcher\games\One"), App("Mod", "", @"C:\PF\Launcher\games\One\Mod")]);
        Assert.Equal(100, list[0].ProgramSize);
        Assert.Equal(SizeKind.Measured, list[0].ProgramKind);
        Assert.Contains(list[0].Notes, n => n.Contains("exclues"));
        Assert.Equal(9000, list[1].ProgramSize);
        Assert.Equal(1, list[2].ProgramSize);
    }

    [Fact]
    public void Unknown_when_nothing_is_known()
    {
        var a = _sizer.Build([App("Nothing Here At All")]).Single();
        Assert.Equal(SizeKind.Unknown, a.ProgramKind);
        Assert.Equal(0, a.TotalEstimated);
        Assert.NotEmpty(a.Notes);
    }

    [Fact]
    public void Store_package_data_is_found_by_family_and_program_size_is_honestly_unknown()
    {
        var a = _sizer.Build([App("Notes", "Acme", @"C:\Program Files\WindowsApps\Acme.Notes_1_x64__abc", src: AppSource.Store, family: "Acme.Notes_abc")]).Single();
        Assert.Equal(77, a.DataSize);
        Assert.Equal(SizeKind.Unknown, a.ProgramKind);
        Assert.Contains(a.Notes, n => n.Contains("WindowsApps"));
    }

    public void Dispose()
    {
        NexusDatabase.ReleaseAll();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

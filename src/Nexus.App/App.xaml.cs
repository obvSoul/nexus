using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Nexus.App.Services;
using Nexus.App.ViewModels;
using Nexus.App.Widget;
using Nexus.Core;
using Nexus.Core.Indexing;
using Nexus.Core.Search;
using Nexus.Database;
using Nexus.Indexer;
using Nexus.Infrastructure;
using Nexus.Search;
using Nexus.Storage;

namespace Nexus.App;

public partial class App : Application
{
    private const int DeferredCheckDelayMs = 30_000;

    private ServiceProvider? _services;
    private INexusLog _log = NullLog.Instance;
    private Mutex? _instance;
    private EventWaitHandle? _showRequest;
    private RegisteredWaitHandle? _showWait;
    private MainWindow? _main;
    private MemoryTrimmer? _trimmer;
    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // the main window can be hidden while the widget stays available
        bool background = e.Args.Contains(Autostart.BackgroundArgument);

        // One instance only: two indexers on one database would compete. Also lets the installer detect a running NEXUS.
        // A copy started on another database (NEXUS_DB) cannot compete with the normal one.
        string name = "NexusFileManagerSingleInstance" + (Environment.GetEnvironmentVariable("NEXUS_DB") is null ? "" : "-OtherDb");
        _instance = new Mutex(true, name, out bool first);
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Show");
        if (!first)
        {
            if (!background)
            {
                WidgetNative.AllowSetForegroundWindow(WidgetNative.ASFW_ANY);
                _showRequest.Set(); // the running NEXUS may have no visible window to bring forward
            }
            Shutdown(0);
            return;
        }

        try { _log = new FileLog(FileLog.DefaultDirectory); } catch (Exception) { /* run without a log file */ }
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show("Une erreur inattendue est survenue (détails dans le journal) :\n" + args.Exception.Message,
                "NEXUS", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        string? notice;
        NexusSettings settings;
        NexusDatabase database;
        bool checkLater;
        try
        {
            // Started with Windows, the shortcut must not wait for a full read of the index on a cold disk.
            var (db, n) = DatabaseMaintenance.OpenWithRecovery(NexusDatabase.DefaultPath, _log, deferCheck: background);
            database = db;
            checkLater = background && DatabaseMaintenance.NeedsCheck(db);
            notice = n;
            settings = new SettingsStore(db).Load();
            var current = settings;
            _services = new ServiceCollection()
                .AddSingleton(_log)
                .AddSingleton(db)
                .AddSingleton<FileRepository>()
                .AddSingleton<UserDataRepository>()
                .AddSingleton(new WindowsSearchProvider(_log) { Enabled = current.UseWindowsSearch })
                .AddSingleton<IContentHitProvider>(sp => sp.GetRequiredService<WindowsSearchProvider>())
                .AddSingleton<ISearchEngine, SqliteSearchEngine>()
                .AddSingleton<SettingsStore>()
                .AddSingleton(new ContentIndexer(db, _log))
                .AddSingleton<SettingsViewModel>()
                .AddSingleton<IndexerService>(sp => new IndexerService(db, _log, new IndexerOptions { Watch = current.WatchChanges, AutoScanOnStart = current.ScanAtStartup }))
                .AddSingleton<FileActions>()
                .AddSingleton<IconProvider>()
                .AddSingleton<SearchState>()
                .AddSingleton<AppCatalog>()
                .AddSingleton<SearchWidgetViewModel>()
                .AddSingleton<WidgetService>()
                .AddSingleton<DashboardViewModel>()
                .AddSingleton<SearchPageViewModel>()
                .AddSingleton<IndexationViewModel>()
                .AddSingleton<StorageQueries>()
                .AddSingleton<DirectoryStatsBuilder>()
                .AddSingleton<TempCacheAnalyzer>()
                .AddSingleton<StorageViewModel>()
                .AddSingleton<Nexus.AppAnalyzer.AppSizer>()
                .AddSingleton<Nexus.AppAnalyzer.AppAnalyzerService>()
                .AddSingleton<AppsViewModel>()
                .AddSingleton<Nexus.Duplicates.DuplicateFinder>(_ => new Nexus.Duplicates.DuplicateFinder(db, _log))
                .AddSingleton<DuplicatesViewModel>()
                .AddSingleton<MainViewModel>()
                .AddSingleton<MainWindow>()
                .BuildServiceProvider();
        }
        catch (Exception ex)
        {
            _log.Error("Startup failed", ex);
            MessageBox.Show("Impossible d'ouvrir la base de données :\n" + ex.Message, "NEXUS", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var indexer = _services.GetRequiredService<IndexerService>();
        var trimmer = _trimmer = new MemoryTrimmer();
        var previous = IndexerState.Stopped;
        indexer.StatusChanged += s =>
        {
            if (previous == IndexerState.Scanning && s.State == IndexerState.Idle) Dispatcher.BeginInvoke(trimmer.Request);
            previous = s.State;
        };
        var store = _services.GetRequiredService<SettingsStore>();
        var content = _services.GetRequiredService<ContentIndexer>();
        indexer.SetThrottle(settings.ThrottleIndexer);
        indexer.SetExclusions(settings.Exclusions, rescan: false);
        indexer.ScanFinished += () => content.Request(() => SettingsViewModel.ContentOptionsFrom(store.Load()));

        var widget = _services.GetRequiredService<WidgetService>();
        widget.ShowMainRequested += ShowMain;
        widget.QuitRequested += Quit;
        widget.Hidden += trimmer.Request;
        widget.ShowAllRequested += text =>
        {
            ShowMain();
            _services.GetRequiredService<MainViewModel>().OpenSearch(text);
        };
        widget.Start();
        _log.Info($"Widget ready {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:N0} ms after process start");
        if (checkLater) StartDeferredCheck(database);
        SessionEnding += (_, _) => _quitting = true; // Windows or an installer is closing NEXUS: nothing may hold it back

        _showWait = ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) => Dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, false);

        if (!background || !widget.Enabled) ShowMain(); // started hidden without a widget, NEXUS would be unreachable
        indexer.Start(); // background thread; first scan is delayed a few seconds
        if (notice is not null && !background) MessageBox.Show(notice, "NEXUS", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void StartDeferredCheck(NexusDatabase database)
    {
        var thread = new Thread(() =>
        {
            BackgroundMode.BeginOnCurrentThread();
            Thread.Sleep(DeferredCheckDelayMs); // the session is still opening: leave the disk to it
            try
            {
                if (!DatabaseMaintenance.CheckOrRequestRepair(database, _log))
                    Dispatcher.BeginInvoke(() => MessageBox.Show("L'index de NEXUS est endommagé. Il sera réparé au prochain lancement de NEXUS.", "NEXUS", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            catch (System.IO.IOException ex) { _log.Error("Deferred integrity check failed", ex); }
        }) { IsBackground = true, Name = "NexusDeferredCheck", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>The main window is only built when it is first needed: a NEXUS started with Windows does not pay for it.</summary>
    private MainWindow GetMainWindow()
    {
        if (_main is not null) return _main;
        var window = _main = _services!.GetRequiredService<MainWindow>();
        window.Deactivated += (_, _) => _trimmer?.Request();
        window.StateChanged += (_, _) => { if (window.WindowState == WindowState.Minimized) _trimmer?.Request(); };
        window.Closing += OnMainClosing;
        window.Closed += (_, _) => Shutdown();
        return window;
    }

    private void ShowMain()
    {
        if (_services is null) return;
        var window = GetMainWindow();
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OnMainClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var widget = _services!.GetRequiredService<WidgetService>();
        if (_quitting || !widget.Enabled) return;
        e.Cancel = true;
        _main!.Hide();
        widget.NoticeRunningInBackground();
        _trimmer?.Request();
    }

    private void Quit()
    {
        _quitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _showWait?.Unregister(null);
            _services?.GetService<WidgetService>()?.Dispose();
            _services?.GetService<ContentIndexer>()?.Dispose();
            _services?.GetService<IndexerService>()?.Stop();
        }
        catch (Exception ex) { _log.Error("Indexer stop failed", ex); }
        base.OnExit(e);
    }
}

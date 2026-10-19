using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Zytrus.Core;
using Zytrus.Native;
using Zytrus.UI;

namespace Zytrus;

/// <summary>
/// Background agent: no main window. Tray icon + global drag detection + the citrus slice
/// (every function a segment), plus "Send to" integration and a settings window.
/// </summary>
public partial class App : Application
{
    const string InstanceId = "Zytrus";

    SingleInstance? _instance;
    TrayIcon? _tray;
    GlobalDragWatcher? _watcher;
    CitrusWindow? _citrus;
    JobRunner? _jobs;
    SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new SingleInstance(InstanceId);
        if (!_instance.IsFirst)
        {
            // Let the running instance bring its window to the front.
            Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
            SingleInstance.SendToFirst(InstanceId, e.Args);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("Unhandled: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        System.Windows.Forms.Application.EnableVisualStyles();
        SettingsStore.Load();
        Theme.Initialize(this);
        ShellIntegration.RemoveLegacyEntries();
        ShellIntegration.RefreshRegistrations();

        var jobs = _jobs = new JobRunner();

        // Drag mode: one citrus slice that pops up whenever files are dragged with the trigger key.
        var citrus = _citrus = new CitrusWindow();
        citrus.ActionDropped += (paths, action) => jobs.Enqueue(action, paths);

        var watcher = _watcher = new GlobalDragWatcher(Dispatcher);
        watcher.ShiftDragStarted += point => citrus.ShowForDrag(point);
        watcher.DragEnded += () => citrus.HideSoon();
        ApplyHookSetting();
        SettingsStore.Changed += ApplyHookSetting;

        var tray = _tray = new TrayIcon();
        tray.SettingsRequested += OpenSettings;
        tray.CompressFilesRequested += () =>
        {
            var files = PickFiles();
            if (files != null) jobs.Enqueue(ProcessAction.EmailCompress, files);
        };
        tray.PickForCitrusRequested += () =>
        {
            var files = PickFiles();
            if (files != null) ShowCitrusMenu(files);
        };
        tray.ExitRequested += () => Shutdown();

        _instance.ArgumentsReceived += args => Dispatcher.InvokeAsync(() => HandleArguments(args, fromOtherInstance: true));
        _instance.StartServer();

        if (SettingsStore.IsFirstRun)
        {
            try { ShellIntegration.InstallSendTo(); } catch (Exception ex) { Log.Error("Send-to install", ex); }
            jobs.Toast.ShowInfo("Zytrus ist bereit",
                "Dateien mit gedrückter ⇧-Taste ziehen – die Zitrone erscheint. Datei auf ein Stück ziehen, fertig. " +
                "Klick aufs Symbol unten rechts öffnet die Einstellungen.", 12);
        }

        HandleArguments(e.Args, fromOtherInstance: false);
        Log.Write("Started " + string.Join(' ', e.Args));
    }

    void HandleArguments(string[] args, bool fromOtherInstance)
    {
        var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).Select(a => a.ToLowerInvariant()).ToHashSet();
        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();

        if (paths.Count > 0)
        {
            if (flags.Contains("--menu")) ShowCitrusMenu(paths);
            else _jobs!.Enqueue(ProcessAction.EmailCompress, paths);
            return;
        }

        if (flags.Contains("--settings") || (fromOtherInstance && !flags.Contains("--background")))
            OpenSettings();
    }

    /// <summary>Click mode: files are already chosen, the user clicks a segment.</summary>
    void ShowCitrusMenu(IReadOnlyList<string> paths)
    {
        var menu = new CitrusWindow(paths);
        menu.ActionChosen += action => _jobs!.Enqueue(action, paths);
        menu.ShowForClick();
    }

    void OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    void ApplyHookSetting()
    {
        if (_watcher == null) return;
        if (SettingsStore.Current.DragHookEnabled) _watcher.Start();
        else _watcher.Stop();
    }

    static string[]? PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Dateien auswählen – Zytrus",
            Filter = "Alle Dateien (*.*)|*.*"
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null) SettingsStore.Save(); // only the real instance owns the settings
        _watcher?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

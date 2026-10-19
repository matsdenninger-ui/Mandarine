using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Mandarine.Core;
using Mandarine.Native;
using Mandarine.UI;

namespace Mandarine;

/// <summary>
/// Background agent: no main window. Tray icon + global Shift-drag detection + floating drop zone,
/// plus "Send to" integration for keyboard/context-menu users.
/// </summary>
public partial class App : Application
{
    const string InstanceId = "Mandarine";

    SingleInstance? _instance;
    TrayIcon? _tray;
    GlobalDragWatcher? _watcher;
    DropZoneWindow? _dropZone;
    JobRunner? _jobs;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new SingleInstance(InstanceId);
        if (!_instance.IsFirst)
        {
            // Let the running instance bring its menu to the front.
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
        ShellIntegration.RefreshRegistrations();

        var jobs = _jobs = new JobRunner();

        var dropZone = _dropZone = new DropZoneWindow();
        dropZone.FilesDropped += (paths, options) =>
        {
            if (options) ShowMenu(paths);
            else jobs.Enqueue(ProcessAction.EmailCompress, paths);
        };

        var watcher = _watcher = new GlobalDragWatcher(Dispatcher);
        watcher.ShiftDragStarted += point => dropZone.ShowNear(point);
        watcher.DragEnded += () => dropZone.HideSoon();
        ApplyHookSetting();

        var tray = _tray = new TrayIcon();
        tray.CompressFilesRequested += () =>
        {
            var files = PickFiles();
            if (files != null) jobs.Enqueue(ProcessAction.EmailCompress, files);
        };
        tray.OptionsForFilesRequested += () =>
        {
            var files = PickFiles();
            if (files != null) ShowMenu(files);
        };
        tray.DragHookChanged += ApplyHookSetting;
        tray.ExitRequested += () => Shutdown();

        _instance.ArgumentsReceived += args => Dispatcher.InvokeAsync(() => HandleArguments(args, fromOtherInstance: true));
        _instance.StartServer();

        if (SettingsStore.IsFirstRun)
        {
            try { ShellIntegration.InstallSendTo(); } catch (Exception ex) { Log.Error("Send-to install", ex); }
            jobs.Toast.ShowInfo("Mandarine ist bereit",
                "Dateien mit gedrückter ⇧-Taste ziehen – die Ablagefläche erscheint am Mauszeiger. " +
                "⇧+Alt beim Loslassen öffnet die Optionen. Auch im Rechtsklick-Menü unter „Senden an“.", 12);
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
            if (flags.Contains("--menu")) ShowMenu(paths);
            else _jobs!.Enqueue(ProcessAction.EmailCompress, paths);
            return;
        }

        if (fromOtherInstance && !flags.Contains("--background"))
            _jobs!.Toast.ShowInfo("Mandarine läuft bereits", "Dateien mit gedrückter ⇧-Taste ziehen – oder Rechtsklick → Senden an → Mandarine.");
    }

    void ShowMenu(IReadOnlyList<string> paths)
    {
        var menu = new ActionMenuWindow(paths);
        menu.ActionChosen += action => _jobs!.Enqueue(action, paths);
        menu.ShowAtCursor();
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
            Title = "Dateien auswählen – Mandarine",
            Filter = "Alle Dateien (*.*)|*.*"
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

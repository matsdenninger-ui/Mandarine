using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Zytrus.Core;
using Zytrus.Native;

namespace Zytrus.UI;

/// <summary>
/// The citrus slice: every function is a glass segment. Two ways to use it:
///  • Drag mode – appears while files are dragged with the trigger key; drop a file onto a segment.
///  • Click mode – for "Senden an → Optionen" and the tray: files are already chosen, click a segment.
/// The core in the middle explains the segment under the cursor; dropping there cancels.
/// </summary>
public partial class CitrusWindow : Window
{
    const double BaseSize = 430;

    readonly CitrusView _view = new();
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly IReadOnlyList<string>? _clickPaths;

    string[]? _dragPaths;
    bool _shown;
    bool _dragInside;
    bool _mouseWasDown;
    bool _done;
    int _glassGeneration;

    /// <summary>Drag mode: files were dropped onto a segment.</summary>
    public event Action<string[], ProcessAction>? ActionDropped;

    /// <summary>Click mode: a segment was clicked.</summary>
    public event Action<ProcessAction>? ActionChosen;

    bool ClickMode => _clickPaths != null;

    public CitrusWindow(IReadOnlyList<string>? clickPaths = null)
    {
        InitializeComponent();
        _clickPaths = clickPaths;
        Host.Children.Add(_view);

        SourceInitialized += (_, _) => WindowEffects.MakeToolWindow(this, noActivate: !ClickMode);
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (!_dragInside) HideNow(); };

        if (ClickMode)
        {
            AllowDrop = false;
            ShowActivated = true;
            MouseMove += (_, e) => _view.Hover = _view.HitTest(e.GetPosition(_view));
            MouseLeave += (_, _) => _view.Hover = CitrusView.None;
            MouseLeftButtonUp += OnClick;
            PreviewKeyDown += OnKey;
            Deactivated += (_, _) => Dismiss();
            _poll.Tick += (_, _) => PollOutside();
        }
        else
        {
            DragEnter += OnDragEnter;
            DragOver += OnDragOver;
            DragLeave += OnDragLeave;
            Drop += OnDrop;
        }
    }

    // ---- setup ---------------------------------------------------------------------------------

    void ApplySettings()
    {
        var s = SettingsStore.Current;
        Width = Height = Math.Round(BaseSize * s.CitrusScale);

        string idleTitle, idleSubtitle;
        if (ClickMode)
        {
            var paths = _clickPaths!;
            idleTitle = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : Format.Files(paths.Count);
            idleSubtitle = "Stück anklicken · Esc schließt";
        }
        else
        {
            idleTitle = "Auf ein Stück ziehen";
            idleSubtitle = "Mitte = abbrechen";
        }

        var segments = s.Segments.Select(a =>
        {
            var info = ActionCatalog.Get(a);
            return new CitrusView.Segment(a, info.Glyph, info.ShortLabel, info.Title, ActionCatalog.Describe(a, s), true);
        });
        _view.Configure(segments, s.Variety, s.ShowLabels, idleTitle, idleSubtitle);

        if (ClickMode)
        {
            var available = ActionRunner.AvailableActions(_clickPaths!);
            _view.SetAvailability(available.Contains);
        }
    }

    Anchor PlacementAnchor => SettingsStore.Current.Placement == CitrusPlacement.ScreenCenter
        ? Anchor.CenterOfMonitor
        : Anchor.CenteredOnPoint;

    void Present(Win32.POINT cursor)
    {
        new WindowInteropHelper(this).EnsureHandle();

        // Snapshot what is behind the fruit before it becomes visible – the glass refracts this.
        var rect = WindowEffects.ComputePlacement(this, cursor, PlacementAnchor, 0);
        var backdrop = LiquidGlass.CaptureScreen(rect.X, rect.Y, rect.Width, rect.Height);
        _view.SetGlass(null);

        WindowEffects.Place(this, cursor, PlacementAnchor, 0);
        Host.Opacity = 0;
        Show();
        UpdateLayout();
        WindowEffects.Place(this, cursor, PlacementAnchor, 0); // again with the DPI-adjusted size

        var pop = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut };
        Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        HostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = pop });
        HostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = pop });
        HostRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-25, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        if (backdrop != null) StartGlass(backdrop);
    }

    /// <summary>Renders the liquid glass on a worker thread and fades it in when ready.</summary>
    void StartGlass(LiquidGlass.Backdrop backdrop)
    {
        int generation = ++_glassGeneration;
        var shape = _view.ShapeFor(backdrop.Width, backdrop.Height);
        var parameters = CitrusView.GlassParameters(SettingsStore.Current);

        Task.Run(() => LiquidGlass.Render(backdrop, shape, parameters)).ContinueWith(task =>
        {
            if (task.Exception != null)
            {
                Log.Error("Liquid glass", task.Exception);
                return;
            }
            var result = task.Result;
            Dispatcher.InvokeAsync(() =>
            {
                if (generation == _glassGeneration && IsVisible) _view.SetGlass(result);
            });
        }, TaskScheduler.Default);
    }

    // ---- drag mode -----------------------------------------------------------------------------

    internal void ShowForDrag(Win32.POINT cursor)
    {
        _hideTimer.Stop();
        if (_shown) return;
        _shown = true;
        _dragPaths = null;
        ApplySettings();
        _view.Hover = CitrusView.None;
        Present(cursor);
    }

    /// <summary>Mouse button went up somewhere: wait a moment so a drop can still arrive.</summary>
    public void HideSoon()
    {
        if (!_shown) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void HideNow()
    {
        _hideTimer.Stop();
        _dragInside = false;
        if (!_shown) return;
        _shown = false;

        var duration = TimeSpan.FromMilliseconds(150);
        var fade = new DoubleAnimation(0, duration);
        fade.Completed += (_, _) => { if (!_shown) Hide(); };
        Host.BeginAnimation(OpacityProperty, fade);
        HostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, duration));
        HostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, duration));
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragInside = true;
        _hideTimer.Stop();
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dragPaths == null && e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            _dragPaths = paths;
            var available = ActionRunner.AvailableActions(paths);
            _view.SetAvailability(available.Contains);
        }

        int hit = _view.HitTest(e.GetPosition(_view));
        _view.Hover = hit;

        bool accept = _dragPaths != null && _view.IsSegmentEnabled(hit);
        e.Effects = !accept ? DragDropEffects.None
            : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy   // never Move: originals stay put
            : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
            : DragDropEffects.None;
    }

    void OnDragLeave(object sender, DragEventArgs e)
    {
        _dragInside = false;
        _view.Hover = CitrusView.None;
        if (!Win32.IsKeyDown(Win32.VK_LBUTTON)) HideSoon();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        int hit = _view.HitTest(e.GetPosition(_view));
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        var segment = _view.IsSegmentEnabled(hit) ? _view.SegmentAt(hit) : null;
        e.Effects = segment != null ? DragDropEffects.Copy : DragDropEffects.None;
        HideNow();

        if (segment != null && paths is { Length: > 0 })
        {
            var action = segment.Action;
            Dispatcher.InvokeAsync(() => ActionDropped?.Invoke(paths, action)); // let the drag source finish first
        }
    }

    // ---- click mode ----------------------------------------------------------------------------

    public void ShowForClick()
    {
        ApplySettings();
        _view.Hover = CitrusView.None;
        Present(WindowEffects.CursorPosition());
        Activate();
        Win32.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Focus();
        _mouseWasDown = Win32.IsKeyDown(Win32.VK_LBUTTON) || Win32.IsKeyDown(Win32.VK_RBUTTON);
        _poll.Start();
    }

    void OnClick(object sender, MouseButtonEventArgs e)
    {
        int hit = _view.HitTest(e.GetPosition(_view));
        if (hit == CitrusView.CenterHit) { Dismiss(); return; }
        if (_view.IsSegmentEnabled(hit)) Choose(_view.SegmentAt(hit)!.Action);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
            return;
        }

        int index = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
            _ => -1
        };
        if (_view.IsSegmentEnabled(index))
        {
            Choose(_view.SegmentAt(index)!.Action);
            e.Handled = true;
        }
    }

    /// <summary>Esc and clicks outside the fruit close it, even if Windows refused to give us focus.</summary>
    void PollOutside()
    {
        if (Win32.IsKeyDown(Win32.VK_ESCAPE)) { Dismiss(); return; }

        bool down = Win32.IsKeyDown(Win32.VK_LBUTTON) || Win32.IsKeyDown(Win32.VK_RBUTTON);
        if (down && !_mouseWasDown && Win32.GetCursorPos(out var pt))
        {
            try
            {
                var local = _view.PointFromScreen(new Point(pt.X, pt.Y));
                if (_view.HitTest(local) == CitrusView.None) { Dismiss(); return; }
            }
            catch (InvalidOperationException)
            {
                // not connected to a presentation source any more
            }
        }
        _mouseWasDown = down;
    }

    void Choose(ProcessAction action)
    {
        if (_done) return;
        _done = true;
        _poll.Stop();
        Close();
        ActionChosen?.Invoke(action);
    }

    void Dismiss()
    {
        if (_done) return;
        _done = true;
        _poll.Stop();
        Close();
    }
}

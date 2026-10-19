using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mandarine.Native;

namespace Mandarine.UI;

/// <summary>
/// The "menu bar target" of the macOS original: a small acrylic pill that pops up next to the cursor
/// while you drag files with Shift held. Drop = compress for email, drop with Shift+Alt = options menu.
/// </summary>
public partial class DropZoneWindow : Window
{
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    bool _shown;
    bool _dragInside;

    /// <summary>Paths and whether the options menu was requested (Alt held).</summary>
    public event Action<string[], bool>? FilesDropped;

    public DropZoneWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (!_dragInside) HideNow(); };

        SourceInitialized += (_, _) =>
        {
            WindowEffects.MakeToolWindow(this, noActivate: true);
            bool acrylic = WindowEffects.TryApplyAcrylic(this);
            Root.SetResourceReference(Border.BackgroundProperty, acrylic ? "Brush.SurfaceAcrylic" : "Brush.Surface");
        };

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
    }

    internal void ShowNear(Win32.POINT cursor)
    {
        _hideTimer.Stop();
        new WindowInteropHelper(this).EnsureHandle();
        var anchor = SettingsStore.Current.DropZone == DropZonePlacement.TopCenter ? Anchor.TopCenterOfMonitor : Anchor.AboveRightOfPoint;
        WindowEffects.Place(this, cursor, anchor, 28);
        SetMode(Win32.IsKeyDown(Win32.VK_MENU));

        if (_shown) return;
        _shown = true;
        Body.Opacity = 0;
        Show();
        WindowEffects.Place(this, cursor, anchor, 28); // again with the real, DPI-adjusted size
        Animate(Body, OpacityProperty, 0, 1, 140);
        Animate(BodyScale, System.Windows.Media.ScaleTransform.ScaleXProperty, 0.92, 1, 180);
        Animate(BodyScale, System.Windows.Media.ScaleTransform.ScaleYProperty, 0.92, 1, 180);
    }

    /// <summary>Called when the mouse button goes up; waits a moment so a drop can still arrive.</summary>
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
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) => { if (!_shown) Hide(); };
        Body.BeginAnimation(OpacityProperty, fade);
        DropOutline.BeginAnimation(OpacityProperty, null);
        DropOutline.Opacity = 0;
    }

    void SetMode(bool options)
    {
        Glyph.Text = options ? "" : ""; // More : Mail
        TitleText.Text = options ? "Optionen anzeigen" : "Für E-Mail komprimieren";
        HintText.Text = options
            ? "Loslassen · Konvertieren, Metadaten, Schwärzen"
            : $"Loslassen · Ziel ≤ {Core.Format.Megabytes(SettingsStore.Current.TargetMegabytes)} · ⇧+Alt Optionen";
    }

    void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragInside = true;
        _hideTimer.Stop();
        Animate(DropOutline, OpacityProperty, DropOutline.Opacity, 1, 120);
        Animate(BodyScale, System.Windows.Media.ScaleTransform.ScaleXProperty, BodyScale.ScaleX, 1.04, 140);
        Animate(BodyScale, System.Windows.Media.ScaleTransform.ScaleYProperty, BodyScale.ScaleY, 1.04, 140);
        OnDragOver(sender, e);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        SetMode(e.KeyStates.HasFlag(DragDropKeyStates.AltKey));
        e.Effects = !e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.None
            : e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy   // never Move: originals stay where they are
            : e.AllowedEffects.HasFlag(DragDropEffects.Link) ? DragDropEffects.Link
            : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDragLeave(object sender, DragEventArgs e)
    {
        _dragInside = false;
        Animate(DropOutline, OpacityProperty, DropOutline.Opacity, 0, 120);
        Animate(BodyScale, System.Windows.Media.ScaleTransform.ScaleXProperty, BodyScale.ScaleX, 1, 140);
        Animate(BodyScale, System.Windows.Media.ScaleTransform.ScaleYProperty, BodyScale.ScaleY, 1, 140);
        if (!Win32.IsKeyDown(Win32.VK_LBUTTON)) HideSoon(); // button already released
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        bool options = e.KeyStates.HasFlag(DragDropKeyStates.AltKey);
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = DragDropEffects.Copy;
        HideNow();
        if (paths is { Length: > 0 })
            Dispatcher.InvokeAsync(() => FilesDropped?.Invoke(paths, options)); // let the drag source finish first
    }

    static void Animate(IAnimatable target, DependencyProperty property, double from, double to, int ms)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        target.BeginAnimation(property, animation);
    }
}

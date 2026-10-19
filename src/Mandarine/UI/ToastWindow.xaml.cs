using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mandarine.Native;

namespace Mandarine.UI;

/// <summary>Floating status toast in the bottom-right corner: progress while working, result when done.</summary>
public partial class ToastWindow : Window
{
    readonly DispatcherTimer _autoHide = new();
    Win32.POINT _anchor;
    bool _shown;
    bool _hover;

    public event Action? CancelRequested;
    public event Action? RevealRequested;

    public ToastWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            WindowEffects.MakeToolWindow(this, noActivate: true);
            bool acrylic = WindowEffects.TryApplyAcrylic(this);
            Root.SetResourceReference(Border.BackgroundProperty, acrylic ? "Brush.SurfaceAcrylic" : "Brush.Surface");
        };

        CloseButton.Click += (_, _) => HideNow();
        CancelButton.Click += (_, _) => { CancelButton.IsEnabled = false; CancelRequested?.Invoke(); };
        RevealButton.Click += (_, _) => RevealRequested?.Invoke();
        _autoHide.Tick += (_, _) => { _autoHide.Stop(); if (!_hover) HideNow(); };
        MouseEnter += (_, _) => _hover = true;
        MouseLeave += (_, _) =>
        {
            _hover = false;
            if (!_autoHide.IsEnabled && CancelButton.Visibility != Visibility.Visible && _shown)
            {
                _autoHide.Interval = TimeSpan.FromSeconds(2.5);
                _autoHide.Start();
            }
        };
        SizeChanged += (_, _) => { if (_shown) WindowEffects.Place(this, _anchor, Anchor.BottomRightOfWorkArea); };
    }

    public void ShowWorking(string title)
    {
        _autoHide.Stop();
        SetBubble("Brush.AccentGradient", "", spin: true); // Sync
        TitleText.Text = title;
        DetailText.Text = "Wird vorbereitet …";
        WarningText.Visibility = Visibility.Collapsed;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        RevealButton.Visibility = Visibility.Collapsed;
        Present();
    }

    public void UpdateProgress(string message, double fraction)
    {
        DetailText.Text = message;
        if (fraction < 0)
        {
            Progress.IsIndeterminate = true;
        }
        else
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(fraction, 0, 1);
        }
    }

    public void ShowQueued(int waiting)
    {
        WarningText.Text = waiting == 1 ? "Danach: 1 weiterer Auftrag" : $"Danach: {waiting} weitere Aufträge";
        WarningText.Visibility = Visibility.Visible;
    }

    public void ShowDone(string title, string detail, IReadOnlyList<string> warnings, bool offerReveal)
    {
        SetBubble("Brush.Success", "", spin: false); // CheckMark
        TitleText.Text = title;
        DetailText.Text = detail;
        ShowWarnings(warnings);
        Finish(offerReveal, warnings.Count > 0 ? 9 : 5);
    }

    public void ShowError(string title, string message)
    {
        SetBubble("Brush.Error", "", spin: false); // Error
        TitleText.Text = title;
        DetailText.Text = message;
        ShowWarnings(Array.Empty<string>());
        Finish(false, 10);
    }

    public void ShowInfo(string title, string detail, double seconds = 4)
    {
        SetBubble("Brush.AccentGradient", "", spin: false); // Info
        TitleText.Text = title;
        DetailText.Text = detail;
        ShowWarnings(Array.Empty<string>());
        Finish(false, seconds);
    }

    void ShowWarnings(IReadOnlyList<string> warnings)
    {
        var shown = warnings.Take(3).ToList();
        if (warnings.Count > 3) shown.Add($"… und {warnings.Count - 3} weitere Hinweise (siehe Log)");
        WarningText.Text = string.Join("\n", shown.Select(w => "• " + w));
        WarningText.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Finish(bool offerReveal, double seconds)
    {
        Progress.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        RevealButton.Visibility = offerReveal ? Visibility.Visible : Visibility.Collapsed;
        Present();
        _autoHide.Stop();
        _autoHide.Interval = TimeSpan.FromSeconds(seconds);
        _autoHide.Start();
    }

    void SetBubble(string brushKey, string glyph, bool spin)
    {
        Bubble.SetResourceReference(Border.BackgroundProperty, brushKey);
        Glyph.Text = glyph;
        if (spin)
        {
            GlyphRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            GlyphRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            GlyphRotation.Angle = 0;
        }
    }

    void Present()
    {
        if (!_shown)
        {
            _anchor = WindowEffects.CursorPosition();
            new WindowInteropHelper(this).EnsureHandle();
            Body.Opacity = 0;
            Show();
            _shown = true;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            BodyShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
        UpdateLayout();
        WindowEffects.Place(this, _anchor, Anchor.BottomRightOfWorkArea);
    }

    public void HideNow()
    {
        _autoHide.Stop();
        if (!_shown) return;
        _shown = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            if (_shown) return;
            Hide();
            GlyphRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        };
        Body.BeginAnimation(OpacityProperty, fade);
    }
}

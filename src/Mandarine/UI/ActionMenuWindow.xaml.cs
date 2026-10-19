using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mandarine.Core;
using Mandarine.Native;

namespace Mandarine.UI;

/// <summary>Floating context menu next to the cursor (Shift+Alt drop or "Send to → Optionen").</summary>
public partial class ActionMenuWindow : Window
{
    sealed record Entry(ProcessAction Action, string Glyph, string Label, string? Hint);

    static readonly Entry[] Entries =
    {
        new(ProcessAction.EmailCompress, "", "Für E-Mail komprimieren", null),
        new(ProcessAction.ConvertJpeg, "", "In JPEG umwandeln", "PDF: jede Seite als Bild"),
        new(ProcessAction.ConvertPng, "", "In PNG umwandeln", null),
        new(ProcessAction.ImagesToPdf, "", "Bilder zu einer PDF", null),
        new(ProcessAction.StripMetadata, "", "Metadaten entfernen", "GPS, Kamera, Autor"),
        new(ProcessAction.RedactSensitive, "", "Sensible Daten schwärzen", "E-Mail, IBAN, Telefon, Karten"),
        new(ProcessAction.RedactAllText, "", "Gesamten Text schwärzen", null),
        new(ProcessAction.ZipOnly, "", "Als ZIP packen", null)
    };

    readonly List<(ProcessAction Action, Button Button)> _buttons = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    bool _mouseWasDown;
    bool _done;

    public event Action<ProcessAction>? ActionChosen;

    public ActionMenuWindow(IReadOnlyList<string> paths)
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            WindowEffects.MakeToolWindow(this, noActivate: false);
            bool acrylic = WindowEffects.TryApplyAcrylic(this);
            Root.SetResourceReference(Border.BackgroundProperty, acrylic ? "Brush.SurfaceAcrylic" : "Brush.Surface");
        };

        HeaderTitle.Text = paths.Count == 1 ? Path.GetFileName(paths[0].TrimEnd('\\')) : Format.Files(paths.Count);
        HeaderDetail.Text = Format.Size(QuickSize(paths));
        if (paths.Count == 1 && Directory.Exists(paths[0])) HeaderDetail.Text += " · Ordner";

        BuildItems(ActionRunner.AvailableActions(paths));

        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => Dismiss();
        _poll.Tick += (_, _) => PollOutsideClick();
    }

    public void ShowAtCursor()
    {
        var cursor = WindowEffects.CursorPosition();
        Body.Opacity = 0;
        Show();
        UpdateLayout();
        WindowEffects.Place(this, cursor, Anchor.BelowRightOfPoint, 8);

        // Activation can be refused after a drag from another app; mouse use works either way,
        // and Esc / clicks outside are watched by polling below.
        Activate();
        Win32.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        if (_buttons.Count > 0) _buttons[0].Button.Focus();

        _mouseWasDown = Win32.IsKeyDown(Win32.VK_LBUTTON) || Win32.IsKeyDown(Win32.VK_RBUTTON);
        _poll.Start();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
        BodyShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
    }

    void BuildItems(HashSet<ProcessAction> available)
    {
        int number = 1;
        foreach (var entry in Entries.Where(e => available.Contains(e.Action)))
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock
            {
                Text = entry.Glyph,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
            icon.SetResourceReference(TextBlock.ForegroundProperty, entry.Action == ProcessAction.EmailCompress ? "Brush.Accent" : "Brush.Text");
            grid.Children.Add(icon);

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock
            {
                Text = entry.Action == ProcessAction.EmailCompress
                    ? $"{entry.Label} (≤ {Format.Megabytes(SettingsStore.Current.TargetMegabytes)})"
                    : entry.Label,
                FontSize = 13,
                FontWeight = entry.Action == ProcessAction.EmailCompress ? FontWeights.SemiBold : FontWeights.Normal
            };
            labels.Children.Add(label);
            if (entry.Hint != null)
            {
                var hint = new TextBlock { Text = entry.Hint, FontSize = 11, Margin = new Thickness(0, 1, 0, 0) };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
                labels.Children.Add(hint);
            }
            Grid.SetColumn(labels, 1);
            grid.Children.Add(labels);

            var key = new TextBlock { Text = number.ToString(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0) };
            key.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
            Grid.SetColumn(key, 2);
            grid.Children.Add(key);

            var button = new Button { Content = grid };
            button.SetResourceReference(StyleProperty, "MenuButton");
            var action = entry.Action;
            button.Click += (_, _) => Choose(action);
            Items.Children.Add(button);
            _buttons.Add((action, button));
            number++;
        }
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
        if (index >= 0 && index < _buttons.Count)
        {
            Choose(_buttons[index].Action);
            e.Handled = true;
        }
    }

    void PollOutsideClick()
    {
        if (Win32.IsKeyDown(Win32.VK_ESCAPE)) { Dismiss(); return; }

        bool down = Win32.IsKeyDown(Win32.VK_LBUTTON) || Win32.IsKeyDown(Win32.VK_RBUTTON);
        if (down && !_mouseWasDown)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (Win32.GetWindowRect(hwnd, out var rect) && Win32.GetCursorPos(out var pt) &&
                (pt.X < rect.Left || pt.X >= rect.Right || pt.Y < rect.Top || pt.Y >= rect.Bottom))
            {
                Dismiss();
                return;
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

    static long QuickSize(IReadOnlyList<string> paths)
    {
        long total = 0;
        int counted = 0;
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p)) total += new FileInfo(p).Length;
                else if (Directory.Exists(p))
                {
                    foreach (var f in new DirectoryInfo(p).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    {
                        total += f.Length;
                        if (++counted > 20000) break; // keep the menu instant for huge folders
                    }
                }
            }
            catch
            {
                // ignore unreadable entries
            }
        }
        return total;
    }
}

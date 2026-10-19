using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading.Tasks;
using Zytrus.Core;
using Zytrus.Native;

namespace Zytrus.UI;

/// <summary>
/// Settings with a live preview of the citrus slice (including its liquid glass). Every change applies immediately;
/// slider values are written to disk when the window closes.
/// </summary>
public partial class SettingsWindow : Window
{
    readonly CitrusView _preview = new();
    readonly Grid _previewStage = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Image _previewBackground = new() { Stretch = Stretch.Fill };
    readonly DispatcherTimer _glassDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    int _glassGeneration;
    bool _loading;
    bool _ownChange;
    bool _rebuildPending;

    public SettingsWindow()
    {
#pragma warning disable WPF0001 // Fluent theme (Windows 11 look for the standard controls)
        try { ThemeMode = ThemeMode.System; }
        catch (Exception ex) { Log.Error("Fluent theme", ex); }
#pragma warning restore WPF0001

        InitializeComponent();
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }

        // Preview: the fruit over a sample "desktop", so the glass has something to refract.
        _previewStage.Children.Add(_previewBackground);
        _previewStage.Children.Add(_preview);
        PreviewHost.Child = _previewStage;
        _glassDebounce.Tick += (_, _) => { _glassDebounce.Stop(); RenderPreviewGlass(); };
        _preview.MouseMove += (_, e) => _preview.Hover = _preview.HitTest(e.GetPosition(_preview));
        _preview.MouseLeave += (_, _) => _preview.Hover = CitrusView.None;

        BuildColorChoices();
        foreach (var info in ActionCatalog.All)
            CenterBox.Items.Add(new ComboBoxItem { Content = info.Title, Tag = info.Action });
        LoadValues();
        Wire();

        SettingsStore.Changed += OnStoreChanged;
        Closed += (_, _) =>
        {
            SettingsStore.Changed -= OnStoreChanged;
            SettingsStore.Save();
        };
    }

    // ---- load / refresh ------------------------------------------------------------------------

    void LoadValues()
    {
        _loading = true;
        try
        {
            var s = SettingsStore.Current;
            foreach (var radio in ColorPanel.Children.OfType<RadioButton>())
                radio.IsChecked = radio.Tag is CitrusVariety c && c == s.Variety;
            SizeSlider.Value = s.CitrusScale;
            BlurSlider.Value = s.GlassBlur;
            RefractionSlider.Value = s.GlassRefraction;
            TintSlider.Value = s.GlassTint;
            PlacementBox.SelectedIndex = (int)s.Placement;
            LabelsCheck.IsChecked = s.ShowLabels;

            HookCheck.IsChecked = s.DragHookEnabled;
            TriggerBox.SelectedIndex = (int)s.Trigger;
            CenterBox.SelectedItem = CenterBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is ProcessAction a && a == s.CenterAction);
            AllAppsCheck.IsChecked = s.DragFromAllApps;

            TargetSlider.Value = s.TargetMegabytes;
            SmallSlider.Value = s.SmallTargetMegabytes;
            MetadataCheck.IsChecked = s.StripMetadata;
            HevcCheck.IsChecked = s.PreferHevc;

            OutputBox.SelectedIndex = (int)s.Output;
            SuffixText.Text = s.Suffix;
            ZipSlider.Value = s.ZipWhenMoreThan + 1;
            ClipboardCheck.IsChecked = s.CopyToClipboard;
            RevealCheck.IsChecked = s.RevealInExplorer;
            SoundCheck.IsChecked = s.PlaySound;

            AutostartCheck.IsChecked = Safe(() => ShellIntegration.AutostartEnabled);
            SendToCheck.IsChecked = Safe(() => ShellIntegration.SendToInstalled);
        }
        finally
        {
            _loading = false;
        }
        RefreshDerived();
    }

    void OnStoreChanged()
    {
        // Changes made here only need the derived parts; changes from elsewhere (tray, reset) reload everything.
        if (_ownChange) RefreshDerived();
        else LoadValues();
    }

    void RefreshDerived()
    {
        var s = SettingsStore.Current;
        SizeValue.Text = $"{s.CitrusScale * 100:0} %";
        BlurValue.Text = s.GlassBlur.ToString("0.#");
        RefractionValue.Text = $"{s.GlassRefraction * 100:0} %";
        TintValue.Text = $"{s.GlassTint * 100:0} %";
        TargetValue.Text = Format.Megabytes(s.TargetMegabytes);
        SmallValue.Text = Format.Megabytes(s.SmallTargetMegabytes);
        ZipValue.Text = $"{s.ZipWhenMoreThan + 1} Dateien";
        FolderText.Text = s.OutputFolder;

        double size = Math.Round(310 * s.CitrusScale / 1.4); // shows the chosen size relative to the largest
        _previewStage.Width = size;
        _previewStage.Height = size;
        _previewBackground.Clip = new RectangleGeometry(new Rect(0, 0, size, size), 14, 14);
        _preview.Configure(
            s.Segments.Select(a =>
            {
                var info = ActionCatalog.Get(a);
                return new CitrusView.Segment(a, info.Glyph, info.ShortLabel, info.Title, ActionCatalog.Describe(a, s), true);
            }),
            s.Variety, s.ShowLabels, "Vorschau", "Maus über ein Stück bewegen");

        ScheduleSegmentListRebuild();
        _glassDebounce.Stop();
        _glassDebounce.Start();
    }

    /// <summary>Renders the sample backdrop and the glass over it (on a worker thread).</summary>
    void RenderPreviewGlass()
    {
        try
        {
            int size = (int)Math.Round(_previewStage.Width);
            if (size < 40) return;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var sample = LiquidGlass.CreateSampleBackdrop(size, dpi);
            _previewBackground.Source = sample;

            var backdrop = LiquidGlass.FromBitmap(sample);
            var shape = _preview.ShapeFor(backdrop.Width, backdrop.Height);
            var parameters = CitrusView.GlassParameters(SettingsStore.Current);
            int generation = ++_glassGeneration;

            Task.Run(() => LiquidGlass.Render(backdrop, shape, parameters)).ContinueWith(task =>
            {
                if (task.Exception != null)
                {
                    Log.Error("Preview glass", task.Exception);
                    return;
                }
                var result = task.Result;
                Dispatcher.InvokeAsync(() => { if (generation == _glassGeneration) _preview.SetGlass(result); });
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Log.Error("Preview glass", ex);
        }
    }

    /// <summary>Rebuilt after the current click has finished, so we never remove the control being clicked.</summary>
    void ScheduleSegmentListRebuild()
    {
        if (_rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _rebuildPending = false;
            RebuildSegmentList();
        });
    }

    // ---- segment list --------------------------------------------------------------------------

    void RebuildSegmentList()
    {
        SegmentList.Children.Clear();
        var s = SettingsStore.Current;
        var active = s.Segments;
        var order = active.Concat(ActionCatalog.DefaultOrder().Where(a => !active.Contains(a))).ToList();

        foreach (var action in order)
        {
            var info = ActionCatalog.Get(action);
            int index = active.IndexOf(action);
            bool on = index >= 0;

            var icon = new TextBlock { Text = info.Glyph, FontSize = 15, Width = 26, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = info.Title });
            texts.Children.Add(new TextBlock { Text = ActionCatalog.Describe(action, s), FontSize = 11.5, Opacity = 0.65 });

            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(icon);
            label.Children.Add(texts);

            var check = new CheckBox { Content = label, IsChecked = on, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            check.Click += (_, _) => ToggleSegment(action, check.IsChecked == true);

            var up = ArrowButton("", "Nach vorne (gegen den Uhrzeigersinn)", on && index > 0, () => MoveSegment(action, -1));
            var down = ArrowButton("", "Nach hinten (im Uhrzeigersinn)", on && index < active.Count - 1, () => MoveSegment(action, 1));

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Opacity = on ? 1 : 0.75 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(up, 1);
            Grid.SetColumn(down, 2);
            row.Children.Add(check);
            row.Children.Add(up);
            row.Children.Add(down);
            SegmentList.Children.Add(row);
        }
    }

    Button ArrowButton(string glyph, string tooltip, bool enabled, Action onClick)
    {
        var text = new TextBlock { Text = glyph, FontSize = 11 };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
        var button = new Button
        {
            Content = text,
            ToolTip = tooltip,
            IsEnabled = enabled,
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    void ToggleSegment(ProcessAction action, bool on)
    {
        var list = SettingsStore.Current.Segments.ToList();
        if (on)
        {
            if (!list.Contains(action)) list.Add(action);
        }
        else
        {
            if (list.Count <= 2)
            {
                MessageBox.Show(this, "Mindestens zwei Stücke müssen bleiben.", "Zytrus", MessageBoxButton.OK, MessageBoxImage.Information);
                ScheduleSegmentListRebuild();
                return;
            }
            list.Remove(action);
        }
        Change(s => s.Segments = list);
    }

    void MoveSegment(ProcessAction action, int delta)
    {
        var list = SettingsStore.Current.Segments.ToList();
        int i = list.IndexOf(action), j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        Change(s => s.Segments = list);
    }

    // ---- other controls ------------------------------------------------------------------------

    void BuildColorChoices()
    {
        foreach (var color in Enum.GetValues<CitrusVariety>())
        {
            var palette = CitrusPalette.For(color);
            var swatch = new System.Windows.Shapes.Ellipse
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 6, 0),
                Fill = new RadialGradientBrush(palette.RimLight, palette.Rim) { GradientOrigin = new Point(0.35, 0.3) },
                Stroke = new SolidColorBrush(palette.RimEdge),
                StrokeThickness = 1
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(swatch);
            content.Children.Add(new TextBlock { Text = color.ToString(), VerticalAlignment = VerticalAlignment.Center });

            var radio = new RadioButton { Content = content, Tag = color, GroupName = "CitrusVariety", Margin = new Thickness(0, 0, 18, 4) };
            var chosen = color;
            radio.Checked += (_, _) => Change(s => s.Variety = chosen);
            ColorPanel.Children.Add(radio);
        }
    }

    void Wire()
    {
        SizeSlider.ValueChanged += (_, e) => Change(s => s.CitrusScale = Math.Round(e.NewValue, 2), save: false);
        BlurSlider.ValueChanged += (_, e) => Change(s => s.GlassBlur = e.NewValue, save: false);
        RefractionSlider.ValueChanged += (_, e) => Change(s => s.GlassRefraction = Math.Round(e.NewValue, 2), save: false);
        TintSlider.ValueChanged += (_, e) => Change(s => s.GlassTint = Math.Round(e.NewValue, 2), save: false);
        PlacementBox.SelectionChanged += (_, _) => Change(s => s.Placement = (CitrusPlacement)Math.Max(0, PlacementBox.SelectedIndex));
        LabelsCheck.Click += (_, _) => Change(s => s.ShowLabels = LabelsCheck.IsChecked == true);

        HookCheck.Click += (_, _) => Change(s => s.DragHookEnabled = HookCheck.IsChecked == true);
        TriggerBox.SelectionChanged += (_, _) => Change(s => s.Trigger = (DragTrigger)Math.Max(0, TriggerBox.SelectedIndex));
        AllAppsCheck.Click += (_, _) => Change(s => s.DragFromAllApps = AllAppsCheck.IsChecked == true);
        CenterBox.SelectionChanged += (_, _) =>
        {
            if (CenterBox.SelectedItem is ComboBoxItem { Tag: ProcessAction action }) Change(s => s.CenterAction = action);
        };

        TargetSlider.ValueChanged += (_, e) => Change(s => s.TargetMegabytes = e.NewValue, save: false);
        SmallSlider.ValueChanged += (_, e) => Change(s => s.SmallTargetMegabytes = e.NewValue, save: false);
        MetadataCheck.Click += (_, _) => Change(s => s.StripMetadata = MetadataCheck.IsChecked == true);
        HevcCheck.Click += (_, _) => Change(s => s.PreferHevc = HevcCheck.IsChecked == true);

        OutputBox.SelectionChanged += (_, _) => Change(s => s.Output = (OutputLocation)Math.Max(0, OutputBox.SelectedIndex));
        FolderButton.Click += (_, _) => PickFolder();
        SuffixText.TextChanged += (_, _) => Change(s => s.Suffix = CleanSuffix(SuffixText.Text), save: false);
        ZipSlider.ValueChanged += (_, e) => Change(s => s.ZipWhenMoreThan = Math.Max(1, (int)Math.Round(e.NewValue) - 1), save: false);
        ClipboardCheck.Click += (_, _) => Change(s => s.CopyToClipboard = ClipboardCheck.IsChecked == true);
        RevealCheck.Click += (_, _) => Change(s => s.RevealInExplorer = RevealCheck.IsChecked == true);
        SoundCheck.Click += (_, _) => Change(s => s.PlaySound = SoundCheck.IsChecked == true);

        AutostartCheck.Click += (_, _) =>
        {
            try { ShellIntegration.SetAutostart(AutostartCheck.IsChecked == true); }
            catch (Exception ex)
            {
                Log.Error("Autostart", ex);
                AutostartCheck.IsChecked = Safe(() => ShellIntegration.AutostartEnabled);
            }
        };
        SendToCheck.Click += (_, _) =>
        {
            try
            {
                if (SendToCheck.IsChecked == true) ShellIntegration.InstallSendTo();
                else ShellIntegration.RemoveSendTo();
            }
            catch (Exception ex)
            {
                Log.Error("Send to", ex);
                SendToCheck.IsChecked = Safe(() => ShellIntegration.SendToInstalled);
            }
        };

        ResetButton.Click += (_, _) =>
        {
            var answer = MessageBox.Show(this, "Alle Einstellungen auf Standard zurücksetzen?", "Zytrus",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) SettingsStore.ResetToDefaults();
        };
        DoneButton.Click += (_, _) => Close();
    }

    void PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Ordner für die Ergebnisse wählen" };
        if (Directory.Exists(SettingsStore.Current.OutputFolder)) dialog.InitialDirectory = SettingsStore.Current.OutputFolder;
        if (dialog.ShowDialog(this) == true)
        {
            var folder = dialog.FolderName;
            Change(s => s.OutputFolder = folder);
        }
    }

    void Change(Action<AppSettings> mutate, bool save = true)
    {
        if (_loading) return;
        _ownChange = true;
        try { SettingsStore.Update(mutate, save); }
        finally { _ownChange = false; }
    }

    static string CleanSuffix(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(text.Where(ch => !invalid.Contains(ch)).ToArray());
    }

    static bool Safe(Func<bool> read)
    {
        try { return read(); } catch { return false; }
    }
}

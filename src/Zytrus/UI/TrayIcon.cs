using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;

namespace Zytrus.UI;

/// <summary>Notification-area icon (the Windows equivalent of the NSStatusItem) with quick settings.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ContextMenuStrip _menu;

    public event Action? SettingsRequested;
    public event Action? CompressFilesRequested;
    public event Action? PickForCitrusRequested;
    public event Action? ExitRequested;

    static readonly (double Mb, string Label)[] Limits =
    {
        (17, "Gmail, Outlook.com, iCloud (≤ 17 MB)"),
        (6, "Firmen-Mail / Exchange (≤ 6 MB)"),
        (3, "Sehr klein (≤ 3 MB)")
    };

    public TrayIcon()
    {
        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Renderer = new Forms.ToolStripProfessionalRenderer(new FlatColors()) { RoundedEdges = false },
            Font = new Font("Segoe UI", 9f)
        };
        _menu.Opening += (_, _) => Build();
        Build();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Zytrus – Dateien mit ⇧ ziehen",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) SettingsRequested?.Invoke();
        };
    }

    void Build()
    {
        var s = SettingsStore.Current;
        _menu.Items.Clear();

        var settings = Item("Einstellungen …", () => SettingsRequested?.Invoke());
        settings.Font = new Font(_menu.Font, FontStyle.Bold);
        _menu.Items.Add(settings);
        _menu.Items.Add(Item("Dateien auswählen → Zitrone …", () => PickForCitrusRequested?.Invoke()));
        _menu.Items.Add(Item("Dateien für E-Mail komprimieren …", () => CompressFilesRequested?.Invoke()));
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var limit = new Forms.ToolStripMenuItem($"E-Mail-Limit: {Core.Format.Megabytes(s.TargetMegabytes)}");
        foreach (var (mb, label) in Limits)
        {
            var entry = Item(label, () => SettingsStore.Update(x => x.TargetMegabytes = mb));
            entry.Checked = Math.Abs(s.TargetMegabytes - mb) < 0.01;
            limit.DropDownItems.Add(entry);
        }
        _menu.Items.Add(limit);

        string key = s.Trigger switch
        {
            DragTrigger.Ctrl => "Strg",
            DragTrigger.ShiftCtrl => "⇧+Strg",
            _ => "⇧"
        };
        _menu.Items.Add(Toggle($"Zitrone beim Ziehen mit {key}", s.DragHookEnabled,
            v => SettingsStore.Update(x => x.DragHookEnabled = v)));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(Item("Protokoll öffnen", () => { if (File.Exists(Log.FilePath)) Open("notepad.exe", Log.FilePath); }));
        _menu.Items.Add(Item("Beenden", () => ExitRequested?.Invoke()));
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    static Forms.ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => Safe(onClick);
        return item;
    }

    static Forms.ToolStripMenuItem Toggle(string text, bool isChecked, Action<bool> onChange)
    {
        var item = new Forms.ToolStripMenuItem(text) { Checked = isChecked };
        item.Click += (_, _) => Safe(() => onChange(!isChecked));
        return item;
    }

    static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("Tray action", ex);
            Forms.MessageBox.Show(ex.Message, "Zytrus", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
        }
    }

    static void Open(string exe, string argument) =>
        Process.Start(new ProcessStartInfo(exe, $"\"{argument}\"") { UseShellExecute = true });

    static Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (info != null)
            {
                using var stream = info.Stream;
                return new Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Tray icon", ex);
        }
        return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    /// <summary>Flat, Windows 11-like colors for the WinForms menu.</summary>
    sealed class FlatColors : Forms.ProfessionalColorTable
    {
        static readonly Color Background = Color.FromArgb(249, 249, 250);
        static readonly Color Hover = Color.FromArgb(234, 234, 238);
        static readonly Color Check = Color.FromArgb(255, 225, 190);

        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuBorder => Color.FromArgb(218, 218, 222);
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color SeparatorDark => Color.FromArgb(226, 226, 230);
        public override Color SeparatorLight => Background;
        public override Color CheckBackground => Check;
        public override Color CheckSelectedBackground => Check;
        public override Color CheckPressedBackground => Check;
    }
}

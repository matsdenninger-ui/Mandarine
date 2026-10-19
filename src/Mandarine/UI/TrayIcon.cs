using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Mandarine.Native;
using Forms = System.Windows.Forms;

namespace Mandarine.UI;

/// <summary>Notification-area icon (the Windows equivalent of the NSStatusItem) with quick settings.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Forms.ContextMenuStrip _menu;

    public event Action? CompressFilesRequested;
    public event Action? OptionsForFilesRequested;
    public event Action? DragHookChanged;
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
            Text = "Mandarine – Dateien mit ⇧ ziehen",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) CompressFilesRequested?.Invoke();
        };
    }

    void Build()
    {
        var s = SettingsStore.Current;
        _menu.Items.Clear();

        var compress = Item("Dateien für E-Mail komprimieren …", () => CompressFilesRequested?.Invoke());
        compress.Font = new Font(_menu.Font, FontStyle.Bold);
        _menu.Items.Add(compress);
        _menu.Items.Add(Item("Dateien mit Optionen …", () => OptionsForFilesRequested?.Invoke()));
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var limit = new Forms.ToolStripMenuItem($"E-Mail-Limit: {Core.Format.Megabytes(s.TargetMegabytes)}");
        foreach (var (mb, label) in Limits)
        {
            var entry = Item(label, () => SettingsStore.Update(x => x.TargetMegabytes = mb));
            entry.Checked = Math.Abs(s.TargetMegabytes - mb) < 0.01;
            limit.DropDownItems.Add(entry);
        }
        _menu.Items.Add(limit);

        _menu.Items.Add(Toggle("Metadaten (GPS, Kamera) entfernen", s.StripMetadata, v => SettingsStore.Update(x => x.StripMetadata = v)));
        _menu.Items.Add(Toggle("Ergebnis in Zwischenablage", s.CopyToClipboard, v => SettingsStore.Update(x => x.CopyToClipboard = v)));
        _menu.Items.Add(Toggle("Ergebnis im Explorer markieren", s.RevealInExplorer, v => SettingsStore.Update(x => x.RevealInExplorer = v)));
        _menu.Items.Add(Toggle("Ton bei Fertigstellung", s.PlaySound, v => SettingsStore.Update(x => x.PlaySound = v)));
        _menu.Items.Add(Toggle("Ablagefläche bei ⇧-Ziehen", s.DragHookEnabled, v =>
        {
            SettingsStore.Update(x => x.DragHookEnabled = v);
            DragHookChanged?.Invoke();
        }));
        _menu.Items.Add(new Forms.ToolStripSeparator());

        _menu.Items.Add(Toggle("Im Kontextmenü „Senden an“", ShellIntegration.SendToInstalled, v =>
        {
            if (v) ShellIntegration.InstallSendTo(); else ShellIntegration.RemoveSendTo();
        }));
        _menu.Items.Add(Toggle("Mit Windows starten", ShellIntegration.AutostartEnabled, ShellIntegration.SetAutostart));
        _menu.Items.Add(Item("Einstellungen bearbeiten …", () => Open("notepad.exe", SettingsStore.FilePath)));
        _menu.Items.Add(Item("Einstellungen neu laden", () => { SettingsStore.Reload(); DragHookChanged?.Invoke(); }));
        _menu.Items.Add(Item("Protokoll öffnen", () => { if (File.Exists(Log.FilePath)) Open("notepad.exe", Log.FilePath); }));
        _menu.Items.Add(new Forms.ToolStripSeparator());
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
            Forms.MessageBox.Show(ex.Message, "Mandarine", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
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

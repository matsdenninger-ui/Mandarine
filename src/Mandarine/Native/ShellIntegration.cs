using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace Mandarine.Native;

/// <summary>"Send to" shortcuts, autostart, Explorer selection and the file clipboard.</summary>
internal static class ShellIntegration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "Mandarine";

    static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path");

    static string SendToFolder => Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
    static string CompressLink => Path.Combine(SendToFolder, "Mandarine – Für E-Mail komprimieren.lnk");
    static string MenuLink => Path.Combine(SendToFolder, "Mandarine – Optionen.lnk");

    // ---- "Send to" ---------------------------------------------------------------------------

    public static bool SendToInstalled => File.Exists(CompressLink);

    public static void InstallSendTo()
    {
        CreateShortcut(CompressLink, "--email", "Dateien für den E-Mail-Versand verkleinern");
        CreateShortcut(MenuLink, "--menu", "Mandarine-Optionen für die ausgewählten Dateien");
    }

    public static void RemoveSendTo()
    {
        TryDelete(CompressLink);
        TryDelete(MenuLink);
    }

    /// <summary>Keeps shortcuts and autostart pointing at the current exe if the user moved it.</summary>
    public static void RefreshRegistrations()
    {
        try
        {
            if (SendToInstalled) InstallSendTo();
            if (AutostartEnabled) SetAutostart(true);
        }
        catch (Exception ex)
        {
            Log.Error("Refresh registrations", ex);
        }
    }

    static void CreateShortcut(string linkPath, string arguments, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(linkPath);
            shortcut.TargetPath = ExePath;
            shortcut.Arguments = arguments;
            shortcut.Description = description;
            shortcut.IconLocation = ExePath + ",0";
            shortcut.WorkingDirectory = Path.GetDirectoryName(ExePath);
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Log.Error("Delete " + path, ex); }
    }

    // ---- Autostart ---------------------------------------------------------------------------

    public static bool AutostartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) != null;
        }
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(AppName, $"\"{ExePath}\" --background");
        else key.DeleteValue(AppName, false);
    }

    // ---- Explorer ----------------------------------------------------------------------------

    /// <summary>Opens Explorer with the given files selected (one window per folder).</summary>
    public static void RevealInExplorer(IEnumerable<string> files)
    {
        foreach (var group in files.Where(File.Exists).GroupBy(f => Path.GetDirectoryName(f)!, StringComparer.OrdinalIgnoreCase))
        {
            IntPtr folder = Win32.ILCreateFromPathW(group.Key);
            var items = group.Select(Win32.ILCreateFromPathW).Where(p => p != IntPtr.Zero).ToArray();
            try
            {
                if (folder != IntPtr.Zero && items.Length > 0)
                {
                    int hr = Win32.SHOpenFolderAndSelectItems(folder, (uint)items.Length, items, 0);
                    if (hr != 0) Log.Write($"SHOpenFolderAndSelectItems failed: 0x{hr:X8}");
                }
            }
            finally
            {
                foreach (var pidl in items) Win32.ILFree(pidl);
                if (folder != IntPtr.Zero) Win32.ILFree(folder);
            }
        }
    }

    // ---- Clipboard ---------------------------------------------------------------------------

    /// <summary>
    /// Puts the files on the clipboard as CF_HDROP with "Preferred DropEffect = Copy",
    /// so Ctrl+V attaches them in Outlook and copies them in Explorer. Must run on the STA UI thread.
    /// </summary>
    public static bool CopyFilesToClipboard(IReadOnlyList<string> files)
    {
        var list = new StringCollection();
        foreach (var f in files) list.Add(f);

        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // DROPEFFECT_COPY

        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(60); // another app holds the clipboard
            }
        }
        Log.Write("Clipboard was busy, files not copied.");
        return false;
    }
}

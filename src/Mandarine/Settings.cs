using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mandarine;

public enum OutputLocation { NextToOriginal, Folder }

public enum DropZonePlacement { NearCursor, TopCenter }

/// <summary>User settings, stored as JSON in %APPDATA%\Mandarine\settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>
    /// Target size for all attachments together, in MB as Explorer shows them (1 MB = 1024 × 1024 bytes).
    /// Mail is Base64-encoded on the wire (+~37 %), so 17 MB stays below a 25 MB provider limit.
    /// </summary>
    public double TargetMegabytes { get; set; } = 17;

    public bool StripMetadata { get; set; } = true;
    public OutputLocation Output { get; set; } = OutputLocation.NextToOriginal;
    public string OutputFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Mandarine");
    public string Suffix { get; set; } = "_email";

    /// <summary>More files than this are bundled into one .zip. Folders are always zipped.</summary>
    public int ZipWhenMoreThan { get; set; } = 10;

    /// <summary>HEVC is smaller but many Windows PCs cannot play it without a paid codec. H.264 is the default.</summary>
    public bool PreferHevc { get; set; }

    public bool CopyToClipboard { get; set; } = true;
    public bool RevealInExplorer { get; set; } = true;
    public bool PlaySound { get; set; } = true;

    /// <summary>Shows the drop zone when a drag with Shift held is detected.</summary>
    public bool DragHookEnabled { get; set; } = true;
    public DropZonePlacement DropZone { get; set; } = DropZonePlacement.NearCursor;

    /// <summary>Process names (without .exe) a Shift-drag may start in. "*" allows every app.</summary>
    public List<string> DragSourceApps { get; set; } = new()
    {
        "explorer", "msedge", "chrome", "firefox", "brave", "opera", "vivaldi",
        "outlook", "olk", "thunderbird", "ms-teams", "totalcmd64", "dopus"
    };

    [JsonIgnore]
    public long TargetBytes => (long)(TargetMegabytes * 1024 * 1024);
}

public static class SettingsStore
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mandarine");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Current { get; private set; } = new();

    public static bool IsFirstRun { get; private set; }

    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
            else
            {
                IsFirstRun = true;
                Save();
            }
        }
        catch (Exception ex)
        {
            Log.Write("Settings could not be read, using defaults: " + ex.Message);
            Current = new AppSettings();
        }

        if (Current.TargetMegabytes < 0.5) Current.TargetMegabytes = 0.5;
        if (Current.ZipWhenMoreThan < 1) Current.ZipWhenMoreThan = 1;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception ex)
        {
            Log.Write("Settings could not be saved: " + ex.Message);
        }
    }

    public static void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Re-reads the file (e.g. after the user edited it by hand).</summary>
    public static void Reload()
    {
        Load();
        Changed?.Invoke();
    }
}

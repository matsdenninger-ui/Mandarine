using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Text.Json.Serialization;
using Zytrus.Core;

namespace Zytrus;

public enum OutputLocation { NextToOriginal, Folder }

/// <summary>Where the fruit appears while dragging.</summary>
public enum CitrusPlacement { AtCursor, ScreenCenter }

/// <summary>Key that must be held while dragging for the fruit to appear.</summary>
public enum DragTrigger { Shift, Ctrl, ShiftCtrl }

/// <summary>Fruit variants: color of rim, glass tint and accents.</summary>
public enum CitrusVariety { Zitrone, Limette, Orange, Grapefruit }

/// <summary>User settings, stored as JSON in %APPDATA%\Zytrus\settings.json.</summary>
public sealed class AppSettings
{
    // ---- Citrus (look & trigger) ----------------------------------------------------------------

    /// <summary>Functions shown as segments, in clockwise order starting at the top.</summary>
    public List<ProcessAction> Segments { get; set; } = ActionCatalog.DefaultOrder();

    public CitrusVariety Variety { get; set; } = CitrusVariety.Zitrone;

    /// <summary>0.7 (small) … 1.4 (large).</summary>
    public double CitrusScale { get; set; } = 1.0;

    public CitrusPlacement Placement { get; set; } = CitrusPlacement.AtCursor;

    public bool ShowLabels { get; set; } = true;

    // Liquid glass (parameters as in liquid-glass-js)
    /// <summary>Frosting of the glass segments, 0 (clear) … 12 (very frosted).</summary>
    public double GlassBlur { get; set; } = 5;

    /// <summary>Strength of the edge refraction, 0 … 2 (1 = like the original shader defaults).</summary>
    public double GlassRefraction { get; set; } = 1;

    /// <summary>How much the fruit color tints the glass, 0 … 0.8.</summary>
    public double GlassTint { get; set; } = 0.35;

    /// <summary>Shows the fruit when a drag with the trigger key is detected.</summary>
    public bool DragHookEnabled { get; set; } = true;

    public DragTrigger Trigger { get; set; } = DragTrigger.Shift;

    /// <summary>If false, only drags starting in the apps of DragSourceApps open the fruit.</summary>
    public bool DragFromAllApps { get; set; }

    /// <summary>Process names (without .exe) a drag may start in. "*" allows every app.</summary>
    public List<string> DragSourceApps { get; set; } = new()
    {
        "explorer", "msedge", "chrome", "firefox", "brave", "opera", "vivaldi",
        "outlook", "olk", "thunderbird", "ms-teams", "totalcmd64", "dopus"
    };

    // ---- Compression ----------------------------------------------------------------------------

    /// <summary>
    /// Target size for all attachments together, in MB as Explorer shows them (1 MB = 1024 × 1024 bytes).
    /// Mail is Base64-encoded on the wire (+~37 %), so 17 MB stays below a 25 MB provider limit.
    /// </summary>
    public double TargetMegabytes { get; set; } = 17;

    /// <summary>Target of the "Extra klein" segment.</summary>
    public double SmallTargetMegabytes { get; set; } = 6;

    public bool StripMetadata { get; set; } = true;

    /// <summary>HEVC is smaller but many Windows PCs cannot play it without a paid codec. H.264 is the default.</summary>
    public bool PreferHevc { get; set; }

    // ---- Output ---------------------------------------------------------------------------------

    public OutputLocation Output { get; set; } = OutputLocation.NextToOriginal;
    public string OutputFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Zytrus");
    public string Suffix { get; set; } = "_email";

    /// <summary>More files than this are bundled into one .zip. Folders are always zipped.</summary>
    public int ZipWhenMoreThan { get; set; } = 10;

    public bool CopyToClipboard { get; set; } = true;
    public bool RevealInExplorer { get; set; } = true;
    public bool PlaySound { get; set; } = true;

    [JsonIgnore]
    public long TargetBytes => (long)(TargetMegabytes * 1024 * 1024);

    /// <summary>Repairs values a hand-edited file might have broken.</summary>
    public void Normalize()
    {
        TargetMegabytes = Math.Clamp(TargetMegabytes, 0.5, 500);
        SmallTargetMegabytes = Math.Clamp(SmallTargetMegabytes, 0.5, 500);
        CitrusScale = Math.Clamp(CitrusScale, 0.7, 1.4);
        GlassBlur = Math.Clamp(GlassBlur, 0, 12);
        GlassRefraction = Math.Clamp(GlassRefraction, 0, 2);
        GlassTint = Math.Clamp(GlassTint, 0, 0.8);
        if (!Enum.IsDefined(Variety)) Variety = CitrusVariety.Zitrone;
        ZipWhenMoreThan = Math.Clamp(ZipWhenMoreThan, 1, 1000);
        Suffix ??= "";
        OutputFolder = string.IsNullOrWhiteSpace(OutputFolder) ? new AppSettings().OutputFolder : OutputFolder;
        DragSourceApps ??= new AppSettings().DragSourceApps;

        var segments = (Segments ?? new List<ProcessAction>())
            .Where(a => Enum.IsDefined(a)).Distinct().ToList();
        Segments = segments.Count >= 2 ? segments : ActionCatalog.DefaultOrder();
    }
}

public static class SettingsStore
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zytrus");

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

        Current.Normalize();
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

    /// <param name="save">False while a slider is being dragged; the window saves when it closes.</param>
    public static void Update(Action<AppSettings> mutate, bool save = true)
    {
        mutate(Current);
        if (save) Save();
        Changed?.Invoke();
    }

    /// <summary>Back to factory settings (keeps nothing).</summary>
    public static void ResetToDefaults()
    {
        Current = new AppSettings();
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

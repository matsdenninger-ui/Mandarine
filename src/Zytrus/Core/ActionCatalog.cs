using System.Collections.Generic;
using System.Linq;

namespace Zytrus.Core;

/// <summary>Icon, short segment label and description of every function – one place for fruit, menu and settings.</summary>
public sealed record ActionInfo(ProcessAction Action, string Glyph, string ShortLabel, string Title);

public static class ActionCatalog
{
    // Glyphs: Segoe Fluent Icons / Segoe MDL2 Assets
    public static IReadOnlyList<ActionInfo> All { get; } = new[]
    {
        new ActionInfo(ProcessAction.EmailCompress, "", "E-Mail", "Für E-Mail komprimieren"),
        new ActionInfo(ProcessAction.EmailCompressSmall, "", "Extra\nklein", "Extra klein komprimieren"),
        new ActionInfo(ProcessAction.ConvertJpeg, "", "JPEG", "In JPEG umwandeln"),
        new ActionInfo(ProcessAction.ConvertPng, "", "PNG", "In PNG umwandeln"),
        new ActionInfo(ProcessAction.ImagesToPdf, "", "Bilder\n→ PDF", "Bilder zu einer PDF"),
        new ActionInfo(ProcessAction.StripMetadata, "", "Meta-\ndaten", "Metadaten entfernen"),
        new ActionInfo(ProcessAction.RedactSensitive, "", "Sensibles\nschwärzen", "Sensible Daten schwärzen"),
        new ActionInfo(ProcessAction.RedactAllText, "", "Alles\nschwärzen", "Gesamten Text schwärzen"),
        new ActionInfo(ProcessAction.ZipOnly, "", "ZIP", "Als ZIP packen")
    };

    public static List<ProcessAction> DefaultOrder() => All.Select(a => a.Action).ToList();

    public static ActionInfo Get(ProcessAction action) => All.First(a => a.Action == action);

    /// <summary>One-line explanation, with the current limits filled in.</summary>
    public static string Describe(ProcessAction action, AppSettings s) => action switch
    {
        ProcessAction.EmailCompress => $"alles zusammen ≤ {Format.Megabytes(s.TargetMegabytes)}",
        ProcessAction.EmailCompressSmall => $"alles zusammen ≤ {Format.Megabytes(s.SmallTargetMegabytes)}",
        ProcessAction.ConvertJpeg => "Bilder und PDF-Seiten als JPEG",
        ProcessAction.ConvertPng => "verlustfrei, mit Transparenz",
        ProcessAction.ImagesToPdf => "alle Bilder in einer PDF",
        ProcessAction.StripMetadata => "GPS, Kamera, Autor entfernen",
        ProcessAction.RedactSensitive => "E-Mail, IBAN, Telefon, Karten",
        ProcessAction.RedactAllText => "jeder erkannte Text wird schwarz",
        ProcessAction.ZipOnly => "alles in ein ZIP packen",
        _ => ""
    };
}

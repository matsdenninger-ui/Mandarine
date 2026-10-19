using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Zytrus.Processing;

namespace Zytrus.Core;

/// <summary>Entry point for every action. Runs on a worker thread; owns the job's temp folder.</summary>
public static class ActionRunner
{
    public static async Task<JobResult> RunAsync(ProcessAction action, IReadOnlyList<string> paths, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "Zytrus", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(workDir);
        try
        {
            return action switch
            {
                ProcessAction.EmailCompress => await EmailPipeline.RunAsync(paths, workDir, SettingsStore.Current.TargetMegabytes, progress, ct),
                ProcessAction.EmailCompressSmall => await EmailPipeline.RunAsync(paths, workDir, SettingsStore.Current.SmallTargetMegabytes, progress, ct),
                ProcessAction.ConvertJpeg => await ConvertAsync(paths, workDir, toPng: false, progress, ct),
                ProcessAction.ConvertPng => await ConvertAsync(paths, workDir, toPng: true, progress, ct),
                ProcessAction.ImagesToPdf => ImagesToPdf(paths, workDir, progress, ct),
                ProcessAction.StripMetadata => StripMetadata(paths, workDir, progress, ct),
                ProcessAction.RedactSensitive => await RedactAsync(paths, workDir, RedactionMode.Sensitive, progress, ct),
                ProcessAction.RedactAllText => await RedactAsync(paths, workDir, RedactionMode.AllText, progress, ct),
                ProcessAction.ZipOnly => ZipOnly(paths, workDir, progress),
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch (Exception ex) { Log.Write("Temp cleanup failed: " + ex.Message); }
        }
    }

    /// <summary>Which menu entries make sense for the dropped files.</summary>
    public static HashSet<ProcessAction> AvailableActions(IReadOnlyList<string> paths)
    {
        var kinds = new HashSet<ItemKind>();
        bool folder = false;
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) { folder = true; continue; }
            kinds.Add(FileKinds.Classify(p));
        }

        var actions = new HashSet<ProcessAction> { ProcessAction.EmailCompress, ProcessAction.EmailCompressSmall, ProcessAction.ZipOnly };
        bool images = kinds.Contains(ItemKind.Image), pdfs = kinds.Contains(ItemKind.Pdf);
        if (images || pdfs) actions.Add(ProcessAction.ConvertJpeg);
        if (images) { actions.Add(ProcessAction.ConvertPng); actions.Add(ProcessAction.ImagesToPdf); }
        if (images || pdfs || kinds.Contains(ItemKind.Office) || folder) actions.Add(ProcessAction.StripMetadata);
        if (images || pdfs) { actions.Add(ProcessAction.RedactSensitive); actions.Add(ProcessAction.RedactAllText); }
        return actions;
    }

    // ---- Convert -----------------------------------------------------------------------------

    static async Task<JobResult> ConvertAsync(IReadOnlyList<string> paths, string workDir, bool toPng, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var set = InputExpander.Expand(paths, workDir, expandZips: false);
        var result = new JobResult { BytesBefore = set.TotalSize };
        var produced = new List<ProducedFile>();
        int n = 0;

        foreach (var item in set.Items)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new JobProgress($"Wandle um: {Path.GetFileName(item.SourcePath)}", (double)n++ / set.Items.Count));
            try
            {
                if (item.Kind == ItemKind.Image)
                {
                    var decoded = ImageCodec.Load(item.SourcePath);
                    byte[] bytes = toPng
                        ? ImageCodec.EncodePng(decoded.Bitmap)
                        : ImageCodec.EncodeJpeg(decoded.HasAlpha ? ImageCodec.FlattenOnWhite(decoded.Bitmap) : decoded.Bitmap, 92);
                    var ext = toPng ? ".png" : ".jpg";
                    var path = WorkFiles.Path(workDir, item, "conv", ext);
                    File.WriteAllBytes(path, bytes);
                    produced.Add(new ProducedFile(item, path, WorkFiles.WithExtension(item.RelativePath, ext), bytes.Length, true));
                }
                else if (item.Kind == ItemKind.Pdf && !toPng)
                {
                    var folder = Path.Combine(workDir, "pages_" + n);
                    Directory.CreateDirectory(folder);
                    var stem = Path.GetFileNameWithoutExtension(item.SourcePath);
                    foreach (var page in await PdfRasterizer.ExportPagesAsJpegAsync(item.SourcePath, folder, stem, 150, 88, ct))
                    {
                        var rel = Path.Combine(Path.GetDirectoryName(item.RelativePath) ?? "", Path.GetFileName(page));
                        produced.Add(new ProducedFile(item, page, rel, new FileInfo(page).Length, true));
                    }
                }
                else
                {
                    result.Warnings.Add($"{item.RelativePath}: kein Bild – übersprungen.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Convert " + item.SourcePath, ex);
                result.Warnings.Add($"{item.RelativePath}: {(ex is UserFacingException ? ex.Message : "konnte nicht umgewandelt werden")}");
            }
        }

        return Complete(set, produced, "", result, toPng ? "In PNG umgewandelt" : "In JPEG umgewandelt");
    }

    static JobResult ImagesToPdf(IReadOnlyList<string> paths, string workDir, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var set = InputExpander.Expand(paths, workDir, expandZips: false);
        var images = set.Items.Where(i => i.Kind == ItemKind.Image).OrderBy(i => i.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (images.Count == 0) throw new UserFacingException("Keine Bilder gefunden.");

        var writer = new MiniPdfWriter();
        int n = 0;
        foreach (var item in images)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new JobProgress($"Seite {n + 1} von {images.Count} …", (double)n++ / images.Count));
            var decoded = ImageCodec.Load(item.SourcePath);
            var bitmap = ImageCodec.Downscale(decoded.HasAlpha ? ImageCodec.FlattenOnWhite(decoded.Bitmap) : decoded.Bitmap, 3000);
            var jpeg = ImageCodec.EncodeJpeg(bitmap, 85);
            // Long edge = A4 long edge (842 pt), page in the image's aspect ratio.
            double scale = 842.0 / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
            writer.AddJpegPage(jpeg, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth * scale, bitmap.PixelHeight * scale);
        }

        var dir = OutputPlanner.ResolveDirectory(set.BaseDirectory);
        var name = images.Count == 1 ? Path.GetFileNameWithoutExtension(images[0].SourcePath) + ".pdf" : (set.ContainerName ?? "Bilder") + ".pdf";
        var output = OutputPlanner.UniquePath(dir, name);
        writer.Save(output);

        var result = new JobResult { BytesBefore = images.Sum(i => i.Size), Title = $"PDF mit {writer.PageCount} Seite{(writer.PageCount == 1 ? "" : "n")} erstellt" };
        result.Outputs.Add(output);
        result.BytesAfter = new FileInfo(output).Length;
        result.Detail = Format.Size(result.BytesAfter);
        return result;
    }

    // ---- Metadata ----------------------------------------------------------------------------

    static JobResult StripMetadata(IReadOnlyList<string> paths, string workDir, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var set = InputExpander.Expand(paths, workDir, expandZips: false);
        var result = new JobResult { BytesBefore = set.TotalSize };
        var produced = new List<ProducedFile>();
        int n = 0;

        foreach (var item in set.Items)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new JobProgress($"Metadaten: {Path.GetFileName(item.SourcePath)}", (double)n++ / set.Items.Count));
            try
            {
                switch (item.Kind)
                {
                    case ItemKind.Image:
                        var light = ImageCompressor.Light(item, workDir, stripMetadata: true);
                        produced.Add(light);
                        break;
                    case ItemKind.Pdf:
                    {
                        var path = WorkFiles.Path(workDir, item, "meta", ".pdf");
                        PdfCompressor.StripMetadataOnly(item.SourcePath, path);
                        produced.Add(new ProducedFile(item, path, item.RelativePath, new FileInfo(path).Length, true));
                        break;
                    }
                    case ItemKind.Office:
                    {
                        var path = WorkFiles.Path(workDir, item, "meta", item.Extension);
                        OfficeCompressor.StripMetadataOnly(item.SourcePath, path);
                        produced.Add(new ProducedFile(item, path, item.RelativePath, new FileInfo(path).Length, true));
                        break;
                    }
                    default:
                        produced.Add(new ProducedFile(item, item.SourcePath, item.RelativePath, item.Size, false));
                        if (item.Kind == ItemKind.Video) result.Warnings.Add($"{item.RelativePath}: Video-Metadaten werden nur beim Komprimieren entfernt.");
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Strip metadata " + item.SourcePath, ex);
                result.Warnings.Add($"{item.RelativePath}: {(ex is UserFacingException ? ex.Message : "konnte nicht bereinigt werden")}");
            }
        }

        var changed = produced.Where(p => p.Changed).ToList();
        if (changed.Count == 0 && !set.HasContainer)
        {
            result.Title = "Keine Metadaten gefunden";
            result.Outputs.AddRange(produced.Select(p => p.Input.SourcePath));
            result.BytesAfter = result.BytesBefore;
            return result;
        }
        return Complete(set, set.HasContainer ? produced : changed, "_ohne-metadaten", result, "Metadaten entfernt");
    }

    // ---- Redaction ---------------------------------------------------------------------------

    static async Task<JobResult> RedactAsync(IReadOnlyList<string> paths, string workDir, RedactionMode mode, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var set = InputExpander.Expand(paths, workDir, expandZips: false);
        var result = new JobResult { BytesBefore = set.TotalSize };
        var produced = new List<ProducedFile>();
        int boxes = 0, n = 0;

        foreach (var item in set.Items.Where(i => i.Kind is ItemKind.Image or ItemKind.Pdf))
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new JobProgress($"Texterkennung: {Path.GetFileName(item.SourcePath)}", (double)n++ / set.Items.Count));
            if (item.Kind == ItemKind.Image)
            {
                var decoded = ImageCodec.Load(item.SourcePath);
                var redacted = await Redactor.RedactAsync(decoded.Bitmap, mode, ct);
                boxes += redacted.Boxes;
                bool png = item.Extension == ".png" || decoded.HasAlpha;
                var ext = png ? ".png" : ".jpg";
                var bytes = png ? ImageCodec.EncodePng(redacted.Image) : ImageCodec.EncodeJpeg(redacted.Image, 90);
                var path = WorkFiles.Path(workDir, item, "redact", ext);
                File.WriteAllBytes(path, bytes);
                produced.Add(new ProducedFile(item, path, WorkFiles.WithExtension(item.RelativePath, ext), bytes.Length, true));
            }
            else
            {
                var path = WorkFiles.Path(workDir, item, "redact", ".pdf");
                int position = n - 1;
                var pageProgress = new Progress<double>(p => progress.Report(new JobProgress(
                    $"Schwärze {Path.GetFileName(item.SourcePath)} … {p:P0}", (position + p) / set.Items.Count)));
                boxes += await PdfRasterizer.RasterizeAsync(item.SourcePath, path, 150, 80, mode, pageProgress, ct);
                produced.Add(new ProducedFile(item, path, item.RelativePath, new FileInfo(path).Length, true));
            }
        }

        if (produced.Count == 0) throw new UserFacingException("Schwärzen geht nur bei Bildern und PDFs.");
        var title = boxes == 0 ? "Nichts zum Schwärzen gefunden" : $"{boxes} Stelle{(boxes == 1 ? "" : "n")} geschwärzt";
        var done = Complete(set, produced, "_geschwärzt", result, title);
        if (boxes > 0) done.Warnings.Add("Bitte vor dem Versand kurz prüfen – Texterkennung ist nie perfekt.");
        return done;
    }

    // ---- Zip ---------------------------------------------------------------------------------

    static JobResult ZipOnly(IReadOnlyList<string> paths, string workDir, IProgress<JobProgress> progress)
    {
        progress.Report(new JobProgress("ZIP wird erstellt …"));
        var set = InputExpander.Expand(paths, workDir, expandZips: false);
        var files = set.Items.Select(i => new ProducedFile(i, i.SourcePath, i.RelativePath, i.Size, false)).ToList();
        var result = new JobResult { BytesBefore = set.TotalSize };
        return Complete(set, files, "", result, "ZIP erstellt", forceZip: true);
    }

    // ---- common ------------------------------------------------------------------------------

    static JobResult Complete(InputSet set, List<ProducedFile> produced, string suffix, JobResult result, string title, bool forceZip = false)
    {
        if (produced.Count == 0) throw new UserFacingException(result.Warnings.FirstOrDefault() ?? "Nichts zu tun.");
        var outputs = OutputPlanner.Deliver(set, produced, suffix, forceZip);
        result.Outputs.AddRange(outputs);
        result.BytesAfter = outputs.Sum(o => new FileInfo(o).Length);
        result.Title = title;
        result.Detail = $"{Format.Files(outputs.Count)} · {Format.Size(result.BytesAfter)}";
        return result;
    }
}

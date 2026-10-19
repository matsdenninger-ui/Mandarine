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
                ProcessAction.ImagesToPdf => await ToPdfAsync(paths, workDir, progress, ct),
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

    /// <summary>Every function accepts every file type; files a function can't use are reported, not refused.</summary>
    public static HashSet<ProcessAction> AvailableActions(IReadOnlyList<string> paths) => Enum.GetValues<ProcessAction>().ToHashSet();

    // ---- Convert -----------------------------------------------------------------------------

    /// <summary>
    /// Images → JPEG/PNG; PDFs and Office documents → one image per page; everything else (videos, other files)
    /// → its Windows preview image.
    /// </summary>
    static async Task<JobResult> ConvertAsync(IReadOnlyList<string> paths, string workDir, bool toPng, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var set = InputExpander.Expand(paths, workDir, expandZips: false);
        var result = new JobResult { BytesBefore = set.TotalSize };
        var produced = new List<ProducedFile>();
        string ext = toPng ? ".png" : ".jpg";
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
                    produced.Add(WriteImage(item, workDir, decoded.Bitmap, toPng, "conv"));
                    continue;
                }

                string? pdf = item.Kind == ItemKind.Pdf ? item.SourcePath : null;
                if (pdf == null && DocumentConverter.IsOfficeDocument(item.SourcePath))
                {
                    progress.Report(new JobProgress($"Öffne {Path.GetFileName(item.SourcePath)} mit Office …", (double)(n - 1) / set.Items.Count));
                    pdf = DocumentConverter.ToPdf(item.SourcePath, workDir, ct);
                }

                if (pdf != null)
                {
                    var folder = Path.Combine(workDir, "pages_" + n);
                    Directory.CreateDirectory(folder);
                    var stem = Path.GetFileNameWithoutExtension(item.SourcePath);
                    foreach (var page in await PdfRasterizer.ExportPagesAsync(pdf, folder, stem, 150, toPng, 88, ct))
                    {
                        var rel = Path.Combine(Path.GetDirectoryName(item.RelativePath) ?? "", Path.GetFileName(page));
                        produced.Add(new ProducedFile(item, page, rel, new FileInfo(page).Length, true));
                    }
                    continue;
                }

                var preview = await DocumentConverter.ThumbnailAsync(item.SourcePath, 1600);
                if (preview != null)
                {
                    produced.Add(WriteImage(item, workDir, preview, toPng, "preview"));
                    if (DocumentConverter.IsOfficeDocument(item.SourcePath))
                        result.Warnings.Add($"{item.RelativePath}: Microsoft Office nicht gefunden – nur Vorschaubild.");
                }
                else
                {
                    result.Warnings.Add($"{item.RelativePath}: konnte nicht in ein Bild umgewandelt werden.");
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

    static ProducedFile WriteImage(InputItem item, string workDir, System.Windows.Media.Imaging.BitmapSource bitmap, bool png, string tag)
    {
        bool alpha = bitmap.Format == System.Windows.Media.PixelFormats.Bgra32;
        byte[] bytes = png
            ? ImageCodec.EncodePng(bitmap)
            : ImageCodec.EncodeJpeg(alpha ? ImageCodec.FlattenOnWhite(bitmap) : bitmap, 92);
        var ext = png ? ".png" : ".jpg";
        var path = WorkFiles.Path(workDir, item, tag, ext);
        File.WriteAllBytes(path, bytes);
        return new ProducedFile(item, path, WorkFiles.WithExtension(item.RelativePath, ext), bytes.Length, true);
    }

    // ---- To PDF ------------------------------------------------------------------------------

    /// <summary>
    /// Everything into one PDF, in name order: images become pages, PDFs are appended as they are (text stays),
    /// Office documents are converted with the installed Office, other files contribute their preview image.
    /// </summary>
    static async Task<JobResult> ToPdfAsync(IReadOnlyList<string> paths, string workDir, IProgress<JobProgress> progress, CancellationToken ct)
    {
        var set = InputExpander.Expand(paths, workDir, expandZips: false);
        var items = set.Items.OrderBy(i => i.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToList();
        var result = new JobResult { BytesBefore = set.TotalSize };
        var merged = new PdfSharp.Pdf.PdfDocument();
        int n = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new JobProgress($"{Path.GetFileName(item.SourcePath)} ({n + 1}/{items.Count}) …", (double)n++ / items.Count));
            try
            {
                string? pdf = null;
                if (item.Kind == ItemKind.Pdf)
                {
                    pdf = item.SourcePath;
                }
                else if (item.Kind == ItemKind.Image)
                {
                    pdf = ImagePdf(ImageCodec.Load(item.SourcePath).Bitmap, workDir, item);
                }
                else if (DocumentConverter.IsOfficeDocument(item.SourcePath))
                {
                    progress.Report(new JobProgress($"Öffne {Path.GetFileName(item.SourcePath)} mit Office …", (double)(n - 1) / items.Count));
                    pdf = DocumentConverter.ToPdf(item.SourcePath, workDir, ct);
                    if (pdf == null) result.Warnings.Add($"{item.RelativePath}: Microsoft Office nicht gefunden – nur Vorschaubild.");
                }

                if (pdf == null)
                {
                    var preview = await DocumentConverter.ThumbnailAsync(item.SourcePath, 1600);
                    if (preview != null) pdf = ImagePdf(preview, workDir, item);
                }

                if (pdf == null)
                {
                    result.Warnings.Add($"{item.RelativePath}: konnte nicht in die PDF übernommen werden.");
                    continue;
                }

                if (!AppendPdf(merged, pdf))
                {
                    // Protected or unusual PDF: append it as page images instead.
                    var raster = WorkFiles.Path(workDir, item, "raster", ".pdf");
                    await PdfRasterizer.RasterizeAsync(pdf, raster, 150, 85, RedactionMode.None, null, ct);
                    if (!AppendPdf(merged, raster)) result.Warnings.Add($"{item.RelativePath}: PDF konnte nicht übernommen werden.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("To PDF " + item.SourcePath, ex);
                result.Warnings.Add($"{item.RelativePath}: {(ex is UserFacingException ? ex.Message : "konnte nicht übernommen werden")}");
            }
        }

        if (merged.PageCount == 0) throw new UserFacingException(result.Warnings.FirstOrDefault() ?? "Keine Seiten erzeugt.");

        var dir = OutputPlanner.ResolveDirectory(set.BaseDirectory);
        var name = items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].SourcePath) + ".pdf" : (set.ContainerName ?? "Zusammengefasst") + ".pdf";
        var output = OutputPlanner.UniquePath(dir, name);
        if (items.Count == 1 && string.Equals(items[0].SourcePath, output, StringComparison.OrdinalIgnoreCase))
            output = OutputPlanner.UniquePath(dir, Path.GetFileNameWithoutExtension(name) + "_neu.pdf");
        merged.Save(output);

        result.Title = $"PDF mit {merged.PageCount} Seite{(merged.PageCount == 1 ? "" : "n")} erstellt";
        result.Outputs.Add(output);
        result.BytesAfter = new FileInfo(output).Length;
        result.Detail = Format.Size(result.BytesAfter);
        return result;
    }

    /// <summary>One image as a single-page PDF (long edge = A4 long edge, page in the image's aspect ratio).</summary>
    static string ImagePdf(System.Windows.Media.Imaging.BitmapSource source, string workDir, InputItem item)
    {
        bool alpha = source.Format == System.Windows.Media.PixelFormats.Bgra32;
        var bitmap = ImageCodec.Downscale(alpha ? ImageCodec.FlattenOnWhite(source) : source, 3000);
        var jpeg = ImageCodec.EncodeJpeg(bitmap, 85);
        double scale = 842.0 / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
        var writer = new MiniPdfWriter();
        writer.AddJpegPage(jpeg, bitmap.PixelWidth, bitmap.PixelHeight, bitmap.PixelWidth * scale, bitmap.PixelHeight * scale);
        var path = WorkFiles.Path(workDir, item, "page", ".pdf");
        writer.Save(path);
        return path;
    }

    static bool AppendPdf(PdfSharp.Pdf.PdfDocument target, string pdfPath)
    {
        try
        {
            var input = PdfSharp.Pdf.IO.PdfReader.Open(pdfPath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            foreach (PdfSharp.Pdf.PdfPage page in input.Pages) target.AddPage(page);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"PDF import failed ({pdfPath}): {ex.Message}");
            return false;
        }
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

        foreach (var item in set.Items)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new JobProgress($"Texterkennung: {Path.GetFileName(item.SourcePath)}", (double)n++ / set.Items.Count));
            try
            {
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
                    continue;
                }

                // PDFs directly; Office documents via the installed Office → PDF; result is always an image PDF.
                string? pdf = item.Kind == ItemKind.Pdf ? item.SourcePath : null;
                if (pdf == null && DocumentConverter.IsOfficeDocument(item.SourcePath))
                {
                    progress.Report(new JobProgress($"Öffne {Path.GetFileName(item.SourcePath)} mit Office …", (double)(n - 1) / set.Items.Count));
                    pdf = DocumentConverter.ToPdf(item.SourcePath, workDir, ct);
                    if (pdf == null)
                    {
                        result.Warnings.Add($"{item.RelativePath}: zum Schwärzen wird Microsoft Office benötigt.");
                        continue;
                    }
                }
                if (pdf == null)
                {
                    result.Warnings.Add($"{item.RelativePath}: enthält keinen Text zum Schwärzen – übersprungen.");
                    continue;
                }

                var output = WorkFiles.Path(workDir, item, "redact", ".pdf");
                int position = n - 1;
                var pageProgress = new Progress<double>(p => progress.Report(new JobProgress(
                    $"Schwärze {Path.GetFileName(item.SourcePath)} … {p:P0}", (position + p) / set.Items.Count)));
                boxes += await PdfRasterizer.RasterizeAsync(pdf, output, 150, 80, mode, pageProgress, ct);
                produced.Add(new ProducedFile(item, output, WorkFiles.WithExtension(item.RelativePath, ".pdf"), new FileInfo(output).Length, true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error("Redact " + item.SourcePath, ex);
                result.Warnings.Add($"{item.RelativePath}: {(ex is UserFacingException ? ex.Message : "konnte nicht geschwärzt werden")}");
            }
        }

        if (produced.Count == 0) throw new UserFacingException(result.Warnings.FirstOrDefault() ?? "Schwärzen geht bei Bildern, PDFs und Office-Dokumenten.");
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

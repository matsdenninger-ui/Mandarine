using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Zytrus.Processing;

/// <summary>
/// Makes "every file" usable for the page/image functions:
///  • Office documents → PDF through the locally installed Microsoft Office (COM automation, no cloud).
///  • Anything else → the Windows preview thumbnail (videos show a frame, documents their first page if a
///    preview handler is installed, everything else at least its icon).
/// </summary>
internal static class DocumentConverter
{
    static readonly HashSet<string> WordTypes = new(StringComparer.OrdinalIgnoreCase)
    { ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt", ".txt", ".xml", ".htm", ".html", ".wps" };

    static readonly HashSet<string> PowerPointTypes = new(StringComparer.OrdinalIgnoreCase)
    { ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".pot", ".potx", ".odp" };

    static readonly HashSet<string> ExcelTypes = new(StringComparer.OrdinalIgnoreCase)
    { ".xls", ".xlsx", ".xlsm", ".xlsb", ".csv", ".ods" };

    public static bool IsOfficeDocument(string path)
    {
        var ext = Path.GetExtension(path);
        return WordTypes.Contains(ext) || PowerPointTypes.Contains(ext) || ExcelTypes.Contains(ext);
    }

    /// <summary>PDF next to the work files, or null if Office is missing or the conversion failed.</summary>
    public static string? ToPdf(string path, string outputFolder, CancellationToken ct)
    {
        if (!IsOfficeDocument(path)) return null;
        string? result = null;
        var thread = new Thread(() =>
        {
            try { result = ConvertOnSta(path, outputFolder); }
            catch (Exception ex) { Log.Error("Office → PDF " + path, ex); }
        })
        {
            IsBackground = true,
            Name = "Office conversion"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!thread.Join(200))
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
            {
                Log.Write("Office conversion abandoned: " + path);
                return null;
            }
        }
        return result != null && File.Exists(result) ? result : null;
    }

    static string? ConvertOnSta(string path, string outputFolder)
    {
        var ext = Path.GetExtension(path);
        var output = Path.Combine(outputFolder, $"{Guid.NewGuid():N}_{Path.GetFileNameWithoutExtension(path)}.pdf");

        if (WordTypes.Contains(ext)) return WithApp("Word.Application", app =>
        {
            app.Visible = false;
            app.DisplayAlerts = 0; // wdAlertsNone
            dynamic doc = app.Documents.Open(path, false, true, false); // ConfirmConversions, ReadOnly, AddToRecentFiles
            try { doc.ExportAsFixedFormat(output, 17); } // wdExportFormatPDF
            finally { doc.Close(false); Marshal.FinalReleaseComObject(doc); }
            return output;
        }, app => (int)app.Documents.Count == 0);

        if (PowerPointTypes.Contains(ext)) return WithApp("PowerPoint.Application", app =>
        {
            dynamic presentation = app.Presentations.Open(path, -1, 0, 0); // ReadOnly, Untitled = false, WithWindow = false
            try { presentation.SaveAs(output, 32); } // ppSaveAsPDF
            finally { presentation.Close(); Marshal.FinalReleaseComObject(presentation); }
            return output;
        }, app => (int)app.Presentations.Count == 0);

        if (ExcelTypes.Contains(ext)) return WithApp("Excel.Application", app =>
        {
            app.Visible = false;
            app.DisplayAlerts = false;
            dynamic workbook = app.Workbooks.Open(path, 0, true); // UpdateLinks = 0, ReadOnly
            try { workbook.ExportAsFixedFormat(0, output); } // xlTypePDF
            finally { workbook.Close(false); Marshal.FinalReleaseComObject(workbook); }
            return output;
        }, app => (int)app.Workbooks.Count == 0);

        return null;
    }

    /// <summary>Starts the Office app, runs the job and quits it again – unless the user already had documents open in it.</summary>
    static string? WithApp(string progId, Func<dynamic, string> job, Func<dynamic, bool> isIdle)
    {
        var type = Type.GetTypeFromProgID(progId);
        if (type == null)
        {
            Log.Write(progId + " is not installed.");
            return null;
        }

        dynamic app = Activator.CreateInstance(type)!;
        bool quitAfterwards = true;
        try
        {
            try { quitAfterwards = isIdle(app); } catch { quitAfterwards = false; }
            return job(app);
        }
        finally
        {
            try
            {
                // Re-check: never close an Office window the user is working in.
                if (quitAfterwards && isIdle(app)) app.Quit();
            }
            catch (Exception ex)
            {
                Log.Write($"{progId} quit: {ex.Message}");
            }
            Marshal.FinalReleaseComObject(app);
        }
    }

    /// <summary>The Windows preview image of any file (what Explorer shows as a large icon), or null.</summary>
    public static async Task<BitmapSource?> ThumbnailAsync(string path, uint size)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail == null || thumbnail.Size == 0) return null;
            using var stream = thumbnail.AsStream();
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = ImageCodec.Materialize(new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0));
            return frame;
        }
        catch (Exception ex)
        {
            Log.Write($"Thumbnail failed for {path}: {ex.Message}");
            return null;
        }
    }
}

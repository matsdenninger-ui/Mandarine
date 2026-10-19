using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zytrus.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace Zytrus.Processing;

/// <summary>Renders PDF pages with the built-in Windows PDF engine (Windows.Data.Pdf).</summary>
internal static class PdfRasterizer
{
    const int MaxPagePixels = 6000;

    public sealed record RenderedPage(BitmapSource Image, double PointWidth, double PointHeight);

    public static async Task<WinPdf.PdfDocument> OpenAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        try
        {
            return await WinPdf.PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex)
        {
            Log.Error("PDF load", ex);
            throw new UserFacingException("Die PDF konnte nicht geöffnet werden (beschädigt oder passwortgeschützt).");
        }
    }

    public static async Task<RenderedPage> RenderPageAsync(WinPdf.PdfDocument pdf, uint index, int dpi)
    {
        using var page = pdf.GetPage(index);
        double widthIn = page.Size.Width / 96.0, heightIn = page.Size.Height / 96.0; // Size is in DIPs
        double px = Math.Max(widthIn, heightIn) * dpi;
        double effectiveDpi = px > MaxPagePixels ? dpi * MaxPagePixels / px : dpi;

        var options = new WinPdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(widthIn * effectiveDpi)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(heightIn * effectiveDpi))
        };

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        stream.Seek(0);

        BitmapSource image;
        using (var managed = stream.AsStream())
        {
            var decoder = BitmapDecoder.Create(managed, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            image = ImageCodec.Materialize(new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgr24, null, 0));
        }
        return new RenderedPage(image, widthIn * 72, heightIn * 72);
    }

    /// <summary>Writes a new image-only PDF. With redaction, OCR runs on each page before encoding.</summary>
    public static async Task<int> RasterizeAsync(
        string source, string destination, int dpi, int quality, RedactionMode redaction,
        IProgress<double>? progress, CancellationToken ct)
    {
        var pdf = await OpenAsync(source);
        var writer = new MiniPdfWriter();
        int boxes = 0;

        for (uint i = 0; i < pdf.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await RenderPageAsync(pdf, i, dpi);
            var image = page.Image;
            if (redaction != RedactionMode.None)
            {
                var result = await Redactor.RedactAsync(image, redaction, ct);
                image = result.Image;
                boxes += result.Boxes;
            }
            var jpeg = ImageCodec.EncodeJpeg(image, quality);
            writer.AddJpegPage(jpeg, image.PixelWidth, image.PixelHeight, page.PointWidth, page.PointHeight);
            progress?.Report((i + 1.0) / pdf.PageCount);
        }

        writer.Save(destination);
        return boxes;
    }

    /// <summary>Exports every page as JPEG or PNG; returns the written files.</summary>
    public static async Task<List<string>> ExportPagesAsync(string source, string folder, string baseName, int dpi, bool png, int quality, CancellationToken ct)
    {
        var pdf = await OpenAsync(source);
        var files = new List<string>();
        int digits = Math.Max(2, pdf.PageCount.ToString().Length);
        for (uint i = 0; i < pdf.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var page = await RenderPageAsync(pdf, i, dpi);
            var path = Path.Combine(folder, $"{baseName}_Seite-{(i + 1).ToString().PadLeft(digits, '0')}{(png ? ".png" : ".jpg")}");
            File.WriteAllBytes(path, png ? ImageCodec.EncodePng(page.Image) : ImageCodec.EncodeJpeg(page.Image, quality));
            files.Add(path);
        }
        return files;
    }
}

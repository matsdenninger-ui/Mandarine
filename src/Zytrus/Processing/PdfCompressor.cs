using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Zytrus.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Zytrus.Processing;

/// <summary>
/// Two strategies, tried in this order by the ladder:
///  1. Structural (levels 0–3): keep the PDF (text stays selectable), re-encode embedded photos as JPEG at lower DPI.
///  2. Raster (levels 4+): render each page and rebuild an image-only PDF – for scans that are still too big.
/// </summary>
internal static class PdfCompressor
{
    static readonly (int Dpi, int Quality)[] Structural = { (200, 80), (150, 74), (120, 68), (96, 62) };
    static readonly (int Dpi, int Quality)[] Raster = { (130, 70), (110, 62), (90, 55) };

    public static int MaxLevel => Structural.Length + Raster.Length - 1;

    public static bool IsRasterLevel(int level) => level >= Structural.Length;

    public static async Task<ProducedFile> AtLevelAsync(InputItem item, int level, string workDir, bool stripMetadata, CancellationToken ct)
    {
        var output = WorkFiles.Path(workDir, item, $"pdf{level}", ".pdf");
        if (!IsRasterLevel(level))
        {
            var (dpi, quality) = Structural[level];
            await Task.Run(() => RecompressImages(item.SourcePath, output, dpi, quality, stripMetadata), ct);
        }
        else
        {
            var (dpi, quality) = Raster[level - Structural.Length];
            await PdfRasterizer.RasterizeAsync(item.SourcePath, output, dpi, quality, RedactionMode.None, null, ct);
        }
        return new ProducedFile(item, output, item.RelativePath, new FileInfo(output).Length, true);
    }

    /// <summary>Removes author/creator info and the XMP packet, keeps everything else.</summary>
    public static void StripMetadataOnly(string source, string destination)
    {
        var doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
        StripInfo(doc);
        doc.Save(destination);
    }

    static void RecompressImages(string source, string destination, int dpi, int quality, bool stripMetadata)
    {
        PdfDocument doc;
        try
        {
            doc = PdfReader.Open(source, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex)
        {
            Log.Error("PDFsharp open", ex);
            throw new UserFacingException("Die PDF ist passwortgeschützt oder beschädigt.");
        }

        double maxPageInches = 8.27; // A4 fallback
        foreach (PdfPage page in doc.Pages)
            maxPageInches = Math.Max(maxPageInches, Math.Max(page.Width.Point, page.Height.Point) / 72.0);
        int capPixels = (int)Math.Round(maxPageInches * dpi);

        var objects = doc.Internals.GetAllObjects();

        // Soft masks (alpha channels) must stay lossless, otherwise edges get JPEG halos.
        var softMasks = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        foreach (var obj in objects)
            if (obj is PdfDictionary d && IsImage(d) && d.Elements.GetObject("/SMask") is PdfObject mask)
                softMasks.Add(mask);

        int changed = 0;
        foreach (var obj in objects)
        {
            if (obj is not PdfDictionary image || !IsImage(image) || softMasks.Contains(image)) continue;
            try
            {
                if (TryRecompress(image, capPixels, quality)) changed++;
            }
            catch (Exception ex)
            {
                Log.Write("PDF image skipped: " + ex.Message);
            }
        }

        if (stripMetadata) StripInfo(doc);
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        doc.Options.CompressContentStreams = true;
        doc.Save(destination);
        Log.Write($"PDF: {changed} images re-encoded at {dpi} dpi / Q{quality}");
    }

    static bool IsImage(PdfDictionary d) => d.Stream != null && NameOf(d.Elements["/Subtype"]) == "/Image";

    static bool TryRecompress(PdfDictionary image, int capPixels, int quality)
    {
        var e = image.Elements;
        if (e.ContainsKey("/ImageMask") && e.GetBoolean("/ImageMask")) return false;
        if (e.ContainsKey("/Decode")) return false;

        int width = e.GetInteger("/Width"), height = e.GetInteger("/Height");
        if (width < 64 || height < 64) return false;

        string? filter = SingleFilter(e["/Filter"]);
        int components = ComponentCount(e["/ColorSpace"]);
        if (filter == null || components == 0) return false;

        byte[] raw = image.Stream!.Value;
        BitmapSource bitmap;

        if (filter == "/DCTDecode")
        {
            using var ms = new MemoryStream(raw);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            bitmap = decoder.Frames[0];
            if (bitmap.Format == PixelFormats.Cmyk32) return false;
        }
        else if ((filter == "/FlateDecode" || filter == "") && e.GetInteger("/BitsPerComponent") == 8)
        {
            byte[] pixels = filter == "" ? raw : image.Stream.UnfilteredValue;
            int stride = width * components;
            if (pixels.Length < stride * height) return false; // unknown predictor etc.
            bitmap = BitmapSource.Create(width, height, 96, 96,
                components == 3 ? PixelFormats.Rgb24 : PixelFormats.Gray8, null, pixels, stride);
        }
        else
        {
            return false;
        }

        var target = components == 3 ? PixelFormats.Bgr24 : PixelFormats.Gray8;
        BitmapSource normalized = bitmap.Format == target ? bitmap : new FormatConvertedBitmap(bitmap, target, null, 0);
        var scaled = ImageCodec.Downscale(ImageCodec.Materialize(normalized), capPixels);
        bool resized = scaled.PixelWidth != width;

        // Don't re-encode JPEGs that are already small enough: generation loss for no gain.
        if (filter == "/DCTDecode" && !resized && quality >= 74) return false;

        var jpeg = ImageCodec.EncodeJpeg(scaled, quality);
        if (jpeg.Length >= raw.Length * 0.9) return false;

        image.Stream.Value = jpeg; // also updates /Length
        e.SetName("/Filter", "/DCTDecode");
        e.Remove("/DecodeParms");
        e.SetInteger("/Width", scaled.PixelWidth);
        e.SetInteger("/Height", scaled.PixelHeight);
        e.SetInteger("/BitsPerComponent", 8);
        return true;
    }

    static void StripInfo(PdfDocument doc)
    {
        foreach (var key in new[] { "/Author", "/Creator", "/Keywords", "/Subject" })
            doc.Info.Elements.Remove(key);
        doc.Internals.Catalog.Elements.Remove("/Metadata");
    }

    // ---- small PDF object helpers ---------------------------------------------------------------

    static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    static string? NameOf(PdfItem? item) => Resolve(item) switch
    {
        PdfName n => n.Value,
        PdfNameObject n => n.Value,
        _ => null
    };

    /// <summary>"" for no filter, the filter name for exactly one filter, null for chains we don't handle.</summary>
    static string? SingleFilter(PdfItem? item)
    {
        var resolved = Resolve(item);
        if (resolved == null) return "";
        if (resolved is PdfArray array) return array.Elements.Count == 1 ? NameOf(array.Elements[0]) : null;
        return NameOf(resolved);
    }

    /// <summary>1 or 3 for gray/RGB-like color spaces, 0 for anything we leave alone (CMYK, Indexed, Separation…).</summary>
    static int ComponentCount(PdfItem? item)
    {
        var resolved = Resolve(item);
        var name = NameOf(resolved);
        if (name != null) return name switch { "/DeviceRGB" => 3, "/DeviceGray" => 1, _ => 0 };

        if (resolved is PdfArray array && array.Elements.Count >= 2)
        {
            switch (NameOf(array.Elements[0]))
            {
                case "/ICCBased":
                    var stream = Resolve(array.Elements[1]) as PdfDictionary;
                    int n = stream?.Elements.GetInteger("/N") ?? 0;
                    return n is 1 or 3 ? n : 0;
                case "/CalRGB": return 3;
                case "/CalGray": return 1;
            }
        }
        return 0;
    }
}

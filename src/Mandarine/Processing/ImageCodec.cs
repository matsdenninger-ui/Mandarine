using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mandarine.Processing;

/// <summary>
/// Decoding, orientation, color management, scaling and encoding on top of WPF imaging (= Windows Imaging Component).
/// HEIC/AVIF/WebP decode through the codecs installed in Windows ("HEIF-Bilderweiterungen" etc.).
/// Thread-safe as long as each thread works on its own objects; results are frozen.
/// </summary>
internal static class ImageCodec
{
    public sealed record Decoded(BitmapSource Bitmap, bool HasAlpha);

    /// <summary>Loads the first frame, converts it to sRGB, applies EXIF orientation and normalizes to Bgr24/Bgra32.</summary>
    public static Decoded Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(stream);
    }

    public static Decoded Load(Stream stream)
    {
        BitmapDecoder decoder;
        try
        {
            decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or System.Runtime.InteropServices.COMException)
        {
            throw new Core.UserFacingException(
                "Bildformat wird nicht unterstützt. Für HEIC/AVIF bitte die kostenlosen „HEIF-Bilderweiterungen“ aus dem Microsoft Store installieren.");
        }

        BitmapFrame frame = decoder.Frames[0];
        BitmapSource source = frame;

        // Color management: convert embedded profiles (e.g. Display P3 from phones) to sRGB before we drop the profile.
        try
        {
            if (frame.ColorContexts is { Count: > 0 } contexts && frame.Format != PixelFormats.Cmyk32)
            {
                var target = HasAlphaChannel(frame) ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
                source = new ColorConvertedBitmap(frame, contexts[0], new ColorContext(PixelFormats.Bgra32), target);
            }
        }
        catch
        {
            source = frame;
        }

        source = ApplyOrientation(source, ReadOrientation(frame.Metadata as BitmapMetadata));

        if (HasAlphaChannel(source))
        {
            var bgra = Materialize(new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0));
            return AlphaIsUsed(bgra)
                ? new Decoded(bgra, true)
                : new Decoded(Materialize(new FormatConvertedBitmap(bgra, PixelFormats.Bgr24, null, 0)), false);
        }
        return new Decoded(Materialize(new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0)), false);
    }

    public static ushort ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata == null) return 1;
        foreach (var query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                var value = metadata.GetQuery(query);
                if (value != null)
                {
                    var o = Convert.ToUInt16(value);
                    if (o is >= 1 and <= 8) return o;
                }
            }
            catch
            {
                // query not supported by this container
            }
        }
        return 1;
    }

    static BitmapSource ApplyOrientation(BitmapSource source, ushort orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => Group(new RotateTransform(90), new ScaleTransform(-1, 1)),
            6 => new RotateTransform(90),
            7 => Group(new RotateTransform(270), new ScaleTransform(-1, 1)),
            8 => new RotateTransform(270),
            _ => null
        };
        return transform == null ? source : new TransformedBitmap(source, transform);
    }

    static Transform Group(params Transform[] transforms)
    {
        var group = new TransformGroup();
        foreach (var t in transforms) group.Children.Add(t);
        return group;
    }

    static bool HasAlphaChannel(BitmapSource s)
    {
        var f = s.Format;
        if (f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64 ||
            f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float)
            return true;
        return s.Palette != null && s.Palette.Colors.Any(c => c.A < 255);
    }

    /// <summary>Expects a materialized Bgra32 bitmap; true if any pixel is noticeably transparent.</summary>
    static bool AlphaIsUsed(BitmapSource bgra)
    {
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] < 250) return true;
        return false;
    }

    /// <summary>Copies a lazy bitmap chain into memory once, so repeated encodes don't re-run the pipeline.</summary>
    public static BitmapSource Materialize(BitmapSource source)
    {
        var wb = new WriteableBitmap(source);
        wb.Freeze();
        return wb;
    }

    /// <summary>High-quality downscale so the long edge is at most maxLongEdge (never upscales).</summary>
    public static BitmapSource Downscale(BitmapSource source, int maxLongEdge)
    {
        int longEdge = Math.Max(source.PixelWidth, source.PixelHeight);
        if (maxLongEdge <= 0 || longEdge <= maxLongEdge) return source;

        BitmapSource current = source;
        // Halve first for big factors – avoids aliasing from a single bilinear step.
        while (Math.Max(current.PixelWidth, current.PixelHeight) / 2 >= maxLongEdge * 1.1)
            current = Materialize(new TransformedBitmap(current, new ScaleTransform(0.5, 0.5)));

        double factor = (double)maxLongEdge / Math.Max(current.PixelWidth, current.PixelHeight);
        if (factor < 0.999)
            current = Materialize(new TransformedBitmap(current, new ScaleTransform(factor, factor)));
        return current;
    }

    public static BitmapSource Scale(BitmapSource source, double factor) =>
        factor >= 0.999 ? source : Materialize(new TransformedBitmap(source, new ScaleTransform(factor, factor)));

    /// <summary>Removes alpha by compositing onto white.</summary>
    public static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        var buffer = PixelBuffer.From(source);
        var d = buffer.Data;
        for (int i = 0; i < d.Length; i += 4)
        {
            int a = d[i + 3];
            if (a == 255) continue;
            d[i] = (byte)((d[i] * a + 255 * (255 - a)) / 255);
            d[i + 1] = (byte)((d[i + 1] * a + 255 * (255 - a)) / 255);
            d[i + 2] = (byte)((d[i + 2] * a + 255 * (255 - a)) / 255);
            d[i + 3] = 255;
        }
        return Materialize(new FormatConvertedBitmap(buffer.ToBitmap(), PixelFormats.Bgr24, null, 0));
    }

    /// <summary>JPEG without any metadata (BitmapFrame.Create without metadata writes only JFIF).</summary>
    public static byte[] EncodeJpeg(BitmapSource source, int quality)
    {
        if (source.Format != PixelFormats.Bgr24 && source.Format != PixelFormats.Gray8)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static BitmapSource ToGray8(BitmapSource source) =>
        source.Format == PixelFormats.Gray8 ? source : Materialize(new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0));
}

/// <summary>Raw Bgra32 pixels for painting (redaction boxes, flattening).</summary>
internal sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Data { get; }

    PixelBuffer(int width, int height, byte[] data)
    {
        Width = width;
        Height = height;
        Data = data;
    }

    public static PixelBuffer From(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var data = new byte[bgra.PixelWidth * 4 * bgra.PixelHeight];
        bgra.CopyPixels(data, bgra.PixelWidth * 4, 0);
        return new PixelBuffer(bgra.PixelWidth, bgra.PixelHeight, data);
    }

    public void FillRect(int x, int y, int w, int h, byte r = 0, byte g = 0, byte b = 0)
    {
        int x0 = Math.Clamp(x, 0, Width), y0 = Math.Clamp(y, 0, Height);
        int x1 = Math.Clamp(x + w, 0, Width), y1 = Math.Clamp(y + h, 0, Height);
        for (int row = y0; row < y1; row++)
        {
            int i = row * Stride + x0 * 4;
            for (int col = x0; col < x1; col++, i += 4)
            {
                Data[i] = b;
                Data[i + 1] = g;
                Data[i + 2] = r;
                Data[i + 3] = 255;
            }
        }
    }

    public BitmapSource ToBitmap()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Data, Stride);
        bitmap.Freeze();
        return bitmap;
    }
}

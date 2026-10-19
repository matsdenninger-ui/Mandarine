using System;
using System.IO;
using Zytrus.Core;

namespace Zytrus.Processing;

/// <summary>Image steps of the email pipeline: a lossless "light" pass and a quality ladder.</summary>
internal static class ImageCompressor
{
    /// <summary>Long edge in px and JPEG quality, from gentle to aggressive.</summary>
    static readonly (int MaxEdge, int Quality)[] Ladder =
    {
        (3200, 85), (2560, 80), (2048, 76), (1600, 72), (1280, 68), (1024, 62), (800, 56)
    };

    public static int MaxLevel => Ladder.Length - 1;

    /// <summary>Formats most mail recipients can open everywhere. Others (HEIC, WebP, AVIF, TIFF, BMP) get converted.</summary>
    public static bool IsMailFriendly(string ext) => ext is ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".png";

    /// <summary>
    /// No visible change: strips metadata losslessly and converts exotic formats to JPEG/PNG.
    /// Returns the original path untouched when nothing needs to happen.
    /// </summary>
    public static ProducedFile Light(InputItem item, string workDir, bool stripMetadata)
    {
        if (IsMailFriendly(item.Extension))
        {
            if (!stripMetadata) return Unchanged(item);
            var stripped = MetadataStripper.TryStrip(item.SourcePath);
            if (stripped == null || stripped.LongLength == item.Size) return Unchanged(item); // nothing to remove
            return Write(item, workDir, stripped, item.Extension, "light");
        }

        var decoded = ImageCodec.Load(item.SourcePath);
        return decoded.HasAlpha
            ? Write(item, workDir, ImageCodec.EncodePng(decoded.Bitmap), ".png", "light")
            : Write(item, workDir, ImageCodec.EncodeJpeg(decoded.Bitmap, 90), ".jpg", "light");
    }

    /// <summary>Re-encodes at the given ladder level. Images with real transparency stay PNG and are only downscaled.</summary>
    public static ProducedFile AtLevel(InputItem item, int level, string workDir)
    {
        var (maxEdge, quality) = Ladder[Math.Clamp(level, 0, MaxLevel)];
        var decoded = ImageCodec.Load(item.SourcePath);
        var scaled = ImageCodec.Downscale(decoded.Bitmap, maxEdge);

        if (decoded.HasAlpha)
        {
            var png = ImageCodec.EncodePng(scaled);
            // Very large transparent images: flatten to JPEG on the aggressive steps.
            if (level >= 3 && png.Length > 1_500_000)
                return Write(item, workDir, ImageCodec.EncodeJpeg(ImageCodec.FlattenOnWhite(scaled), quality), ".jpg", $"l{level}");
            return Write(item, workDir, png, ".png", $"l{level}");
        }

        return Write(item, workDir, ImageCodec.EncodeJpeg(scaled, quality), ".jpg", $"l{level}");
    }

    static ProducedFile Unchanged(InputItem item) =>
        new(item, item.SourcePath, item.RelativePath, item.Size, false);

    static ProducedFile Write(InputItem item, string workDir, byte[] bytes, string ext, string tag)
    {
        var path = WorkFiles.Path(workDir, item, tag, ext);
        File.WriteAllBytes(path, bytes);
        return new ProducedFile(item, path, WorkFiles.WithExtension(item.RelativePath, ext), bytes.LongLength, true);
    }
}

/// <summary>Unique scratch file names inside a job's temp folder.</summary>
internal static class WorkFiles
{
    static int _counter;

    public static string Path(string workDir, InputItem item, string tag, string ext)
    {
        int n = System.Threading.Interlocked.Increment(ref _counter);
        var name = System.IO.Path.GetFileNameWithoutExtension(item.SourcePath);
        if (name.Length > 60) name = name[..60];
        return System.IO.Path.Combine(workDir, $"{n:D4}_{tag}_{name}{ext}");
    }

    public static string WithExtension(string relativePath, string ext) =>
        System.IO.Path.ChangeExtension(relativePath, ext);
}

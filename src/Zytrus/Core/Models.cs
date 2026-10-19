using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Zytrus.Core;

public enum ItemKind { Image, Video, Pdf, Office, Archive, Other }

public enum ProcessAction
{
    EmailCompress,
    EmailCompressSmall,
    ConvertJpeg,
    ConvertPng,
    ImagesToPdf,
    StripMetadata,
    RedactSensitive,
    RedactAllText,
    ZipOnly
}

public enum RedactionMode { None, Sensitive, AllText }

/// <summary>One input file. RelativePath keeps folder structure for zips ("Urlaub/IMG_1.jpg").</summary>
public sealed record InputItem(string SourcePath, string RelativePath, ItemKind Kind, long Size)
{
    public string Extension => Path.GetExtension(SourcePath).ToLowerInvariant();
}

/// <summary>A produced file plus the name it should get in the final output.</summary>
public sealed record ProducedFile(InputItem Input, string Path, string RelativePath, long Size, bool Changed);

public sealed record JobProgress(string Message, double Fraction = -1);

public sealed class JobResult
{
    public List<string> Outputs { get; } = new();
    public List<string> Warnings { get; } = new();
    public long BytesBefore { get; set; }
    public long BytesAfter { get; set; }
    public string Title { get; set; } = "Fertig";
    public string Detail { get; set; } = "";
}

/// <summary>Errors whose message is meant for the user (German).</summary>
public sealed class UserFacingException : Exception
{
    public UserFacingException(string message) : base(message) { }
}

public static class FileKinds
{
    static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".heic", ".heif", ".hif", ".avif", ".webp", ".tif", ".tiff", ".bmp" };

    static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".3gp", ".mts", ".m2ts", ".webm" };

    static readonly HashSet<string> Office = new(StringComparer.OrdinalIgnoreCase)
    { ".docx", ".docm", ".pptx", ".pptm", ".xlsx", ".xlsm" };

    /// <summary>Formats that do not shrink further when zipped.</summary>
    static readonly HashSet<string> Compressed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".heic", ".heif", ".webp", ".avif", ".mp4", ".m4v", ".mov", ".mkv",
        ".webm", ".mp3", ".m4a", ".aac", ".ogg", ".zip", ".7z", ".rar", ".gz", ".docx", ".xlsx", ".pptx", ".pdf"
    };

    public static ItemKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return ItemKind.Image;
        if (Videos.Contains(ext)) return ItemKind.Video;
        if (Office.Contains(ext)) return ItemKind.Office;
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return ItemKind.Pdf;
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)) return ItemKind.Archive;
        return ItemKind.Other;
    }

    public static bool IsAlreadyCompressed(string path) => Compressed.Contains(Path.GetExtension(path));
}

public static class Format
{
    static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Same units as Explorer (1 MB = 1024 × 1024 bytes).</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0", German) + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024.0).ToString("0.0", German) + " MB";
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00", German) + " GB";
    }

    public static string Megabytes(double mb) => mb.ToString("0.#", German) + " MB";

    public static string Files(int count) => count == 1 ? "1 Datei" : $"{count} Dateien";
}

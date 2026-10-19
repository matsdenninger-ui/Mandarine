using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Mandarine.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Mandarine.Processing;

/// <summary>
/// Blacks out text using the on-device Windows OCR engine (Windows.Media.Ocr, no cloud).
/// "Sensitive" only covers patterns like e-mail addresses, IBANs, card and phone numbers;
/// "AllText" covers every recognized word. Pixels are overwritten, so nothing can be recovered.
/// </summary>
internal static class Redactor
{
    static readonly Regex[] SensitivePatterns =
    {
        // E-mail (OCR sometimes puts spaces around the @)
        new(@"[A-Z0-9._%+\-]+\s?@\s?[A-Z0-9\-]+(?:\.[A-Z0-9\-]+)*\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // IBAN (DE89 3704 0044 0532 0130 00)
        new(@"\b[A-Z]{2}\s?\d{2}(?:\s?[A-Z0-9]){11,30}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Card and other long numbers (13–19 digits with optional separators)
        new(@"\b(?:\d[\s\-]?){12,18}\d\b", RegexOptions.Compiled),
        // International phone numbers
        new(@"(?<![\w+])(?:\+|00)\d{1,3}[\s\-/]?(?:\(0\)\s?)?\d{2,5}[\s\-/]?\d{3,}(?:[\s\-]?\d{2,})*", RegexOptions.Compiled),
        // National phone numbers (leading 0)
        new(@"(?<!\w)0\d{2,5}[\s\-/]?\d{3,}(?:[\s\-]?\d{2,})*", RegexOptions.Compiled)
    };

    public sealed record Result(BitmapSource Image, int Boxes);

    public static async Task<Result> RedactAsync(BitmapSource image, RedactionMode mode, CancellationToken ct)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new UserFacingException(
                "Keine OCR-Sprache installiert. Windows-Einstellungen → Zeit und Sprache → Sprache → Sprachpaket mit „Optische Zeichenerkennung“ hinzufügen.");

        // OCR works on Gray8; downscale only if the image exceeds the engine limit.
        double scale = 1.0;
        int longEdge = Math.Max(image.PixelWidth, image.PixelHeight);
        if (longEdge > OcrEngine.MaxImageDimension) scale = (double)OcrEngine.MaxImageDimension / longEdge;

        var gray = ImageCodec.ToGray8(ImageCodec.Scale(image, scale));
        int w = gray.PixelWidth, h = gray.PixelHeight;
        var pixels = new byte[w * h];
        gray.CopyPixels(pixels, w, 0);

        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Gray8, w, h, BitmapAlphaMode.Ignore);

        ct.ThrowIfCancellationRequested();
        OcrResult ocr = await engine.RecognizeAsync(softwareBitmap);

        var boxes = new List<Windows.Foundation.Rect>();
        foreach (var line in ocr.Lines)
        {
            if (mode == RedactionMode.AllText) boxes.AddRange(line.Words.Select(word => word.BoundingRect));
            else boxes.AddRange(FindSensitive(line));
        }

        if (boxes.Count == 0) return new Result(image, 0);

        var buffer = PixelBuffer.From(image);
        foreach (var r in boxes)
        {
            double pad = Math.Max(2, r.Height * 0.15);
            buffer.FillRect(
                (int)Math.Floor((r.X - pad) / scale),
                (int)Math.Floor((r.Y - pad) / scale),
                (int)Math.Ceiling((r.Width + 2 * pad) / scale),
                (int)Math.Ceiling((r.Height + 2 * pad) / scale));
        }
        return new Result(buffer.ToBitmap(), boxes.Count);
    }

    /// <summary>Joins the words of a line, runs the patterns and maps matches back to word boxes.</summary>
    static IEnumerable<Windows.Foundation.Rect> FindSensitive(OcrLine line)
    {
        var text = new StringBuilder();
        var spans = new List<(int Start, int End, Windows.Foundation.Rect Box)>();
        foreach (var word in line.Words)
        {
            if (text.Length > 0) text.Append(' ');
            int start = text.Length;
            text.Append(word.Text);
            spans.Add((start, text.Length, word.BoundingRect));
        }

        var s = text.ToString();
        var hit = new bool[spans.Count];
        foreach (var pattern in SensitivePatterns)
        {
            foreach (Match m in pattern.Matches(s))
            {
                int mStart = m.Index, mEnd = m.Index + m.Length;
                for (int i = 0; i < spans.Count; i++)
                    if (spans[i].Start < mEnd && spans[i].End > mStart) hit[i] = true;
            }
        }

        for (int i = 0; i < spans.Count; i++)
            if (hit[i]) yield return spans[i].Box;
    }
}

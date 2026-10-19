// Liquid glass for the citrus segments.
//
// Port of the WebGL fragment shader from liquid-glass-js
// (https://github.com/matsdenninger-ui/liquid-glass-js, fork of dashersw/liquid-glass-js),
// MIT License, Copyright (c) 2025 Armagan Amcalar – see THIRD-PARTY-NOTICES.md.
//
// The shader samples a snapshot of the page behind the element, pushes the sampling point along the
// shape's outward normal (strong right at the rim, fading inwards), adds a small ripple, blurs and tints
// the result and masks it to the shape. Here the same math runs once on the CPU over a screenshot of the
// area behind the fruit; the shape is a circle (core) plus annular sectors (segments) instead of a
// rounded rectangle.

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace Zytrus.UI;

internal static class LiquidGlass
{
    // Defaults of liquid-glass-js (controls.js)
    const double EdgeIntensity = 0.01;
    const double RimIntensity = 0.05;
    const double EdgeDistance = 0.15;
    const double RimDistance = 0.8;
    const double RippleEffect = 0.1;

    /// <summary>Raw BGRA pixels of what is behind the fruit.</summary>
    public sealed record Backdrop(int Width, int Height, byte[] Bgra);

    /// <summary>Fruit geometry in pixels of the backdrop.</summary>
    public sealed record Shape(double CenterX, double CenterY, double CoreRadius, double SegmentInner, double SegmentOuter,
        int Segments, double StartAngle, double MembraneHalfWidth);

    /// <param name="Blur">liquid-glass-js "blurRadius" (0–12, default 5)</param>
    /// <param name="Refraction">multiplier on the rim/edge refraction (1 = shader defaults)</param>
    /// <param name="TintOpacity">mix towards the fruit tint (0–0.8)</param>
    public sealed record Parameters(double Blur, double Refraction, double TintOpacity, Color Tint);

    /// <summary>The rendered glass plus the average brightness per segment (to pick dark or light labels).</summary>
    public sealed record Result(BitmapSource Image, double[] SegmentLuma, double CoreLuma);

    // ---- capture -------------------------------------------------------------------------------

    /// <summary>Screenshot of a physical-pixel rectangle. Call before the fruit window is visible.</summary>
    public static Backdrop? CaptureScreen(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        try
        {
            using var bitmap = new System.Drawing.Bitmap(width, height, DrawingPixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));

            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[width * height * 4];
                for (int row = 0; row < height; row++)
                    Marshal.Copy(data.Scan0 + row * data.Stride, bytes, row * width * 4, width * 4);
                return new Backdrop(width, height, bytes);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Screen capture for glass failed: " + ex.Message);
            return null;
        }
    }

    public static Backdrop FromBitmap(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(bytes, bgra.PixelWidth * 4, 0);
        return new Backdrop(bgra.PixelWidth, bgra.PixelHeight, bytes);
    }

    /// <summary>A colorful stand-in desktop for the settings preview, so the glass has something to refract.</summary>
    public static BitmapSource CreateSampleBackdrop(int size, double pixelsPerDip)
    {
        var visual = new DrawingVisual();
        double s = size;
        using (var dc = visual.RenderOpen())
        {
            var bg = new LinearGradientBrush(Color.FromRgb(0x1E, 0x3C, 0x72), Color.FromRgb(0x6A, 0x3D, 0x9A), 45);
            dc.DrawRectangle(bg, null, new System.Windows.Rect(0, 0, s, s));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3D)), null, new System.Windows.Point(s * 0.22, s * 0.28), s * 0.20, s * 0.20);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0xB6)), null, new System.Windows.Point(s * 0.80, s * 0.70), s * 0.24, s * 0.24);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xFF, 0x5D, 0x8F)), null, new System.Windows.Point(s * 0.70, s * 0.18), s * 0.12, s * 0.12);

            var face = new Typeface("Segoe UI");
            var white = Brushes.White;
            string[] lines = { "Rechnung_Oktober.pdf", "Urlaub_2026.jpg", "Präsentation.pptx", "Vertrag_final.docx", "Video_Hochzeit.mp4" };
            for (int i = 0; i < lines.Length; i++)
            {
                var text = new FormattedText(lines[i], System.Globalization.CultureInfo.CurrentUICulture,
                    System.Windows.FlowDirection.LeftToRight, face, s * 0.055, white, pixelsPerDip);
                dc.DrawText(text, new System.Windows.Point(s * 0.12, s * (0.18 + i * 0.15)));
            }
            var stripe = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
            for (int i = 0; i < 6; i++)
                dc.DrawRectangle(stripe, null, new System.Windows.Rect(0, s * (0.08 + i * 0.16), s, s * 0.012));
        }

        int px = (int)Math.Ceiling(size * pixelsPerDip);
        var target = new RenderTargetBitmap(px, px, 96 * pixelsPerDip, 96 * pixelsPerDip, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    // ---- render --------------------------------------------------------------------------------

    public static Result Render(Backdrop source, Shape shape, Parameters p)
    {
        int w = source.Width, h = source.Height;

        // Gaussian blur of the backdrop. In the shader, 13×13 taps spaced sigma texels apart with
        // sigma = blurRadius / 2 give an effective standard deviation of about sigma² pixels.
        double sigma = p.Blur / 2.0;
        double std = Math.Min(40, sigma * sigma);
        var blurred = std < 0.5 ? (byte[])source.Bgra.Clone() : BoxBlur3(source.Bgra, w, h, std);

        var output = new byte[w * h * 4];
        int segments = Math.Max(1, shape.Segments);
        double step = 360.0 / segments;
        double minRes = Math.Min(w, h);
        double tintR = p.Tint.R, tintG = p.Tint.G, tintB = p.Tint.B;
        double top = shape.CenterY - shape.SegmentOuter, height = Math.Max(1, shape.SegmentOuter * 2);

        var lumaSum = new double[segments + 1];   // last slot = core
        var lumaCount = new double[segments + 1];
        var gate = new object();

        Parallel.For(0, h, y =>
        {
            var localSum = new double[segments + 1];
            var localCount = new double[segments + 1];
            for (int x = 0; x < w; x++)
            {
                double px = x + 0.5, py = y + 0.5;
                double dx = px - shape.CenterX, dy = py - shape.CenterY;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r > shape.SegmentOuter + 1.5) continue;

                // Signed distance (negative inside) and outward normal of the piece this pixel belongs to.
                double sd, nx, ny;
                int piece;
                double ux = r > 1e-6 ? dx / r : 0, uy = r > 1e-6 ? dy / r : -1;

                if (r < (shape.CoreRadius + shape.SegmentInner) / 2)
                {
                    piece = segments; // core
                    sd = r - shape.CoreRadius;
                    nx = ux;
                    ny = uy;
                }
                else
                {
                    double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
                    double rel = ((angle - shape.StartAngle) % 360 + 360) % 360;
                    piece = Math.Min(segments - 1, (int)(rel / step));
                    double local = rel - piece * step;
                    double a0 = (shape.StartAngle + piece * step) * Math.PI / 180;
                    double a1 = a0 + step * Math.PI / 180;

                    double dOuter = shape.SegmentOuter - r;
                    double dInner = r - shape.SegmentInner;
                    double dEdge0 = r * Math.Sin(Math.Min(local, 90) * Math.PI / 180);
                    double dEdge1 = r * Math.Sin(Math.Min(step - local, 90) * Math.PI / 180);

                    double d = dOuter;
                    nx = ux; ny = uy;
                    if (dInner < d) { d = dInner; nx = -ux; ny = -uy; }
                    if (dEdge0 < d) { d = dEdge0; nx = Math.Sin(a0); ny = -Math.Cos(a0); }
                    if (dEdge1 < d) { d = dEdge1; nx = -Math.Sin(a1); ny = Math.Cos(a1); }
                    sd = -(d - shape.MembraneHalfWidth);
                }

                // Shape mask with a 1 px soft edge (smoothstep(-1, 1, distance) in the shader)
                double mask = 1 - SmoothStep(-1, 1, sd);
                if (mask <= 0) continue;

                // Refraction: rim + edge terms along the normal, ripple along the tangent.
                double inside = Math.Max(0, -sd);
                double rim = Math.Exp(-inside * RimDistance);
                double total = (EdgeIntensity * Math.Exp(-inside * EdgeDistance) + RimIntensity * rim) * p.Refraction;
                double ripple = Math.Sin(inside / minRes * 25) * RippleEffect * rim * p.Refraction;
                double sx = px + nx * total * w + (-ny) * ripple * w;
                double sy = py + ny * total * h + nx * ripple * h;

                Sample(blurred, w, h, sx - 0.5, sy - 0.5, out double b, out double g, out double rr);

                // Tint towards the fruit color, lighter at the top like the shader's vertical gradient.
                double t = Math.Clamp((py - top) / height, 0, 1);
                double shade = 1.0 - 0.15 * t;
                rr += (tintR * shade - rr) * p.TintOpacity;
                g += (tintG * shade - g) * p.TintOpacity;
                b += (tintB * shade - b) * p.TintOpacity;

                int o = (y * w + x) * 4;
                output[o] = ToByte(b);
                output[o + 1] = ToByte(g);
                output[o + 2] = ToByte(rr);
                output[o + 3] = ToByte(255 * mask);

                if (mask > 0.99)
                {
                    localSum[piece] += (0.2126 * rr + 0.7152 * g + 0.0722 * b) / 255.0;
                    localCount[piece]++;
                }
            }
            lock (gate)
            {
                for (int i = 0; i <= segments; i++)
                {
                    lumaSum[i] += localSum[i];
                    lumaCount[i] += localCount[i];
                }
            }
        });

        var luma = new double[segments];
        for (int i = 0; i < segments; i++) luma[i] = lumaCount[i] > 0 ? lumaSum[i] / lumaCount[i] : 0.8;
        double coreLuma = lumaCount[segments] > 0 ? lumaSum[segments] / lumaCount[segments] : 0.8;

        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, output, w * 4);
        image.Freeze();
        return new Result(image, luma, coreLuma);
    }

    static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>Bilinear sample with clamping at the borders.</summary>
    static void Sample(byte[] img, int w, int h, double x, double y, out double b, out double g, out double r)
    {
        x = Math.Clamp(x, 0, w - 1.001);
        y = Math.Clamp(y, 0, h - 1.001);
        int x0 = (int)x, y0 = (int)y;
        double fx = x - x0, fy = y - y0;
        int i00 = (y0 * w + x0) * 4, i10 = i00 + 4, i01 = i00 + w * 4, i11 = i01 + 4;
        double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
        b = img[i00] * w00 + img[i10] * w10 + img[i01] * w01 + img[i11] * w11;
        g = img[i00 + 1] * w00 + img[i10 + 1] * w10 + img[i01 + 1] * w01 + img[i11 + 1] * w11;
        r = img[i00 + 2] * w00 + img[i10 + 2] * w10 + img[i01 + 2] * w01 + img[i11 + 2] * w11;
    }

    /// <summary>Three box blurs ≈ Gaussian with the given standard deviation (running sums, O(1) per pixel).</summary>
    static byte[] BoxBlur3(byte[] src, int w, int h, double std)
    {
        int radius = Math.Max(1, (int)Math.Round((Math.Sqrt(4 * std * std + 1) - 1) / 2));
        var a = (byte[])src.Clone();
        var b = new byte[src.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxHorizontal(a, b, w, h, radius);
            BoxVertical(b, a, w, h, radius);
        }
        return a;
    }

    static void BoxHorizontal(byte[] src, byte[] dst, int w, int h, int r)
    {
        double norm = 1.0 / (2 * r + 1);
        Parallel.For(0, h, y =>
        {
            int row = y * w * 4;
            for (int c = 0; c < 3; c++)
            {
                double sum = 0;
                for (int k = -r; k <= r; k++) sum += src[row + Math.Clamp(k, 0, w - 1) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x * 4 + c] = (byte)(sum * norm + 0.5);
                    int add = Math.Min(w - 1, x + r + 1), remove = Math.Max(0, x - r);
                    sum += src[row + add * 4 + c] - src[row + remove * 4 + c];
                }
            }
            for (int x = 0; x < w; x++) dst[row + x * 4 + 3] = 255;
        });
    }

    static void BoxVertical(byte[] src, byte[] dst, int w, int h, int r)
    {
        double norm = 1.0 / (2 * r + 1);
        int stride = w * 4;
        Parallel.For(0, w, x =>
        {
            int col = x * 4;
            for (int c = 0; c < 3; c++)
            {
                double sum = 0;
                for (int k = -r; k <= r; k++) sum += src[Math.Clamp(k, 0, h - 1) * stride + col + c];
                for (int y = 0; y < h; y++)
                {
                    dst[y * stride + col + c] = (byte)(sum * norm + 0.5);
                    int add = Math.Min(h - 1, y + r + 1), remove = Math.Max(0, y - r);
                    sum += src[add * stride + col + c] - src[remove * stride + col + c];
                }
            }
            for (int y = 0; y < h; y++) dst[y * stride + col + 3] = 255;
        });
    }
}

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Zytrus.Processing;

/// <summary>
/// Writes a minimal PDF where every page is one JPEG (DCTDecode). Used for rasterized/redacted PDFs
/// and "images → PDF". Rasterizing is what makes redaction real: no text layer survives underneath.
/// </summary>
internal sealed class MiniPdfWriter
{
    sealed record Page(byte[] Jpeg, int PixelWidth, int PixelHeight, bool Gray, double PointWidth, double PointHeight);

    readonly List<Page> _pages = new();

    public int PageCount => _pages.Count;

    public void AddJpegPage(byte[] jpeg, int pixelWidth, int pixelHeight, double pointWidth, double pointHeight, bool gray = false) =>
        _pages.Add(new Page(jpeg, pixelWidth, pixelHeight, gray, pointWidth, pointHeight));

    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var offsets = new List<long>();

        void Ascii(string s)
        {
            var bytes = Encoding.ASCII.GetBytes(s);
            fs.Write(bytes, 0, bytes.Length);
        }

        void BeginObject(int number)
        {
            while (offsets.Count < number) offsets.Add(0);
            offsets[number - 1] = fs.Position;
            Ascii($"{number} 0 obj\n");
        }

        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        Ascii("%PDF-1.4\n");
        fs.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);

        // 1 = catalog, 2 = pages, then per page: page, content, image.
        BeginObject(1);
        Ascii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var kids = new StringBuilder();
        for (int i = 0; i < _pages.Count; i++) kids.Append($"{3 + i * 3} 0 R ");
        BeginObject(2);
        Ascii($"<< /Type /Pages /Kids [{kids}] /Count {_pages.Count} >>\nendobj\n");

        for (int i = 0; i < _pages.Count; i++)
        {
            var p = _pages[i];
            int pageObj = 3 + i * 3, contentObj = pageObj + 1, imageObj = pageObj + 2;

            BeginObject(pageObj);
            Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(p.PointWidth)} {N(p.PointHeight)}] " +
                  $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

            var content = Encoding.ASCII.GetBytes($"q {N(p.PointWidth)} 0 0 {N(p.PointHeight)} 0 0 cm /Im0 Do Q");
            BeginObject(contentObj);
            Ascii($"<< /Length {content.Length} >>\nstream\n");
            fs.Write(content, 0, content.Length);
            Ascii("\nendstream\nendobj\n");

            BeginObject(imageObj);
            Ascii($"<< /Type /XObject /Subtype /Image /Width {p.PixelWidth} /Height {p.PixelHeight} " +
                  $"/ColorSpace {(p.Gray ? "/DeviceGray" : "/DeviceRGB")} /BitsPerComponent 8 /Filter /DCTDecode /Length {p.Jpeg.Length} >>\nstream\n");
            fs.Write(p.Jpeg, 0, p.Jpeg.Length);
            Ascii("\nendstream\nendobj\n");
        }

        long xref = fs.Position;
        Ascii($"xref\n0 {offsets.Count + 1}\n");
        Ascii("0000000000 65535 f \n");
        foreach (var offset in offsets) Ascii($"{offset:D10} 00000 n \n");
        Ascii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}

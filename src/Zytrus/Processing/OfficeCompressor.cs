using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Zytrus.Core;

namespace Zytrus.Processing;

/// <summary>
/// .docx/.pptx/.xlsx are zip packages; most of their weight is pictures in */media/.
/// Pictures are downscaled/re-encoded in place, keeping file names and formats so all references stay valid.
/// </summary>
internal static class OfficeCompressor
{
    static readonly (int MaxEdge, int Quality)[] Ladder = { (2048, 82), (1600, 76), (1280, 70), (1024, 64), (800, 58) };

    public static int MaxLevel => Ladder.Length - 1;

    public static ProducedFile AtLevel(InputItem item, int level, string workDir, bool stripMetadata)
    {
        var (maxEdge, quality) = Ladder[Math.Clamp(level, 0, MaxLevel)];
        var output = WorkFiles.Path(workDir, item, $"office{level}", item.Extension);
        File.Copy(item.SourcePath, output, true);

        using (var zip = ZipFile.Open(output, ZipArchiveMode.Update))
        {
            foreach (var entry in zip.Entries.ToList())
            {
                var name = entry.FullName;
                if (!name.Contains("/media/", StringComparison.OrdinalIgnoreCase) || entry.Length < 150_000) continue;
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext is not (".jpg" or ".jpeg" or ".png")) continue;

                byte[] original;
                using (var s = entry.Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    original = ms.ToArray();
                }

                byte[]? smaller = Recompress(original, ext, maxEdge, quality);
                if (smaller == null || smaller.Length >= original.Length * 0.9) continue;

                entry.Delete();
                var replacement = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var target = replacement.Open();
                target.Write(smaller, 0, smaller.Length);
            }

            if (stripMetadata) StripDocumentProperties(zip);
        }

        return new ProducedFile(item, output, item.RelativePath, new FileInfo(output).Length, true);
    }

    static byte[]? Recompress(byte[] data, string ext, int maxEdge, int quality)
    {
        try
        {
            using var ms = new MemoryStream(data);
            var decoded = ImageCodec.Load(ms);
            var scaled = ImageCodec.Downscale(decoded.Bitmap, maxEdge);
            return ext == ".png" ? ImageCodec.EncodePng(scaled) : ImageCodec.EncodeJpeg(scaled, quality);
        }
        catch (Exception ex)
        {
            Log.Write("Office picture skipped: " + ex.Message);
            return null;
        }
    }

    /// <summary>Removes author, "last modified by", company and manager from docProps.</summary>
    public static void StripDocumentProperties(ZipArchive zip)
    {
        RewriteXml(zip, "docProps/core.xml", doc =>
        {
            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName is "creator" or "lastModifiedBy" or "keywords").ToList())
                el.Remove();
        });
        RewriteXml(zip, "docProps/app.xml", doc =>
        {
            foreach (var el in doc.Descendants().Where(e => e.Name.LocalName is "Company" or "Manager").ToList())
                el.Remove();
        });
    }

    static void RewriteXml(ZipArchive zip, string entryName, Action<XDocument> edit)
    {
        var entry = zip.GetEntry(entryName);
        if (entry == null) return;
        XDocument doc;
        using (var s = entry.Open()) doc = XDocument.Load(s);
        edit(doc);
        entry.Delete();
        var replacement = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var target = replacement.Open();
        doc.Save(target);
    }

    public static void StripMetadataOnly(string source, string destination)
    {
        File.Copy(source, destination, true);
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Update);
        StripDocumentProperties(zip);
    }
}

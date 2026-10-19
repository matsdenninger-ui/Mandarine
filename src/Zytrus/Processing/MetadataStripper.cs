using System;
using System.IO;
using System.Text;

namespace Zytrus.Processing;

/// <summary>
/// Lossless metadata removal: rewrites the container without touching compressed pixel data.
/// JPEG: drops EXIF/XMP (APP1), IPTC/Photoshop (APP13), comments, vendor APPn and appended
/// multi-picture data (MPF, which can carry its own EXIF incl. GPS). Keeps JFIF, ICC profile and Adobe APP14.
/// Orientation is preserved through a minimal EXIF block so the photo is not displayed rotated.
/// PNG: drops eXIf, tEXt, zTXt, iTXt (XMP) and tIME chunks.
/// </summary>
internal static class MetadataStripper
{
    public static byte[]? TryStrip(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var data = File.ReadAllBytes(path);
        try
        {
            return ext switch
            {
                ".jpg" or ".jpeg" or ".jpe" or ".jfif" => StripJpeg(data),
                ".png" => StripPng(data),
                _ => null
            };
        }
        catch (Exception ex)
        {
            Log.Error("Lossless strip failed for " + path, ex);
            return null;
        }
    }

    // ---- JPEG ----------------------------------------------------------------------------------

    static byte[]? StripJpeg(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;

        using var header = new MemoryStream();
        int pos = 2;
        ushort orientation = 1;
        byte[]? app0 = null;

        // 1) Header segments up to the first SOS.
        while (true)
        {
            if (pos + 4 > d.Length) return null;
            if (d[pos] != 0xFF) return null;
            byte marker = d[pos + 1];
            if (marker == 0xFF) { pos++; continue; }          // fill byte
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) { pos += 2; continue; }
            if (marker == 0xDA) break;                          // SOS: image data follows

            int length = (d[pos + 2] << 8) | d[pos + 3];
            if (length < 2 || pos + 2 + length > d.Length) return null;
            var segment = new ReadOnlySpan<byte>(d, pos, 2 + length);

            if (marker == 0xE1 && IsExif(d, pos + 4, length - 2))
                orientation = ReadExifOrientation(d, pos + 4 + 6, length - 2 - 6);

            if (marker == 0xE0 && app0 == null) app0 = segment.ToArray();
            else if (Keep(marker, d, pos + 4, length - 2)) header.Write(segment);

            pos += 2 + length;
        }

        // 2) Scan data, possibly several scans (progressive), until EOI. Everything after EOI is dropped.
        int scanStart = pos;
        int end = FindEndOfImage(d, pos);
        if (end < 0) return null;

        using var output = new MemoryStream(d.Length);
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);
        if (app0 != null) output.Write(app0);
        if (orientation is > 1 and <= 8) output.Write(MinimalExif(orientation));
        header.Position = 0;
        header.CopyTo(output);
        output.Write(d, scanStart, end - scanStart);
        return output.ToArray();
    }

    static bool Keep(byte marker, byte[] d, int payload, int payloadLength)
    {
        if (marker == 0xFE) return false;                          // COM
        if (marker == 0xE2) return StartsWith(d, payload, payloadLength, "ICC_PROFILE"); // drop MPF, keep ICC
        if (marker == 0xEE) return true;                           // Adobe (color transform)
        if (marker >= 0xE0 && marker <= 0xEF) return false;        // other APPn incl. EXIF/XMP/IPTC
        return true;                                               // DQT, DHT, SOF, DRI, ...
    }

    /// <summary>Returns the index just after EOI, walking segments between scans.</summary>
    static int FindEndOfImage(byte[] d, int pos)
    {
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF) return -1;
            byte marker = d[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker == 0xD9) return pos + 2;

            int length = (d[pos + 2] << 8) | d[pos + 3];
            if (length < 2 || pos + 2 + length > d.Length) return -1;
            pos += 2 + length;
            if (marker != 0xDA) continue;

            // Entropy-coded data: 0xFF is stuffed as FF00, restart markers are FFD0–FFD7.
            while (pos + 1 < d.Length)
            {
                if (d[pos] == 0xFF)
                {
                    byte next = d[pos + 1];
                    if (next == 0x00 || (next >= 0xD0 && next <= 0xD7) || next == 0xFF) { pos += next == 0xFF ? 1 : 2; continue; }
                    break; // real marker
                }
                pos++;
            }
        }
        // Truncated file without EOI: keep everything we have.
        return d.Length;
    }

    static bool IsExif(byte[] d, int offset, int length) => StartsWith(d, offset, length, "Exif\0\0");

    static bool StartsWith(byte[] d, int offset, int length, string ascii)
    {
        if (length < ascii.Length || offset + ascii.Length > d.Length) return false;
        for (int i = 0; i < ascii.Length; i++)
            if (d[offset + i] != (byte)ascii[i]) return false;
        return true;
    }

    /// <summary>Reads tag 0x0112 from IFD0 of a TIFF structure.</summary>
    static ushort ReadExifOrientation(byte[] d, int tiff, int length)
    {
        try
        {
            if (length < 8) return 1;
            bool little = d[tiff] == 0x49 && d[tiff + 1] == 0x49;
            ushort U16(int o) => little ? (ushort)(d[o] | (d[o + 1] << 8)) : (ushort)((d[o] << 8) | d[o + 1]);
            uint U32(int o) => little
                ? (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24))
                : (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

            int ifd = tiff + (int)U32(tiff + 4);
            int count = U16(ifd);
            for (int i = 0; i < count; i++)
            {
                int entry = ifd + 2 + i * 12;
                if (entry + 12 > tiff + length) break;
                if (U16(entry) == 0x0112) return U16(entry + 8);
            }
        }
        catch (IndexOutOfRangeException) { }
        return 1;
    }

    /// <summary>APP1 with a single IFD0 entry: Orientation. 34 bytes.</summary>
    static byte[] MinimalExif(ushort orientation) => new byte[]
    {
        0xFF, 0xE1, 0x00, 0x22,                         // APP1, length 34
        0x45, 0x78, 0x69, 0x66, 0x00, 0x00,             // "Exif\0\0"
        0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // big-endian TIFF header, IFD0 at 8
        0x00, 0x01,                                     // 1 entry
        0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, // tag 0x0112, SHORT, count 1
        0x00, (byte)orientation, 0x00, 0x00,            // value
        0x00, 0x00, 0x00, 0x00                          // no next IFD
    };

    // ---- PNG -----------------------------------------------------------------------------------

    static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    static byte[]? StripPng(byte[] d)
    {
        if (d.Length < 8 || !d.AsSpan(0, 8).SequenceEqual(PngSignature)) return null;
        using var output = new MemoryStream(d.Length);
        output.Write(PngSignature);
        int pos = 8;
        while (pos + 12 <= d.Length)
        {
            int length = (d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3];
            if (length < 0 || pos + 12 + length > d.Length) return null;
            string type = Encoding.ASCII.GetString(d, pos + 4, 4);
            bool drop = type is "eXIf" or "tEXt" or "zTXt" or "iTXt" or "tIME";
            if (!drop) output.Write(d, pos, 12 + length);
            pos += 12 + length;
            if (type == "IEND") break;
        }
        return output.ToArray();
    }
}

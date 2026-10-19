using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Mandarine.Core;

namespace Mandarine.Processing;

internal static class ZipBuilder
{
    /// <summary>Creates a zip; media that is already compressed is stored, everything else gets the smallest setting.</summary>
    public static void Create(string zipPath, IEnumerable<(string SourceFile, string EntryName)> entries)
    {
        using var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var used = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var (source, entryName) in entries)
        {
            var name = UniqueEntryName(entryName.Replace('\\', '/').TrimStart('/'), used);
            var level = FileKinds.IsAlreadyCompressed(source) ? CompressionLevel.NoCompression : CompressionLevel.SmallestSize;
            zip.CreateEntryFromFile(source, name, level);
        }
    }

    /// <summary>Rough upper bound of what zipping adds (headers, central directory).</summary>
    public static long Overhead(int entries) => 2048 + entries * 300L;

    static string UniqueEntryName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var dir = Path.GetDirectoryName(name)?.Replace('\\', '/');
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            var candidate = string.IsNullOrEmpty(dir) ? $"{stem} ({i}){ext}" : $"{dir}/{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }
}

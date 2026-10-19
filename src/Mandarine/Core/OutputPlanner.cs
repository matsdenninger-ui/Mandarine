using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mandarine.Processing;

namespace Mandarine.Core;

/// <summary>Decides where results go and what they're called, then moves them there.</summary>
public static class OutputPlanner
{
    public static string ResolveDirectory(string preferred)
    {
        var s = SettingsStore.Current;
        if (s.Output == OutputLocation.NextToOriginal && IsGoodLocation(preferred)) return preferred;
        Directory.CreateDirectory(s.OutputFolder);
        return s.OutputFolder;
    }

    /// <summary>Not writable or a temp folder (e.g. a file opened from inside a zip) → use the fallback folder.</summary>
    static bool IsGoodLocation(string dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        if (Path.GetFullPath(dir).StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var probe = Path.Combine(dir, $".mandarine_{Guid.NewGuid():N}.tmp");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string UniquePath(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            path = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
    }

    /// <summary>"IMG_1234.HEIC" + "_email" + ".jpg" → "IMG_1234_email.jpg".</summary>
    public static string NameWithSuffix(string relativeOrName, string suffix)
    {
        var stem = Path.GetFileNameWithoutExtension(relativeOrName);
        return stem + suffix + Path.GetExtension(relativeOrName).ToLowerInvariant();
    }

    public static string ZipName(InputSet set, string suffix)
    {
        if (set.ContainerName != null && set.Items.All(i => i.RelativePath.StartsWith(set.ContainerName, StringComparison.OrdinalIgnoreCase)))
            return set.ContainerName + suffix + ".zip";
        var folder = Path.GetFileName(set.BaseDirectory.TrimEnd('\\'));
        return (string.IsNullOrEmpty(folder) ? "Anhänge" : folder) + suffix + ".zip";
    }

    /// <summary>
    /// Writes the produced files to their final place. Bundles into a zip when needed.
    /// Unchanged single files are handed back as the original path (nothing to copy).
    /// </summary>
    public static List<string> Deliver(InputSet set, IReadOnlyList<ProducedFile> files, string suffix, bool forceZip)
    {
        var s = SettingsStore.Current;
        var dir = ResolveDirectory(set.BaseDirectory);
        bool zip = forceZip || set.HasContainer || files.Count > s.ZipWhenMoreThan;

        if (zip)
        {
            var zipPath = UniquePath(dir, ZipName(set, suffix));
            ZipBuilder.Create(zipPath, files.Select(f => (f.Path, f.RelativePath)));
            return new List<string> { zipPath };
        }

        var outputs = new List<string>();
        foreach (var f in files)
        {
            if (!f.Changed)
            {
                outputs.Add(f.Input.SourcePath);
                continue;
            }
            var target = UniquePath(dir, NameWithSuffix(f.RelativePath, suffix));
            File.Copy(f.Path, target);
            outputs.Add(target);
        }
        return outputs;
    }
}

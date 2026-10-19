using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Zytrus.Core;

/// <summary>The dropped things, flattened into files with their relative paths.</summary>
public sealed class InputSet
{
    public List<InputItem> Items { get; } = new();

    /// <summary>True if a folder or zip was dropped – the result must be a zip to keep the structure.</summary>
    public bool HasContainer { get; set; }

    /// <summary>Folder of the first dropped item: default location for the output.</summary>
    public string BaseDirectory { get; set; } = "";

    /// <summary>Name for a zip output, e.g. the dropped folder's name.</summary>
    public string? ContainerName { get; set; }

    public long TotalSize => Items.Sum(i => i.Size);
}

public static class InputExpander
{
    static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase) { "thumbs.db", "desktop.ini", ".ds_store" };

    /// <param name="expandZips">Unpack .zip files so their content can be compressed (email mode).</param>
    public static InputSet Expand(IEnumerable<string> paths, string workDir, bool expandZips)
    {
        var set = new InputSet();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = paths.Select(p => p.Trim().Trim('"')).Where(p => p.Length > 0).ToList();
        set.BaseDirectory = list.Count > 0 ? (Path.GetDirectoryName(Path.GetFullPath(list[0])) ?? "") : "";

        foreach (var raw in list)
        {
            var path = Path.GetFullPath(raw);
            if (Directory.Exists(path))
            {
                var root = new DirectoryInfo(path);
                set.HasContainer = true;
                set.ContainerName ??= root.Name;
                foreach (var file in root.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System }))
                {
                    if (Junk.Contains(file.Name) || !seen.Add(file.FullName)) continue;
                    var relative = Path.Combine(root.Name, Path.GetRelativePath(root.FullName, file.FullName));
                    set.Items.Add(new InputItem(file.FullName, relative, FileKinds.Classify(file.FullName), file.Length));
                }
            }
            else if (File.Exists(path))
            {
                if (!seen.Add(path)) continue;
                var info = new FileInfo(path);
                var kind = FileKinds.Classify(path);

                if (kind == ItemKind.Archive && expandZips && TryExtract(path, workDir, out var folder))
                {
                    set.HasContainer = true;
                    var stem = Path.GetFileNameWithoutExtension(path);
                    set.ContainerName ??= stem;
                    foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        var relative = Path.Combine(stem, Path.GetRelativePath(folder, file.FullName));
                        var innerKind = FileKinds.Classify(file.FullName);
                        if (innerKind == ItemKind.Archive) innerKind = ItemKind.Other; // no nested unpacking
                        set.Items.Add(new InputItem(file.FullName, relative, innerKind, file.Length));
                    }
                }
                else
                {
                    set.Items.Add(new InputItem(path, info.Name, kind, info.Length));
                }
            }
        }

        if (set.Items.Count == 0)
            throw new UserFacingException("Keine Dateien gefunden.");
        return set;
    }

    static bool TryExtract(string zipPath, string workDir, out string folder)
    {
        folder = Path.Combine(workDir, "unzipped_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // ExtractToDirectory refuses entries that would escape the target folder ("zip slip").
            ZipFile.ExtractToDirectory(zipPath, folder);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Zip not unpacked ({ex.Message}), kept as is: {zipPath}");
            try { Directory.Delete(folder, true); } catch { }
            return false;
        }
    }
}

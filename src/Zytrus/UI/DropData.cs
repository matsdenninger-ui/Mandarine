using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using WpfDataObject = System.Windows.IDataObject;

namespace Zytrus.UI;

/// <summary>
/// Reads files from a drop. Besides normal files (CF_HDROP) this handles "virtual" files that only exist inside
/// the dragging app – attachments dragged out of Outlook, images from browsers, files from zip viewers – by
/// saving them to a temp folder first (FileGroupDescriptor + FileContents).
/// </summary>
internal static class DropData
{
    const string DescriptorW = "FileGroupDescriptorW";
    const string DescriptorA = "FileGroupDescriptor";
    const string Contents = "FileContents";

    public static bool HasFiles(WpfDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DescriptorW) || data.GetDataPresent(DescriptorA);
        }
        catch (Exception ex)
        {
            Log.Write("Drop formats unreadable: " + ex.Message);
            return false;
        }
    }

    public static string Describe(WpfDataObject data)
    {
        try { return string.Join(", ", data.GetFormats(false)); }
        catch { return "?"; }
    }

    /// <summary>Real paths of the dropped files; virtual files are copied to a temp folder.</summary>
    public static string[] ExtractFiles(WpfDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                return files;
        }
        catch (Exception ex)
        {
            Log.Error("FileDrop", ex);
        }

        try
        {
            return ExtractVirtualFiles(data);
        }
        catch (Exception ex)
        {
            Log.Error("Virtual files", ex);
            return Array.Empty<string>();
        }
    }

    static string[] ExtractVirtualFiles(WpfDataObject data)
    {
        bool unicode = data.GetDataPresent(DescriptorW);
        if (!unicode && !data.GetDataPresent(DescriptorA)) return Array.Empty<string>();
        if (data is not ComDataObject com) return Array.Empty<string>();

        var descriptors = ReadDescriptors(data.GetData(unicode ? DescriptorW : DescriptorA) as MemoryStream, unicode);
        if (descriptors.Count == 0) return Array.Empty<string>();

        string folder = Path.Combine(Path.GetTempPath(), "Zytrus", "drops", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        short contentsFormat = unchecked((short)DataFormats.GetDataFormat(Contents).Id);
        var roots = new List<string>();

        for (int i = 0; i < descriptors.Count; i++)
        {
            var (name, isDirectory) = descriptors[i];
            var relative = Sanitize(name);
            if (relative.Length == 0) relative = $"Datei_{i + 1}";
            var target = Path.Combine(folder, relative);
            if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) continue;

            if (isDirectory)
            {
                Directory.CreateDirectory(target);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!SaveContents(com, contentsFormat, i, target)) continue;
            }

            // Hand over top-level entries only; folders bring their content along.
            var top = relative.Split('\\')[0];
            var topPath = Path.Combine(folder, top);
            if (!roots.Contains(topPath, StringComparer.OrdinalIgnoreCase)) roots.Add(topPath);
        }

        Log.Write($"Saved {descriptors.Count} virtual file(s) to {folder}");
        return roots.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
    }

    /// <summary>FILEGROUPDESCRIPTOR: count, then FILEDESCRIPTOR structs (592 bytes Unicode / 332 ANSI).</summary>
    static List<(string Name, bool IsDirectory)> ReadDescriptors(MemoryStream? stream, bool unicode)
    {
        var result = new List<(string, bool)>();
        if (stream == null) return result;
        var bytes = stream.ToArray();
        if (bytes.Length < 4) return result;

        int count = BitConverter.ToInt32(bytes, 0);
        int size = unicode ? 592 : 332;
        for (int i = 0; i < count; i++)
        {
            int offset = 4 + i * size;
            if (offset + size > bytes.Length) break;
            uint flags = BitConverter.ToUInt32(bytes, offset);
            uint attributes = BitConverter.ToUInt32(bytes, offset + 36);
            bool hasAttributes = (flags & 0x4) != 0; // FD_ATTRIBUTES
            string name = unicode
                ? Encoding.Unicode.GetString(bytes, offset + 72, 520)
                : Encoding.Default.GetString(bytes, offset + 72, 260);
            int nul = name.IndexOf('\0');
            if (nul >= 0) name = name[..nul];
            result.Add((name, hasAttributes && (attributes & 0x10) != 0));
        }
        return result;
    }

    static bool SaveContents(ComDataObject com, short format, int index, string target)
    {
        var request = new FORMATETC
        {
            cfFormat = format,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            ptd = IntPtr.Zero,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL
        };

        com.GetData(ref request, out STGMEDIUM medium);
        try
        {
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write);
            if (medium.tymed == TYMED.TYMED_ISTREAM)
            {
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                var buffer = new byte[1 << 16];
                IntPtr readPtr = Marshal.AllocCoTaskMem(sizeof(int));
                try
                {
                    while (true)
                    {
                        stream.Read(buffer, buffer.Length, readPtr);
                        int read = Marshal.ReadInt32(readPtr);
                        if (read <= 0) break;
                        output.Write(buffer, 0, read);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(readPtr);
                    Marshal.ReleaseComObject(stream);
                }
                return true;
            }
            if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                IntPtr pointer = GlobalLock(medium.unionmember);
                try
                {
                    int length = (int)GlobalSize(medium.unionmember).ToUInt64();
                    var bytes = new byte[length];
                    Marshal.Copy(pointer, bytes, 0, length);
                    output.Write(bytes, 0, length);
                }
                finally
                {
                    GlobalUnlock(medium.unionmember);
                }
                return true;
            }
            Log.Write($"Virtual file {index}: unsupported medium {medium.tymed}");
            return false;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = name.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => new string(part.Where(ch => !invalid.Contains(ch)).ToArray()).Trim())
            .Where(part => part.Length > 0 && part != "." && part != "..");
        return string.Join('\\', parts);
    }

    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    static extern UIntPtr GlobalSize(IntPtr hMem);
}

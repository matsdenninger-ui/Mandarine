using System;
using System.IO;

namespace Mandarine;

/// <summary>Tiny file logger: %LOCALAPPDATA%\Mandarine\log.txt (trimmed when it grows past 1 MB).</summary>
public static class Log
{
    static readonly object Gate = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mandarine", "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) info.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Error(string context, Exception ex) => Write($"{context}: {ex}");
}

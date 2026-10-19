using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using Zytrus.Core;
using Zytrus.Native;

namespace Zytrus.UI;

/// <summary>Queues jobs, drives the toast and hands results to clipboard / Explorer. Lives on the UI thread.</summary>
internal sealed class JobRunner
{
    readonly Queue<(ProcessAction Action, string[] Paths)> _queue = new();
    readonly ToastWindow _toast = new();
    CancellationTokenSource? _cts;
    List<string> _lastOutputs = new();
    bool _busy;

    public JobRunner()
    {
        _toast.CancelRequested += () => _cts?.Cancel();
        _toast.RevealRequested += () => ShellIntegration.RevealInExplorer(_lastOutputs);
    }

    public ToastWindow Toast => _toast;

    public void Enqueue(ProcessAction action, IReadOnlyList<string> paths)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (existing.Length == 0)
        {
            _toast.ShowError("Nichts gefunden", "Die Dateien existieren nicht (mehr).");
            return;
        }

        _queue.Enqueue((action, existing));
        if (_busy)
        {
            _toast.ShowQueued(_queue.Count);
            return;
        }
        _ = RunQueueAsync();
    }

    async Task RunQueueAsync()
    {
        _busy = true;
        while (_queue.Count > 0)
        {
            var (action, paths) = _queue.Dequeue();
            using var cts = new CancellationTokenSource();
            _cts = cts;
            _toast.ShowWorking(Title(action));
            if (_queue.Count > 0) _toast.ShowQueued(_queue.Count);
            var progress = new Progress<JobProgress>(p => _toast.UpdateProgress(p.Message, p.Fraction));

            try
            {
                var result = await Task.Run(() => ActionRunner.RunAsync(action, paths, progress, cts.Token));
                Deliver(result);
                _toast.ShowDone(result.Title, result.Detail, result.Warnings, offerReveal: !SettingsStore.Current.RevealInExplorer);
            }
            catch (OperationCanceledException)
            {
                _toast.ShowInfo("Abgebrochen", "Es wurde nichts gespeichert.");
            }
            catch (UserFacingException ex)
            {
                Sounds.Error();
                _toast.ShowError("Hat nicht geklappt", ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error("Job " + action, ex);
                Sounds.Error();
                _toast.ShowError("Unerwarteter Fehler", ex.Message + "\nDetails: " + Log.FilePath);
            }
            finally
            {
                _cts = null;
            }
        }
        _busy = false;
    }

    void Deliver(JobResult result)
    {
        _lastOutputs = result.Outputs.ToList();
        var s = SettingsStore.Current;
        if (s.CopyToClipboard && result.Outputs.Count > 0)
        {
            if (ShellIntegration.CopyFilesToClipboard(result.Outputs))
                result.Detail += " · in Zwischenablage (Strg+V)";
        }
        if (s.RevealInExplorer) ShellIntegration.RevealInExplorer(result.Outputs);
        Sounds.Success();
    }

    static string Title(ProcessAction action) => action switch
    {
        ProcessAction.EmailCompress => "Für E-Mail komprimieren",
        ProcessAction.EmailCompressSmall => "Extra klein komprimieren",
        ProcessAction.ConvertJpeg => "In JPEG umwandeln",
        ProcessAction.ConvertPng => "In PNG umwandeln",
        ProcessAction.ImagesToPdf => "PDF wird erstellt",
        ProcessAction.StripMetadata => "Metadaten entfernen",
        ProcessAction.RedactSensitive => "Sensible Daten schwärzen",
        ProcessAction.RedactAllText => "Text schwärzen",
        ProcessAction.ZipOnly => "ZIP erstellen",
        _ => "Zytrus"
    };
}

internal static class Sounds
{
    static SoundPlayer? _player;

    public static void Success()
    {
        if (!SettingsStore.Current.PlaySound) return;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify System Generic.wav");
            if (File.Exists(path))
            {
                _player ??= new SoundPlayer(path);
                _player.Play();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Sound: " + ex.Message);
        }
        SystemSounds.Asterisk.Play();
    }

    public static void Error()
    {
        if (SettingsStore.Current.PlaySound) SystemSounds.Exclamation.Play();
    }
}

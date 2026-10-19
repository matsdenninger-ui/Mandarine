using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mandarine.Processing;

namespace Mandarine.Core;

/// <summary>
/// "Shift + drop": makes everything fit under the mail limit with as little visible loss as possible.
///
///  1. Light pass – lossless metadata strip, exotic formats → JPEG/PNG. Done if it fits.
///  2. Quality ladder for images, PDFs and Office files, one step at a time (gentle → aggressive).
///  3. Videos get whatever budget is left, split by duration, and are transcoded once to hit it.
///     If they can't fit at a watchable bitrate, the other files are squeezed further first.
/// </summary>
internal sealed class EmailPipeline
{
    readonly AppSettings _settings = SettingsStore.Current;
    readonly string _workDir;
    readonly IProgress<JobProgress> _progress;
    readonly CancellationToken _ct;
    readonly JobResult _result = new();
    readonly ParallelOptions _parallel;

    EmailPipeline(string workDir, IProgress<JobProgress> progress, CancellationToken ct)
    {
        _workDir = workDir;
        _progress = progress;
        _ct = ct;
        _parallel = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
    }

    public static Task<JobResult> RunAsync(IReadOnlyList<string> paths, string workDir, IProgress<JobProgress> progress, CancellationToken ct) =>
        new EmailPipeline(workDir, progress, ct).RunAsync(paths);

    async Task<JobResult> RunAsync(IReadOnlyList<string> paths)
    {
        _progress.Report(new JobProgress("Dateien werden analysiert …"));
        var set = InputExpander.Expand(paths, _workDir, expandZips: true);
        var items = set.Items;
        _result.BytesBefore = set.TotalSize;

        bool willZip = set.HasContainer || items.Count > _settings.ZipWhenMoreThan;
        long target = _settings.TargetBytes;
        long budget = target - (willZip ? ZipBuilder.Overhead(items.Count) : 0);

        // ---- 1. light pass -------------------------------------------------------------------
        _progress.Report(new JobProgress("Metadaten werden entfernt …"));
        var best = new ProducedFile[items.Count];
        Parallel.For(0, items.Count, _parallel, i =>
        {
            var item = items[i];
            best[i] = item.Kind == ItemKind.Image ? Safe(item, () => ImageCompressor.Light(item, _workDir, _settings.StripMetadata)) : Unchanged(item);
        });

        if (best.Sum(b => b.Size) <= budget)
            return Finish(set, best, target);

        // ---- 2. ladder + 3. videos -----------------------------------------------------------
        var ladder = Enumerable.Range(0, items.Count).Where(i => items[i].Kind is ItemKind.Image or ItemKind.Pdf or ItemKind.Office).ToList();
        var videos = new List<(int Index, VideoCompressor.VideoInfo Info)>();
        foreach (var i in Enumerable.Range(0, items.Count).Where(i => items[i].Kind == ItemKind.Video))
        {
            try
            {
                videos.Add((i, await VideoCompressor.ProbeAsync(items[i].SourcePath)));
            }
            catch (Exception ex)
            {
                Log.Error("Video probe", ex);
                _result.Warnings.Add($"{items[i].RelativePath}: Video konnte nicht gelesen werden, unverändert übernommen.");
            }
        }
        var videoIndexes = videos.Select(v => v.Index).ToHashSet();
        long fixedSize = Enumerable.Range(0, items.Count)
            .Where(i => !ladder.Contains(i) && !videoIndexes.Contains(i))
            .Sum(i => best[i].Size);

        var frozen = new bool[items.Count]; // items that failed – stop retrying them
        int maxLevel = new[] { ImageCompressor.MaxLevel, PdfCompressor.MaxLevel, OfficeCompressor.MaxLevel }.Max();

        for (int level = 0; level <= maxLevel; level++)
        {
            _ct.ThrowIfCancellationRequested();
            if (ladder.Count > 0)
            {
                _progress.Report(new JobProgress($"Komprimiere Bilder & Dokumente (Stufe {level + 1}) …", (double)level / (maxLevel + 1)));
                await RunLadderLevel(items, ladder, level, best, frozen);
            }

            long ladderSize = ladder.Sum(i => best[i].Size);

            if (videos.Count == 0)
            {
                if (fixedSize + ladderSize <= budget) return Finish(set, best, target);
                if (ladder.All(i => frozen[i] || level >= MaxLevelFor(items[i].Kind))) break; // nothing left to squeeze
                continue;
            }

            long remaining = budget - fixedSize - ladderSize;
            if (videos.Sum(v => best[v.Index].Size) <= remaining) return Finish(set, best, target);

            long minimum = videos.Sum(v => VideoCompressor.MinimumBytes(v.Info));
            if (remaining >= minimum * 1.05)
            {
                await TranscodeVideos(items, videos, remaining, best);
                return Finish(set, best, target);
            }
        }

        if (videos.Count > 0)
        {
            long ladderMin = ladder.Sum(i => best[i].Size);
            long needed = videos.Sum(v => VideoCompressor.MinimumBytes(v.Info)) + fixedSize + ladderMin;
            throw new UserFacingException(
                $"Passt nicht unter {Format.Megabytes(_settings.TargetMegabytes)}: allein die Videos brauchen mindestens " +
                $"{Format.Size(needed)}. Tipp: Videos kürzen, aufteilen oder per Link teilen.");
        }

        _result.Warnings.Add($"Ziel von {Format.Megabytes(_settings.TargetMegabytes)} nicht ganz erreicht – nicht komprimierbare Dateien sind zu groß.");
        return Finish(set, best, target);
    }

    static int MaxLevelFor(ItemKind kind) => kind switch
    {
        ItemKind.Image => ImageCompressor.MaxLevel,
        ItemKind.Pdf => PdfCompressor.MaxLevel,
        ItemKind.Office => OfficeCompressor.MaxLevel,
        _ => 0
    };

    async Task RunLadderLevel(List<InputItem> items, List<int> ladder, int level, ProducedFile[] best, bool[] frozen)
    {
        var images = ladder.Where(i => items[i].Kind == ItemKind.Image && !frozen[i] && level <= ImageCompressor.MaxLevel).ToList();
        Parallel.ForEach(images, _parallel, i =>
        {
            try
            {
                Keep(best, i, ImageCompressor.AtLevel(items[i], level, _workDir));
            }
            catch (Exception ex)
            {
                Freeze(items[i], ex, frozen, i);
            }
        });

        foreach (var i in ladder.Where(i => items[i].Kind is ItemKind.Pdf or ItemKind.Office && !frozen[i]))
        {
            _ct.ThrowIfCancellationRequested();
            var item = items[i];
            if (level > MaxLevelFor(item.Kind)) continue;
            // A PDF that already fits its share doesn't need to be rasterized.
            if (item.Kind == ItemKind.Pdf && PdfCompressor.IsRasterLevel(level) && best[i].Changed && best[i].Size < 1_000_000) continue;
            try
            {
                var produced = item.Kind == ItemKind.Pdf
                    ? await PdfCompressor.AtLevelAsync(item, level, _workDir, _settings.StripMetadata, _ct)
                    : OfficeCompressor.AtLevel(item, level, _workDir, _settings.StripMetadata);
                Keep(best, i, produced);
                if (item.Kind == ItemKind.Pdf && PdfCompressor.IsRasterLevel(level) && ReferenceEquals(best[i], produced))
                    AddWarningOnce($"{item.RelativePath}: als Bild-PDF gespeichert (Text nicht mehr markierbar).");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Freeze(item, ex, frozen, i);
            }
        }
    }

    async Task TranscodeVideos(List<InputItem> items, List<(int Index, VideoCompressor.VideoInfo Info)> videos, long budget, ProducedFile[] best)
    {
        // Share the budget by duration; videos that are already smaller than their share stay untouched.
        var pending = videos.ToList();
        long available = budget;
        bool changed = true;
        while (changed && pending.Count > 0)
        {
            changed = false;
            double totalSeconds = pending.Sum(v => Math.Max(1, v.Info.Duration.TotalSeconds));
            foreach (var v in pending.ToList())
            {
                long share = (long)(available * Math.Max(1, v.Info.Duration.TotalSeconds) / totalSeconds);
                if (best[v.Index].Size <= share)
                {
                    available -= best[v.Index].Size;
                    pending.Remove(v);
                    changed = true;
                }
            }
        }

        double secondsLeft = pending.Sum(v => Math.Max(1, v.Info.Duration.TotalSeconds));
        int done = 0;
        foreach (var (index, info) in pending)
        {
            _ct.ThrowIfCancellationRequested();
            var item = items[index];
            long share = (long)(available * Math.Max(1, info.Duration.TotalSeconds) / secondsLeft);
            var output = WorkFiles.Path(_workDir, item, "video", ".mp4");
            int position = done;
            var progress = new Progress<double>(p => _progress.Report(new JobProgress(
                $"Video {position + 1}/{pending.Count} wird umgewandelt … {p:P0}", (position + p) / pending.Count)));

            long size = await VideoCompressor.TranscodeAsync(item.SourcePath, output, info, share, _settings.PreferHevc, progress, _ct);
            if (size > share * 1.04)
            {
                // Encoder overshot (common for very short or very static clips): one corrective pass.
                long corrected = (long)(share * (double)share / size * 0.97);
                size = await VideoCompressor.TranscodeAsync(item.SourcePath, output, info, corrected, _settings.PreferHevc, progress, _ct);
            }
            best[index] = new ProducedFile(item, output, WorkFiles.WithExtension(item.RelativePath, ".mp4"), size, true);
            done++;
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    static void Keep(ProducedFile[] best, int i, ProducedFile candidate)
    {
        lock (best)
        {
            if (candidate.Size < best[i].Size) best[i] = candidate;
        }
    }

    void Freeze(InputItem item, Exception ex, bool[] frozen, int i)
    {
        frozen[i] = true;
        Log.Error("Processing " + item.SourcePath, ex);
        AddWarningOnce(ex is UserFacingException ? $"{item.RelativePath}: {ex.Message}" : $"{item.RelativePath}: konnte nicht komprimiert werden.");
    }

    ProducedFile Safe(InputItem item, Func<ProducedFile> work)
    {
        try
        {
            return work();
        }
        catch (Exception ex)
        {
            Log.Error("Light pass " + item.SourcePath, ex);
            AddWarningOnce(ex is UserFacingException ? $"{item.RelativePath}: {ex.Message}" : $"{item.RelativePath}: unverändert übernommen.");
            return Unchanged(item);
        }
    }

    void AddWarningOnce(string warning)
    {
        lock (_result) { if (!_result.Warnings.Contains(warning)) _result.Warnings.Add(warning); }
    }

    static ProducedFile Unchanged(InputItem item) => new(item, item.SourcePath, item.RelativePath, item.Size, false);

    JobResult Finish(InputSet set, ProducedFile[] files, long target)
    {
        _ct.ThrowIfCancellationRequested();
        _progress.Report(new JobProgress("Ergebnis wird gespeichert …", 1));
        var outputs = OutputPlanner.Deliver(set, files, _settings.Suffix, forceZip: false);
        _result.Outputs.AddRange(outputs);
        _result.BytesAfter = outputs.Sum(o => new FileInfo(o).Length);

        bool untouched = files.All(f => !f.Changed) && outputs.All(o => set.Items.Any(i => i.SourcePath.Equals(o, StringComparison.OrdinalIgnoreCase)));
        if (untouched)
        {
            _result.Title = "Passt bereits";
            _result.Detail = $"{Format.Size(_result.BytesAfter)} – Original wird verwendet";
        }
        else
        {
            _result.Title = outputs.Count == 1 && outputs[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? $"ZIP bereit · {Format.Files(files.Length)}"
                : $"{Format.Files(outputs.Count)} bereit";
            _result.Detail = $"{Format.Size(_result.BytesBefore)} → {Format.Size(_result.BytesAfter)}";
        }

        if (_result.BytesAfter > target)
            AddWarningOnce($"Größer als {Format.Megabytes(_settings.TargetMegabytes)} – evtl. lehnt der Mailserver ab.");
        return _result;
    }
}

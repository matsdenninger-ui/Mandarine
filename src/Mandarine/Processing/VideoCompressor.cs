using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mandarine.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Mandarine.Processing;

/// <summary>
/// Transcodes with Windows' own Media Foundation stack (MediaTranscoder): hardware-accelerated H.264/AAC in MP4,
/// bitrate derived from the byte budget. Comparable to AVAssetExportSession on macOS. Fully local.
/// </summary>
internal static class VideoCompressor
{
    /// <summary>MF_MT_VIDEO_ROTATION – copies the "display rotated" flag of phone videos into the output.</summary>
    static readonly Guid MfMtVideoRotation = new("C380465D-2271-428C-9B83-ECEA3B4A85C1");

    public const double MinVideoKbps = 220;

    public sealed record VideoInfo(TimeSpan Duration, uint Width, uint Height, uint Rotation, double FrameRate, bool HasAudio);

    public static async Task<VideoInfo> ProbeAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        VideoProperties props = await file.Properties.GetVideoPropertiesAsync();

        uint width = props.Width, height = props.Height, rotation = 0;
        double fps = 30;
        bool hasAudio = true;

        try
        {
            // The source's own encoding profile carries the coded frame size, frame rate and rotation.
            var source = await MediaEncodingProfile.CreateFromFileAsync(file);
            if (source.Video != null)
            {
                if (source.Video.Width > 0) { width = source.Video.Width; height = source.Video.Height; }
                if (source.Video.FrameRate.Denominator > 0)
                    fps = (double)source.Video.FrameRate.Numerator / source.Video.FrameRate.Denominator;
                if (source.Video.Properties.TryGetValue(MfMtVideoRotation, out var rot) && rot != null)
                    rotation = Convert.ToUInt32(rot);
            }
            hasAudio = source.Audio != null;
        }
        catch (Exception ex)
        {
            Log.Error("CreateFromFileAsync", ex);
        }

        if (rotation == 0)
        {
            rotation = props.Orientation switch
            {
                VideoOrientation.Rotate90 => 90u,
                VideoOrientation.Rotate180 => 180u,
                VideoOrientation.Rotate270 => 270u,
                _ => 0u
            };
        }

        if (double.IsNaN(fps) || fps < 1 || fps > 240) fps = 30;
        return new VideoInfo(props.Duration, width, height, rotation, fps, hasAudio);
    }

    /// <summary>Bytes needed at the lowest acceptable quality (used to decide if a video can fit at all).</summary>
    public static long MinimumBytes(VideoInfo info) =>
        (long)(Math.Max(1, info.Duration.TotalSeconds) * (MinVideoKbps + (info.HasAudio ? 64 : 0)) * 1000 / 8 / 0.93);

    public static async Task<long> TranscodeAsync(
        string sourcePath, string destinationPath, VideoInfo info, long targetBytes, bool preferHevc,
        IProgress<double>? progress, CancellationToken ct)
    {
        double seconds = Math.Max(1, info.Duration.TotalSeconds);
        double totalKbps = targetBytes * 8.0 / 1000.0 / seconds * 0.93; // container + rate-control headroom
        uint audioKbps = !info.HasAudio ? 0u : totalKbps >= 1500 ? 128u : totalKbps >= 600 ? 96u : 64u;
        double videoKbps = totalKbps - audioKbps;

        if (videoKbps < MinVideoKbps)
            throw new UserFacingException(
                $"Das Video ist zu lang für das E-Mail-Limit (mind. {Format.Size(MinimumBytes(info))} nötig). " +
                "Tipp: Video kürzen oder über einen Freigabe-Link senden.");

        videoKbps = Math.Min(videoKbps, preferHevc ? 4500 : 6500); // more doesn't look better for mail
        double equivalent = preferHevc ? videoKbps * 1.4 : videoKbps;
        int maxEdge = equivalent >= 4500 ? 1920 : equivalent >= 2000 ? 1280 : equivalent >= 900 ? 960 : equivalent >= 500 ? 854 : 640;
        var (w, h) = Fit(info.Width, info.Height, maxEdge);

        // 50/60 fps phone clips: half the frames, half the bits.
        double fps = info.FrameRate > 31 ? info.FrameRate / 2 : info.FrameRate;

        MediaEncodingProfile profile = preferHevc
            ? MediaEncodingProfile.CreateHevc(VideoEncodingQuality.HD1080p)
            : MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = w;
        profile.Video.Height = h;
        profile.Video.Bitrate = (uint)(videoKbps * 1000);
        profile.Video.FrameRate.Numerator = (uint)Math.Round(fps * 1000);
        profile.Video.FrameRate.Denominator = 1000;
        if (info.Rotation is 90 or 180 or 270)
            profile.Video.Properties[MfMtVideoRotation] = info.Rotation;

        if (info.HasAudio)
        {
            profile.Audio.Bitrate = audioKbps * 1000;
            profile.Audio.ChannelCount = 2;
        }
        else
        {
            profile.Audio = null;
        }

        var source = await StorageFile.GetFileFromPathAsync(sourcePath);
        File.WriteAllBytes(destinationPath, Array.Empty<byte>());
        var destination = await StorageFile.GetFileFromPathAsync(destinationPath);

        var transcoder = new MediaTranscoder
        {
            HardwareAccelerationEnabled = true,
            VideoProcessingAlgorithm = MediaVideoProcessingAlgorithm.Default
        };

        PrepareTranscodeResult prepared = await transcoder.PrepareFileTranscodeAsync(source, destination, profile);
        if (!prepared.CanTranscode)
        {
            string reason = prepared.FailureReason switch
            {
                TranscodeFailureReason.CodecNotFound => preferHevc
                    ? "HEVC-Encoder fehlt („HEVC-Videoerweiterungen“). In den Einstellungen PreferHevc ausschalten."
                    : "Für dieses Videoformat fehlt ein Codec (z. B. „HEVC-Videoerweiterungen“ für iPhone-Videos).",
                TranscodeFailureReason.InvalidProfile => "Das Video konnte mit diesen Einstellungen nicht umgewandelt werden.",
                _ => "Das Video konnte nicht umgewandelt werden."
            };
            throw new UserFacingException(reason);
        }

        var operation = prepared.TranscodeAsync();
        var reporter = new Progress<double>(p => progress?.Report(Math.Clamp(p / 100.0, 0, 1)));
        await operation.AsTask(ct, reporter);

        return new FileInfo(destinationPath).Length;
    }

    /// <summary>Scales the coded size so the long edge is ≤ maxEdge, keeping aspect and even numbers (encoder requirement).</summary>
    static (uint W, uint H) Fit(uint width, uint height, int maxEdge)
    {
        if (width == 0 || height == 0) return (1280, 720);
        double factor = Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
        uint w = (uint)Math.Max(2, Math.Round(width * factor / 2) * 2);
        uint h = (uint)Math.Max(2, Math.Round(height * factor / 2) * 2);
        return (w, h);
    }
}

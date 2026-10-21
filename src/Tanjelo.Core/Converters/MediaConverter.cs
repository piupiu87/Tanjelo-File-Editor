using System.Globalization;
using System.Text.Json;

namespace Tanjelo.Core.Converters;

/// <summary>Audio and video through ffmpeg. Re-wraps streams without re-encoding whenever the target container allows it.</summary>
public sealed class MediaConverter(Toolbox tools) : IConverter
{
    static readonly Target[] VideoOutputs =
    [
        new("mp4", "MP4", "H.264, plays everywhere", Section.Video),
        new("mov", "MOV", "QuickTime movie", Section.Video),
        new("mkv", "MKV", "Matroska, keeps every track", Section.Video),
        new("webm", "WebM", "VP9 + Opus for the web", Section.Video),
        new("avi", "AVI", "Legacy MPEG-4", Section.Video),
        new("gif", "GIF", "Animated, 15 fps, up to 640 px", Section.Image),
    ];

    static readonly Target[] AudioOutputs =
    [
        new("mp3", "MP3", "Plays everywhere, ~190 kbps", Section.Audio),
        new("m4a", "M4A", "AAC, 256 kbps", Section.Audio),
        new("wav", "WAV", "Uncompressed", Section.Audio),
        new("flac", "FLAC", "Lossless, compressed", Section.Audio),
        new("ogg", "OGG", "Ogg Vorbis", Section.Audio),
        new("opus", "Opus", "Small and efficient", Section.Audio),
        new("aiff", "AIFF", "Uncompressed (Apple)", Section.Audio),
    ];

    static readonly HashSet<string> ExtractableAudio = ["mp3", "m4a", "wav", "flac"];

    public IEnumerable<Target> TargetsFor(string input)
    {
        if (tools.Ffmpeg is null) return [];
        var key = Formats.Key(input);
        if (Formats.Videos.Contains(key))
            return VideoOutputs
                .Concat(AudioOutputs.Where(t => ExtractableAudio.Contains(t.Key)).Select(t => t with { Description = "Audio track only" }));
        if (Formats.Audio.Contains(key))
            return AudioOutputs;
        if (key == "gif")
            return VideoOutputs.Where(t => t.Key is "mp4" or "webm").Select(t => t with { Description = "Animated GIF as video" });
        return [];
    }

    public string? HintFor(string input)
    {
        var key = Formats.Key(input);
        return tools.Ffmpeg is null && (Formats.Videos.Contains(key) || Formats.Audio.Contains(key))
            ? "Audio and video need ffmpeg: winget install Gyan.FFmpeg.Essentials"
            : null;
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await ProbeAsync(input, ctx.Cancellation);
        if (target.Section == Section.Audio && media.Audio is null && media.Probed)
            throw new ConversionException("This file has no audio track.");

        var output = OutputPaths.Beside(input, target.Key);
        ConversionException? lastError = null;
        foreach (var args in Plans(target.Key, media))
        {
            try
            {
                await RunFfmpegAsync(input, output, args, media.Duration, ctx);
                return [output];
            }
            catch (ConversionException ex)
            {
                // A stream-copy attempt can fail on odd inputs; the next plan re-encodes.
                lastError = ex;
                OutputPaths.TryDelete(output);
            }
            catch
            {
                OutputPaths.TryDelete(output);
                throw;
            }
        }
        throw lastError!;
    }

    /// <summary>ffmpeg argument sets to try in order: a fast stream copy when the codecs fit, then a re-encode.</summary>
    static IEnumerable<string[]> Plans(string key, MediaInfo m)
    {
        string[] videoMap = ["-map", "0:v:0", "-map", "0:a?"];
        string[] audioMap = ["-vn", "-map", "0:a:0"];
        string[] h264 = [.. videoMap, "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:a", "aac", "-b:a", "192k"];
        string[] hevcTag = m.Video == "hevc" ? ["-tag:v", "hvc1"] : [];

        switch (key)
        {
            case "mp4":
                if (m.Video is "h264" or "hevc" && m.Audio is null or "aac" or "mp3")
                    yield return [.. videoMap, "-c", "copy", .. hevcTag, "-movflags", "+faststart"];
                yield return [.. h264, "-movflags", "+faststart"];
                break;
            case "mov":
                if (m.Video is "h264" or "hevc" or "prores" or "mjpeg" && m.Audio is null or "aac" or "alac" or "mp3" or "pcm_s16le" or "pcm_s24le")
                    yield return [.. videoMap, "-c", "copy", .. hevcTag];
                yield return h264;
                break;
            case "mkv":
                yield return ["-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-c", "copy"];
                yield return h264;
                break;
            case "webm":
                if (m.Video is "vp8" or "vp9" or "av1" && m.Audio is null or "opus" or "vorbis")
                    yield return [.. videoMap, "-c", "copy"];
                yield return [.. videoMap, "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4",
                    "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k"];
                break;
            case "avi":
                yield return [.. videoMap, "-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "3", "-c:a", "libmp3lame", "-q:a", "3"];
                break;
            case "gif":
                yield return ["-an", "-vf", "fps=15,scale='min(640,iw)':-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle", "-loop", "0"];
                break;
            case "mp3":
                yield return [.. audioMap, "-c:a", "libmp3lame", "-q:a", "2"];
                break;
            case "m4a":
                if (m.Audio == "aac")
                    yield return [.. audioMap, "-c:a", "copy"];
                yield return [.. audioMap, "-c:a", "aac", "-b:a", "256k"];
                break;
            case "wav":
                yield return [.. audioMap, "-c:a", "pcm_s16le"];
                break;
            case "flac":
                yield return [.. audioMap, "-c:a", "flac"];
                break;
            case "ogg":
                yield return [.. audioMap, "-c:a", "libvorbis", "-q:a", "6"];
                break;
            case "opus":
                yield return [.. audioMap, "-c:a", "libopus", "-b:a", "160k"];
                break;
            case "aiff":
                yield return [.. audioMap, "-c:a", "pcm_s16be"];
                break;
            default:
                throw new ConversionException($"Unknown media target '{key}'.");
        }
    }

    async Task RunFfmpegAsync(string input, string output, string[] args, double duration, JobContext ctx)
    {
        string[] all = ["-hide_banner", "-nostdin", "-y", "-v", "error", "-progress", "pipe:1", "-nostats", "-i", input, .. args, output];
        var result = await ProcessRunner.RunAsync(tools.Ffmpeg!, all, ctx.Cancellation, line =>
        {
            if (duration > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_us=".Length), out var microseconds))
                ctx.Progress.Report(microseconds / 1_000_000.0 / duration);
        });
        if (result.ExitCode != 0)
            throw new ConversionException($"ffmpeg failed: {result.LastError}");
    }

    sealed record MediaInfo(bool Probed, string? Video, string? Audio, double Duration);

    async Task<MediaInfo> ProbeAsync(string input, CancellationToken ct)
    {
        if (tools.Ffprobe is null)
            return new MediaInfo(false, null, null, 0);

        var result = await ProcessRunner.RunAsync(tools.Ffprobe,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name:stream_disposition=attached_pic", "-of", "json", input], ct);
        if (result.ExitCode != 0)
            throw new ConversionException($"ffmpeg can't read this file: {result.LastError}");

        using var json = JsonDocument.Parse(result.StdOut);
        string? video = null, audio = null;
        if (json.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                var coverArt = stream.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;
                if (type == "video" && !coverArt) video ??= codec;
                if (type == "audio") audio ??= codec;
            }
        }
        var duration = 0.0;
        if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var dur))
            double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        return new MediaInfo(true, video, audio, duration);
    }
}

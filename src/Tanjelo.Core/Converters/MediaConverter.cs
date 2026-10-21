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
        new("wmv", "WMV", "Windows Media Video", Section.Video),
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
        new("wma", "WMA", "Windows Media, 192 kbps", Section.Audio),
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
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        if (target.Section == Section.Audio && media.Audio is null && media.Probed)
            throw new ConversionException("This file has no audio track.");

        var output = OutputPaths.Beside(input, target.Key);
        // A stream-copy attempt can fail on odd inputs; the next plan re-encodes.
        await Ffmpeg.RunFirstWorkingAsync(tools, input, output, Plans(target.Key, media), media.Duration, ctx);
        return [output];
    }

    /// <summary>ffmpeg argument sets to try in order: a fast stream copy when the codecs fit, then a re-encode.</summary>
    static IEnumerable<string[]> Plans(string key, MediaInfo m)
    {
        string[] videoMap = ["-map", "0:v:0", "-map", "0:a?"];
        string[] audioMap = ["-vn", "-map", "0:a:0"];
        string[] hevcTag = m.Video == "hevc" ? ["-tag:v", "hvc1"] : [];
        string[] upToStereo = m.Channels > 2 ? ["-ac", "2"] : []; // Windows Media Audio holds at most two channels

        switch (key)
        {
            case "mp4":
                if (m.Video is "h264" or "hevc" && m.Audio is null or "aac" or "mp3")
                    yield return [.. videoMap, "-c", "copy", .. hevcTag, "-movflags", "+faststart"];
                yield return [.. videoMap, .. Ffmpeg.VideoEncoder(key), "-movflags", "+faststart"];
                break;
            case "mov":
                if (m.Video is "h264" or "hevc" or "prores" or "mjpeg" && m.Audio is null or "aac" or "alac" or "mp3" or "pcm_s16le" or "pcm_s24le")
                    yield return [.. videoMap, "-c", "copy", .. hevcTag];
                yield return [.. videoMap, .. Ffmpeg.VideoEncoder(key)];
                break;
            case "mkv":
                yield return ["-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-c", "copy"];
                yield return [.. videoMap, .. Ffmpeg.VideoEncoder(key)];
                break;
            case "webm":
                if (m.Video is "vp8" or "vp9" or "av1" && m.Audio is null or "opus" or "vorbis")
                    yield return [.. videoMap, "-c", "copy"];
                yield return [.. videoMap, .. Ffmpeg.VideoEncoder(key)];
                break;
            case "avi":
                yield return [.. videoMap, .. Ffmpeg.VideoEncoder(key)];
                break;
            case "wmv":
                yield return [.. videoMap, .. Ffmpeg.VideoEncoder(key), .. upToStereo];
                break;
            case "gif":
                yield return ["-an", "-vf", "fps=15,scale='min(640,iw)':-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle", "-loop", "0"];
                break;
            case "m4a":
                if (m.Audio == "aac")
                    yield return [.. audioMap, "-c:a", "copy"];
                yield return [.. audioMap, .. Ffmpeg.AudioEncoder(key)];
                break;
            case "wma":
                yield return [.. audioMap, .. Ffmpeg.AudioEncoder(key), .. upToStereo];
                break;
            case "mp3" or "wav" or "flac" or "ogg" or "opus" or "aiff":
                yield return [.. audioMap, .. Ffmpeg.AudioEncoder(key)];
                break;
            default:
                throw new ConversionException($"Unknown media target '{key}'.");
        }
    }
}

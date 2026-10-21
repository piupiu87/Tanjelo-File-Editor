using System.Globalization;
using System.Text.Json;

namespace Tanjelo.Core.Converters;

/// <param name="Probed">False when ffprobe isn't available, so nothing else is known.</param>
/// <param name="Channels">Channels in the first audio track; 0 when unknown.</param>
/// <param name="SampleRate">Sample rate of the first audio track in Hz; 0 when unknown.</param>
sealed record MediaInfo(bool Probed, string? Video, string? Audio, double Duration, int Channels = 0, int SampleRate = 0);

/// <summary>ffmpeg and ffprobe, shared by the media converter and the media tools.</summary>
static class Ffmpeg
{
    /// <summary>Makes even dimensions, which H.264, WMV and 4:2:0 colour need.</summary>
    public const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    /// <summary>Encoder settings for writing audio in a format, the same ones the converter uses.</summary>
    public static string[] AudioEncoder(string key) => key switch
    {
        "mp3" => ["-c:a", "libmp3lame", "-q:a", "2"],
        "m4a" or "aac" => ["-c:a", "aac", "-b:a", "256k"],
        "wav" => ["-c:a", "pcm_s16le"],
        "flac" => ["-c:a", "flac"],
        "ogg" => ["-c:a", "libvorbis", "-q:a", "6"],
        "opus" => ["-c:a", "libopus", "-b:a", "160k"],
        "aiff" => ["-c:a", "pcm_s16be"],
        "wma" => ["-c:a", "wmav2", "-b:a", "192k"],
        _ => throw new ConversionException($"Can't write .{key} audio."),
    };

    /// <summary>Containers <see cref="VideoEncoder"/> can write; anything else is re-encoded to MP4.</summary>
    public static bool CanEncodeVideo(string key) => key is "mp4" or "m4v" or "mov" or "mkv" or "webm" or "avi" or "wmv";

    /// <summary>Encoder settings for re-encoding video (and its sound) into a container.</summary>
    /// <param name="filter">Video filters to run first, such as a speed change.</param>
    public static string[] VideoEncoder(string key, string? filter = null)
    {
        string Filters(bool even) => string.Join(',', new[] { filter, even ? EvenSize : null }.OfType<string>());
        string[] Vf(bool even) => Filters(even) is { Length: > 0 } f ? ["-vf", f] : [];
        return key switch
        {
            "mp4" or "m4v" or "mov" or "mkv" => ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", .. Vf(even: true),
                "-c:a", "aac", "-b:a", "192k"],
            "webm" => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4",
                "-pix_fmt", "yuv420p", .. Vf(even: false), "-c:a", "libopus", "-b:a", "128k"],
            "avi" => ["-c:v", "mpeg4", "-vtag", "xvid", "-q:v", "3", .. Vf(even: false), "-c:a", "libmp3lame", "-q:a", "3"],
            "wmv" => ["-c:v", "wmv2", "-q:v", "3", "-pix_fmt", "yuv420p", .. Vf(even: true), "-c:a", "wmav2", "-b:a", "192k"],
            _ => throw new ConversionException($"Can't write .{key} video."),
        };
    }

    public static async Task<MediaInfo> ProbeAsync(Toolbox tools, string input, CancellationToken ct)
    {
        if (tools.Ffprobe is null)
            return new MediaInfo(false, null, null, 0);

        var result = await ProcessRunner.RunAsync(tools.Ffprobe,
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,channels,sample_rate:stream_disposition=attached_pic", "-of", "json", input], ct);
        if (result.ExitCode != 0)
            throw new ConversionException($"ffmpeg can't read this file: {result.LastError}");

        using var json = JsonDocument.Parse(result.StdOut);
        string? video = null, audio = null;
        int channels = 0, sampleRate = 0;
        if (json.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                var coverArt = stream.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;
                if (type == "video" && !coverArt) video ??= codec;
                if (type == "audio" && audio is null)
                {
                    audio = codec;
                    if (stream.TryGetProperty("channels", out var ch)) channels = ch.GetInt32();
                    if (stream.TryGetProperty("sample_rate", out var sr)) int.TryParse(sr.GetString(), out sampleRate);
                }
            }
        }
        var duration = 0.0;
        if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var dur))
            double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        return new MediaInfo(true, video, audio, duration, channels, sampleRate);
    }

    /// <summary>Runs <c>ffmpeg -i input [args] output</c>, reporting progress against <paramref name="duration"/> seconds of output.</summary>
    /// <param name="onLog">Receives ffmpeg's log lines; set <paramref name="logLevel"/> to "info" for analysis filters' results.</param>
    public static async Task RunAsync(Toolbox tools, string input, string output, string[] args, double duration, IProgress<double> progress,
        CancellationToken ct, string logLevel = "error", Action<string>? onLog = null)
    {
        string[] all = ["-hide_banner", "-nostdin", "-y", "-v", logLevel, "-progress", "pipe:1", "-nostats", "-i", input, .. args, output];
        var result = await ProcessRunner.RunAsync(tools.Ffmpeg!, all, ct, line =>
        {
            if (duration > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan("out_time_us=".Length), out var microseconds))
                progress.Report(microseconds / 1_000_000.0 / duration);
        }, onStdErr: onLog);
        if (result.ExitCode != 0)
            throw new ConversionException($"ffmpeg failed: {result.LastError}");
    }

    /// <summary>
    /// Tries each set of arguments in turn, typically a fast stream copy and then a re-encode, and stops at the
    /// first that works. A failed attempt's output is deleted.
    /// </summary>
    public static async Task RunFirstWorkingAsync(Toolbox tools, string input, string output, IEnumerable<string[]> plans, double duration,
        JobContext ctx, IProgress<double>? progress = null)
    {
        ConversionException? lastError = null;
        foreach (var args in plans)
        {
            try
            {
                await RunAsync(tools, input, output, args, duration, progress ?? ctx.Progress, ctx.Cancellation);
                return;
            }
            catch (ConversionException ex)
            {
                lastError = ex;
                OutputPaths.TryDelete(output);
            }
            catch
            {
                OutputPaths.TryDelete(output);
                throw;
            }
        }
        throw lastError ?? new ConversionException("ffmpeg had nothing to do.");
    }
}

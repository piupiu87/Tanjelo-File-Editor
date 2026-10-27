using System.Globalization;
using System.Text.Json;

namespace Tanjelo.Core.Converters;

/// <param name="Probed">False when ffprobe isn't available, so nothing else is known.</param>
/// <param name="Channels">Channels in the first audio track; 0 when unknown.</param>
/// <param name="SampleRate">Sample rate of the first audio track in Hz; 0 when unknown.</param>
sealed record MediaInfo(bool Probed, string? Video, string? Audio, double Duration, int Channels = 0, int SampleRate = 0)
{
    /// <summary>The picture's size as it plays, after any rotation the file asks for; 0 when unknown.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Whether the file carries a picture of its own, such as an album cover.</summary>
    public bool CoverArt { get; init; }

    /// <summary>The turn the file asks players to make, in degrees; 0 when there's none.</summary>
    public int Rotation { get; init; }

    /// <summary>Frames per second of the picture; 0 when unknown.</summary>
    public double FrameRate { get; init; }

    /// <summary>ffmpeg's name for how the picture's pixels are stored, such as "yuv420p".</summary>
    public string? PixelFormat { get; init; }

    /// <summary>The whole file's bit rate, and the first audio track's, in bits per second; 0 when unknown.</summary>
    public long Bitrate { get; init; }
    public long AudioBitrate { get; init; }

    /// <summary>ffmpeg's name for the first audio track's channel layout, such as "stereo" or "5.1(side)".</summary>
    public string? ChannelLayout { get; init; }

    /// <summary>Where the file's timeline starts, in seconds: ffmpeg's -ss and every time in Tanjelo count from here.</summary>
    public double StartTime { get; init; }

    /// <summary>Tags on the file itself (title, artist, location…), as ffmpeg names them.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
}

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

    /// <summary>
    /// Encoder settings that keep a file's sound as it was, for tools that have to re-encode it: lossless stays
    /// lossless, with the same sample format, and lossy keeps its bitrate.
    /// </summary>
    public static string[] KeepSound(string key, MediaInfo media)
    {
        var bitrate = media.AudioBitrate > 0 ? media.AudioBitrate : media.Bitrate;
        string[] Rate(string codec, long fallback) =>
            ["-c:a", codec, "-b:a", string.Create(CultureInfo.InvariantCulture, $"{Math.Max(32, (int)Math.Round((bitrate > 0 ? bitrate : fallback) / 1000.0))}k")];
        return key switch
        {
            "wav" => ["-c:a", media.Audio is { } wav && wav.StartsWith("pcm_", StringComparison.Ordinal) ? wav : "pcm_s16le"],
            "aiff" => ["-c:a", media.Audio is { } aiff && aiff.StartsWith("pcm_", StringComparison.Ordinal) ? aiff : "pcm_s16be"],
            "flac" => ["-c:a", "flac"],
            "mp3" => Rate("libmp3lame", 192_000),
            "m4a" or "aac" => Rate("aac", 192_000),
            "ogg" => Rate("libvorbis", 160_000),
            "opus" => Rate("libopus", 128_000),
            "wma" => Rate("wmav2", 192_000),
            _ => AudioEncoder(key),
        };
    }

    /// <summary>Containers <see cref="VideoEncoder"/> can write; anything else is re-encoded to MP4.</summary>
    public static bool CanEncodeVideo(string key) => key is "mp4" or "m4v" or "mov" or "mkv" or "webm" or "avi" or "wmv";

    /// <summary>Encoder settings for re-encoding video (and its sound) into a container.</summary>
    /// <param name="filter">Video filters to run first, such as a speed change.</param>
    /// <param name="graph">Whether the picture comes from a filter graph of the caller's, which then makes it even itself.</param>
    public static string[] VideoEncoder(string key, string? filter = null, bool graph = false)
    {
        string Filters(bool even) => string.Join(',', new[] { filter, even ? EvenSize : null }.OfType<string>());
        string[] Vf(bool even) => !graph && Filters(even) is { Length: > 0 } f ? ["-vf", f] : [];
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
            ["-v", "error", "-show_entries",
                "format=duration,start_time,bit_rate:format_tags:stream=codec_type,codec_name,width,height,channels,channel_layout,sample_rate,bit_rate," +
                "r_frame_rate,pix_fmt:stream_side_data=rotation:stream_disposition=attached_pic", "-of", "json", input], ct);
        if (result.ExitCode != 0)
            throw new ConversionException($"ffmpeg can't read this file: {result.LastError}");

        using var json = JsonDocument.Parse(result.StdOut);
        string? video = null, audio = null, layout = null, pixels = null;
        int channels = 0, sampleRate = 0, width = 0, height = 0, rotation = 0;
        var cover = false;
        long audioBitrate = 0;
        double frameRate = 0;
        if (json.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                var coverArt = stream.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a) && a.GetInt32() == 1;
                cover |= type == "video" && coverArt;
                if (type == "video" && !coverArt && video is null)
                {
                    video = codec;
                    if (stream.TryGetProperty("width", out var w)) width = w.GetInt32();
                    if (stream.TryGetProperty("height", out var h)) height = h.GetInt32();
                    if (stream.TryGetProperty("pix_fmt", out var pf)) pixels = pf.GetString();
                    if (stream.TryGetProperty("r_frame_rate", out var fr) && fr.GetString()?.Split('/') is [var num, var den]
                        && double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                        && double.TryParse(den, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd) && dd > 0)
                        frameRate = n / dd;
                    // Phones store portrait video sideways with a note to turn it; ffmpeg turns it as it decodes.
                    if (stream.TryGetProperty("side_data_list", out var sides)
                        && sides.EnumerateArray().FirstOrDefault(s => s.TryGetProperty("rotation", out _)) is { ValueKind: JsonValueKind.Object } side)
                        rotation = (int)side.GetProperty("rotation").GetDouble();
                    if (Math.Abs(rotation) % 180 == 90)
                        (width, height) = (height, width);
                }
                if (type == "audio" && audio is null)
                {
                    audio = codec;
                    if (stream.TryGetProperty("channels", out var ch)) channels = ch.GetInt32();
                    if (stream.TryGetProperty("sample_rate", out var sr)) int.TryParse(sr.GetString(), out sampleRate);
                    if (stream.TryGetProperty("channel_layout", out var cl)) layout = cl.GetString();
                    if (stream.TryGetProperty("bit_rate", out var br)) long.TryParse(br.GetString(), out audioBitrate);
                }
            }
        }
        double duration = 0, start = 0;
        long bitrate = 0;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (json.RootElement.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var dur))
                double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            if (format.TryGetProperty("start_time", out var st))
                double.TryParse(st.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out start);
            if (format.TryGetProperty("bit_rate", out var br))
                long.TryParse(br.GetString(), out bitrate);
            if (format.TryGetProperty("tags", out var list))
                foreach (var tag in list.EnumerateObject())
                    tags[tag.Name] = tag.Value.ToString();
        }
        return new MediaInfo(true, video, audio, duration, channels, sampleRate)
        {
            Width = width,
            Height = height,
            Rotation = rotation,
            CoverArt = cover,
            FrameRate = frameRate,
            PixelFormat = pixels,
            Bitrate = bitrate,
            AudioBitrate = audioBitrate,
            ChannelLayout = layout,
            Tags = tags,
            StartTime = start,
        };
    }

    /// <summary>Runs <c>ffmpeg -i input [args] output</c>, reporting progress against <paramref name="duration"/> seconds of output.</summary>
    /// <param name="onLog">Receives ffmpeg's log lines; set <paramref name="logLevel"/> to "info" for analysis filters' results.</param>
    public static Task RunAsync(Toolbox tools, string input, string output, string[] args, double duration, IProgress<double> progress,
        CancellationToken ct, string logLevel = "error", Action<string>? onLog = null) =>
        RunRawAsync(tools, ["-i", input, .. args, output], duration, progress, ct, logLevel, onLog);

    /// <summary>Runs ffmpeg with these arguments after the usual ones, for jobs with several inputs or options before the input.</summary>
    public static async Task RunRawAsync(Toolbox tools, string[] args, double duration, IProgress<double> progress,
        CancellationToken ct, string logLevel = "error", Action<string>? onLog = null)
    {
        string[] all = ["-hide_banner", "-nostdin", "-y", "-v", logLevel, "-progress", "pipe:1", "-nostats", .. args];
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
    /// Runs ffmpeg or ffprobe with its standard output as a stream of bytes, such as samples or raw pictures, for
    /// <paramref name="read"/> to take in as it comes. Lines of the error output go to <paramref name="onLog"/>.
    /// </summary>
    public static async Task PipeAsync(string exe, string[] args, Func<Stream, Task> read, CancellationToken ct, Action<string>? onLog = null)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = new System.Diagnostics.Process { StartInfo = psi };
        var last = "";
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not { Length: > 0 } line) return;
            last = line;
            onLog?.Invoke(line);
        };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ConversionException($"Couldn't start {Path.GetFileName(exe)}: {ex.Message}", ex);
        }
        process.StandardInput.Close();
        process.BeginErrorReadLine();
        using var kill = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        try
        {
            await read(process.StandardOutput.BaseStream);
            // Whatever the reader left is drained, so the program isn't stuck writing to a full pipe.
            if (process.StandardOutput.BaseStream.CanRead) await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
            await process.WaitForExitAsync(ct);
        }
        catch (Exception ex)
        {
            // A reader that gave up leaves the program writing to a pipe nobody empties.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (ex is IOException) throw new ConversionException($"ffmpeg failed: {last}", ex);
            throw;
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new ConversionException($"ffmpeg failed: {last}");
    }

    /// <summary>Reads exactly <paramref name="buffer"/>'s length; false at the end of the stream.</summary>
    public static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var at = 0;
        while (at < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(at), ct);
            if (read == 0) return false;
            at += read;
        }
        return true;
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

    /// <summary>
    /// The frame at <paramref name="seconds"/> as a PNG, after <paramref name="filter"/> if given, and at most
    /// <paramref name="maxSize"/> pixels on its long side (0 keeps the full size).
    /// </summary>
    public static async Task<byte[]> FrameAsync(Toolbox tools, string input, double seconds, int maxSize, CancellationToken ct, string? filter = null)
    {
        using var temp = OutputPaths.CreateTemp();
        var output = Path.Combine(temp.Path, "frame.png");
        string? fit = maxSize > 0 ? $"scale='min({maxSize},iw)':'min({maxSize},ih)':force_original_aspect_ratio=decrease" : null;
        var filters = string.Join(',', new[] { filter, fit }.OfType<string>());
        string[] vf = filters.Length > 0 ? ["-vf", filters] : [];
        // Seeking before the input jumps to the nearest keyframe and then decodes up to the exact moment.
        await RunRawAsync(tools, ["-ss", Time(seconds), "-i", input, "-map", "0:v:0", "-frames:v", "1", .. vf, "-update", "1", output],
            0, new ActionProgress<double>(_ => { }), ct);
        if (!File.Exists(output))
            throw new ConversionException($"There's no picture at {OptionValues.FormatTime(seconds)}.");
        return await File.ReadAllBytesAsync(output, ct);
    }

    /// <summary>Seconds as ffmpeg reads them.</summary>
    public static string Time(double seconds) => Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);
}

using System.Globalization;

namespace Tanjelo.Core.Converters;

/// <summary>Audio and video tools through ffmpeg. Each writes a changed copy beside the original, named after the change.</summary>
public sealed class MediaTools(Toolbox tools) : IConverter
{
    static Target Tool(string key, string label, string description, Section section, string running, string done) =>
        new(key, label, description, section) { Kind = TargetKind.Tool, Running = running, Done = done };

    static readonly Target[] VideoTools =
    [
        Tool("mute", "Mute", "Remove the sound, losslessly", Section.Video, "Muting…", "Muted"),
        Speed("0.5", "Slow motion, same pitch"),
        Speed("1.5", "Faster, same pitch"),
        Speed("2", "Twice as fast, same pitch"),
        Split(2, "Two"),
        Split(3, "Three"),
        Split(4, "Four"),
    ];

    static Target Split(int parts, string count) =>
        Tool($"split-video-{parts}", $"Split in {parts}", $"{count} equal parts", Section.Video, "Splitting…", "Split") with
        {
            Parameters = new Dictionary<string, string> { ["parts"] = parts.ToString(CultureInfo.InvariantCulture) },
        };

    static Target Speed(string factor, string description) =>
        Tool($"speed-{factor}x", $"{factor}× speed", description, Section.Video, "Changing speed…", "Speed changed") with
        {
            Parameters = new Dictionary<string, string> { ["factor"] = factor },
        };

    static readonly Target[] AudioTools =
    [
        Tool("normalize", "Normalize volume", "Even loudness, -16 LUFS", Section.Audio, "Normalizing…", "Normalized"),
        Tool("mono", "To mono", "All channels in one", Section.Audio, "Mixing to mono…", "Mixed to mono"),
        Tool("stereo", "To stereo", "Two channels", Section.Audio, "Making stereo…", "Made stereo"),
        Tool("trim-silence", "Trim silence", "At the start and end", Section.Audio, "Trimming silence…", "Silence trimmed"),
    ];

    /// <summary>EBU R128 loudness: -16 LUFS integrated, peaks at most -1.5 dBTP.</summary>
    const string Loudness = "loudnorm=I=-16:TP=-1.5:LRA=11";

    public IEnumerable<Target> TargetsFor(string input)
    {
        if (tools.Ffmpeg is null || tools.Ffprobe is null) return [];
        var key = Formats.Key(input);
        if (Formats.Videos.Contains(key)) return VideoTools;
        if (Formats.Audio.Contains(key)) return AudioTools;
        return [];
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        return target.Key switch
        {
            "mute" => [await MuteAsync(input, media, ctx)],
            _ when target.Key.StartsWith("speed-", StringComparison.Ordinal) => [await SpeedAsync(input, media, target, ctx)],
            _ when target.Key.StartsWith("split-video-", StringComparison.Ordinal) => await SplitAsync(input, media, target, ctx),
            "normalize" => [await NormalizeAsync(input, media, ctx)],
            "mono" => [await ChannelsAsync(input, media, 1, ctx)],
            "stereo" => [await ChannelsAsync(input, media, 2, ctx)],
            "trim-silence" => [await TrimSilenceAsync(input, media, ctx)],
            _ => throw new ConversionException($"Unknown tool '{target.Key}'."),
        };
    }

    async Task<string> MuteAsync(string input, MediaInfo media, JobContext ctx)
    {
        if (media.Audio is null)
            throw new ConversionException("This video has no sound to remove.");
        var output = OutputPaths.Suffixed(input, "muted");
        // Subtitles come along when the container takes them as they are; otherwise just the picture.
        await Ffmpeg.RunFirstWorkingAsync(tools, input, output,
            [["-map", "0:v", "-map", "0:s?", "-c", "copy", .. FastStart(input)], ["-map", "0:v", "-c", "copy", .. FastStart(input)]],
            media.Duration, ctx);
        return output;
    }

    async Task<string> SpeedAsync(string input, MediaInfo media, Target target, JobContext ctx)
    {
        var factor = target.Number("factor", 0.25, 4);
        var text = factor.ToString(CultureInfo.InvariantCulture);
        // Re-encoding is unavoidable; containers the encoder can't write (FLV, MPEG-TS...) become MP4.
        var key = Ffmpeg.CanEncodeVideo(Formats.Key(input)) ? Formats.Key(input) : "mp4";
        var output = OutputPaths.Suffixed(input, text + "x", key == Formats.Key(input) ? null : key);
        string[] sound = media.Audio is null ? ["-an"] : ["-map", "0:a:0", "-filter:a", Tempo(factor)];
        string[] args = ["-map", "0:v:0", .. sound, "-sn", .. Ffmpeg.VideoEncoder(key, $"setpts=PTS/{text}"), .. FastStart(output)];
        await Ffmpeg.RunFirstWorkingAsync(tools, input, output, [args], media.Duration / factor, ctx);
        return output;
    }

    /// <summary>
    /// Cuts the video into equal parts. A keyframe cut lands up to one keyframe interval (typically 2 to 10 seconds)
    /// after the requested point, which is accepted within a tenth of a part, at least 2 and at most 10 seconds, but
    /// never a quarter of a part.
    /// </summary>
    async Task<IReadOnlyList<string>> SplitAsync(string input, MediaInfo media, Target target, JobContext ctx)
    {
        var parts = (int)target.Number("parts", 2, 100);
        if (media.Duration <= 0)
            throw new ConversionException("Couldn't tell how long this video is.");
        if (media.Duration < parts)
            throw new ConversionException($"This video is too short to split into {parts} parts.");

        var length = media.Duration / parts;
        var tolerance = Math.Min(Math.Clamp(length / 10, 2, 10), length / 4);
        return await CutAtAsync(tools, input, media, Enumerable.Range(1, parts - 1).Select(i => length * i).ToList(), tolerance,
            "ffmpeg couldn't split this video evenly.", ctx);
    }

    /// <summary>
    /// Cuts the video at <paramref name="points"/> (in seconds) into clip-part1.mp4 and so on. A stream copy is
    /// instant and lossless but can only cut at keyframes; when that leaves a part more than
    /// <paramref name="tolerance"/> seconds off its intended length, the video is re-encoded with keyframes at the
    /// cut points instead.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> CutAtAsync(Toolbox tools, string input, MediaInfo media, IReadOnlyList<double> points,
        double tolerance, string failure, JobContext ctx)
    {
        var bounds = points.Prepend(0).Append(media.Duration).ToList();
        var lengths = bounds.Zip(bounds.Skip(1), (start, end) => end - start).ToList();
        string Times(double shift) => string.Join(',', points.Select(p => (p - shift).ToString("0.###", CultureInfo.InvariantCulture)));
        string[] map = ["-map", "0:v:0", "-map", "0:a?", "-sn"];
        string[] Segment(string times) => ["-f", "segment", "-segment_times", times, "-reset_timestamps", "1"];
        var key = Formats.Key(input);
        var encodeKey = Ffmpeg.CanEncodeVideo(key) ? key : "mp4";

        using var temp = OutputPaths.CreateTemp();
        var copied = await CutAsync(Formats.Ext(input), [.. map, "-c", "copy", .. Segment(Times(0))], new ActionProgress<double>(f => ctx.Progress.Report(f * 0.1)));
        var pieces = copied is not null && await MatchAsync(copied) ? copied : null;
        if (pieces is null)
        {
            foreach (var file in Directory.GetFiles(temp.Path)) File.Delete(file);
            // The muxer sees timestamps slightly shifted from the encoder's, so it could miss a keyframe forced
            // exactly at the cut and run on to the next one. Each part starts a moment earlier to catch it.
            pieces = await CutAsync(encodeKey, [.. map, .. Ffmpeg.VideoEncoder(encodeKey), "-force_key_frames", Times(0), .. Segment(Times(0.2))],
                new ActionProgress<double>(f => ctx.Progress.Report(0.1 + f * 0.9)));
            if (pieces is null || !await MatchAsync(pieces))
                throw new ConversionException(failure);
        }

        var outputs = new List<string>();
        try
        {
            for (var i = 0; i < pieces.Count; i++)
            {
                var output = OutputPaths.Suffixed(input, $"part{i + 1}", Path.GetExtension(pieces[i]).TrimStart('.'));
                File.Move(pieces[i], output);
                outputs.Add(output);
            }
        }
        catch
        {
            outputs.ForEach(OutputPaths.TryDelete);
            throw;
        }
        return outputs;

        // Runs the segment muxer into the scratch folder; null when ffmpeg fails.
        async Task<List<string>?> CutAsync(string ext, string[] args, IProgress<double> progress)
        {
            try
            {
                await Ffmpeg.RunAsync(tools, input, Path.Combine(temp.Path, "part%03d." + ext), args, media.Duration, progress, ctx.Cancellation);
            }
            catch (ConversionException)
            {
                return null;
            }
            return Directory.GetFiles(temp.Path).Order(StringComparer.Ordinal).ToList();
        }

        async Task<bool> MatchAsync(List<string> files)
        {
            if (files.Count != lengths.Count) return false;
            for (var i = 0; i < files.Count; i++)
            {
                var piece = await Ffmpeg.ProbeAsync(tools, files[i], ctx.Cancellation);
                if (Math.Abs(piece.Duration - lengths[i]) > tolerance) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Two passes: the first measures the loudness, the second applies one steady gain to reach the target.
    /// A single pass would adjust the gain as it goes, squashing the file's quiet and loud moments together.
    /// </summary>
    async Task<string> NormalizeAsync(string input, MediaInfo media, JobContext ctx)
    {
        var log = new List<string>();
        await Ffmpeg.RunAsync(tools, input, "-", ["-map", "0:a:0", "-af", Loudness + ":print_format=json", "-f", "null"], media.Duration,
            new ActionProgress<double>(f => ctx.Progress.Report(f / 2)), ctx.Cancellation, logLevel: "info", onLog: line => { lock (log) log.Add(line); });

        string text;
        lock (log) text = string.Join('\n', log);
        int open = text.LastIndexOf('{'), close = text.LastIndexOf('}');
        if (open < 0 || close < open)
            throw new ConversionException("ffmpeg didn't report the loudness of this file.");
        using var json = System.Text.Json.JsonDocument.Parse(text[open..(close + 1)]);
        string Measured(string name) => json.RootElement.GetProperty(name).GetString()!;
        if (!double.TryParse(Measured("input_i"), NumberStyles.Float, CultureInfo.InvariantCulture, out var loudness) || double.IsInfinity(loudness))
            throw new ConversionException("This file is silent.");

        var output = OutputPaths.Suffixed(input, "normalized");
        // loudnorm works at 192 kHz internally, so ask for the original rate back (Opus only takes 48 kHz).
        var rate = Formats.Key(input) == "opus" || media.SampleRate <= 0 ? 48000 : media.SampleRate;
        var filter = $"{Loudness}:measured_I={Measured("input_i")}:measured_TP={Measured("input_tp")}:measured_LRA={Measured("input_lra")}" +
            $":measured_thresh={Measured("input_thresh")}:offset={Measured("target_offset")}:linear=true";
        string[] args = ["-map", "0:a:0", "-af", filter, "-ar", rate.ToString(CultureInfo.InvariantCulture), .. Ffmpeg.AudioEncoder(Formats.Key(input))];
        await Ffmpeg.RunFirstWorkingAsync(tools, input, output, [args], media.Duration, ctx, new ActionProgress<double>(f => ctx.Progress.Report(0.5 + f / 2)));
        return output;
    }

    /// <summary>Downmixes to mono, or makes stereo: a mono source plays on both sides, surround is mixed down.</summary>
    async Task<string> ChannelsAsync(string input, MediaInfo media, int channels, JobContext ctx)
    {
        var name = channels == 1 ? "mono" : "stereo";
        if (media.Channels == channels)
            throw new ConversionException($"This file is already {name}.");
        var output = OutputPaths.Suffixed(input, name);
        string[] args = ["-map", "0:a:0", "-ac", channels.ToString(CultureInfo.InvariantCulture), .. Ffmpeg.AudioEncoder(Formats.Key(input))];
        await Ffmpeg.RunFirstWorkingAsync(tools, input, output, [args], media.Duration, ctx);
        return output;
    }

    /// <summary>
    /// Finds silence (below -50 dB for at least 0.2 s) and keeps what lies between the silence at the start and the
    /// silence at the end. Pauses in the middle stay. Measuring first and then cutting keeps memory use flat, where
    /// the usual reverse-the-audio trick would hold the whole file in memory.
    /// </summary>
    async Task<string> TrimSilenceAsync(string input, MediaInfo media, JobContext ctx)
    {
        var silences = new List<(double Start, double? End)>();
        await Ffmpeg.RunAsync(tools, input, "-", ["-map", "0:a:0", "-af", "silencedetect=noise=-50dB:d=0.2", "-f", "null"], media.Duration,
            new ActionProgress<double>(f => ctx.Progress.Report(f / 2)), ctx.Cancellation, logLevel: "info", onLog: line =>
            {
                if (Seconds(line, "silence_start: ") is { } start)
                    lock (silences) silences.Add((start, null));
                else if (Seconds(line, "silence_end: ") is { } end)
                    lock (silences) if (silences.Count > 0) silences[^1] = (silences[^1].Start, end);
            });

        var duration = media.Duration;
        double from = 0, to = duration;
        if (silences is [var first, ..] && first.Start <= 0.05)
            from = first.End ?? duration;
        // Older ffmpeg builds leave out the end of a silence that runs to the end of the file.
        if (silences is [.., var last] && (last.End is null || last.End >= duration - 0.05) && last.Start > 0.05)
            to = last.Start;
        if (to - from < 0.05)
            throw new ConversionException("This file is silent.");
        if (from <= 0 && to >= duration)
            throw new ConversionException("There's no silence at the start or end to trim.");

        var output = OutputPaths.Suffixed(input, "trimmed");
        string Time(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);
        string[] args = ["-ss", Time(from), "-t", Time(to - from), "-map", "0:a:0", .. Ffmpeg.AudioEncoder(Formats.Key(input))];
        await Ffmpeg.RunFirstWorkingAsync(tools, input, output, [args], to - from, ctx, new ActionProgress<double>(f => ctx.Progress.Report(0.5 + f / 2)));
        return output;

        static double? Seconds(string line, string label)
        {
            var at = line.IndexOf(label, StringComparison.Ordinal);
            if (at < 0) return null;
            var text = line[(at + label.Length)..].Split(' ', 2)[0];
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? Math.Max(0, value) : null;
        }
    }

    /// <summary>ffmpeg's atempo changes speed without changing pitch; each step must stay within 0.5 to 2.</summary>
    static string Tempo(double factor)
    {
        var steps = new List<string>();
        for (; factor > 2; factor /= 2) steps.Add("atempo=2");
        for (; factor < 0.5; factor /= 0.5) steps.Add("atempo=0.5");
        steps.Add("atempo=" + factor.ToString(CultureInfo.InvariantCulture));
        return string.Join(',', steps);
    }

    /// <summary>Moves an MP4's index to the front so it starts playing before it has fully downloaded.</summary>
    internal static string[] FastStart(string path) => Formats.Key(path) is "mp4" or "m4v" or "mov" ? ["-movflags", "+faststart"] : [];
}

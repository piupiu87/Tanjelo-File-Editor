using System.Globalization;

namespace Tanjelo.Core.Converters;

/// <summary>Audio and video tools through ffmpeg. Each writes a changed copy beside the original, named after the change.</summary>
public sealed class MediaTools(Toolbox tools) : IConverter
{
    static Target Tool(string key, string label, string description, Section section, string running, string done) =>
        new(key, label, description, section) { Kind = TargetKind.Tool, Running = running, Done = done };

    static readonly Target[] VideoTools =
    [
        Tool("mute", "Mute", "Remove the sound; the picture isn't re-encoded", Section.Video, "Muting…", "Muted"),
        Speed("0.5", "Slow motion, same pitch"),
        Speed("1.5", "Faster, same pitch"),
        Speed("2", "Twice as fast, same pitch"),
        Split(2, "Two"),
        Split(3, "Three"),
        Split(4, "Four"),
    ];

    static Target Split(int parts, string count) =>
        Tool($"split-video-{parts}", $"Split in {parts}", $"{count} equal parts, cut without re-encoding if possible", Section.Video, "Splitting…", "Split") with
        {
            Parameters = new Dictionary<string, string> { ["parts"] = parts.ToString(CultureInfo.InvariantCulture) },
        };

    static Target Speed(string factor, string description) =>
        Tool($"speed-{factor}x", $"{factor}× speed", description, Section.Video, "Changing speed…", "Speed changed") with
        {
            Parameters = new Dictionary<string, string> { ["factor"] = factor },
        };

    static readonly Target[] AudioTools = [];

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
    /// Cuts the video into equal parts, clip-part1.mp4 and so on. A stream copy is instant and lossless but can only
    /// cut at keyframes; when that leaves parts of clearly different lengths, the video is re-encoded with
    /// keyframes at the cut points instead.
    /// </summary>
    async Task<IReadOnlyList<string>> SplitAsync(string input, MediaInfo media, Target target, JobContext ctx)
    {
        var parts = (int)target.Number("parts", 2, 100);
        if (media.Duration <= 0)
            throw new ConversionException("Couldn't tell how long this video is.");
        if (media.Duration < parts)
            throw new ConversionException($"This video is too short to split into {parts} parts.");

        var length = media.Duration / parts;
        string Times(double shift) => string.Join(',', Enumerable.Range(1, parts - 1)
            .Select(i => (length * i - shift).ToString("0.###", CultureInfo.InvariantCulture)));
        string[] map = ["-map", "0:v:0", "-map", "0:a?", "-sn"];
        string[] Segment(string times) => ["-f", "segment", "-segment_times", times, "-reset_timestamps", "1"];
        var key = Formats.Key(input);
        var encodeKey = Ffmpeg.CanEncodeVideo(key) ? key : "mp4";

        using var temp = OutputPaths.CreateTemp();
        var copied = await CutAsync(Formats.Ext(input), [.. map, "-c", "copy", .. Segment(Times(0))], new ActionProgress<double>(f => ctx.Progress.Report(f * 0.1)));
        var pieces = copied is not null && await EvenAsync(copied) ? copied : null;
        if (pieces is null)
        {
            foreach (var file in Directory.GetFiles(temp.Path)) File.Delete(file);
            // The muxer sees timestamps slightly shifted from the encoder's, so it could miss a keyframe forced
            // exactly at the cut and run on to the next one. Each part starts a moment earlier to catch it.
            pieces = await CutAsync(encodeKey, [.. map, .. Ffmpeg.VideoEncoder(encodeKey), "-force_key_frames", Times(0), .. Segment(Times(0.2))],
                new ActionProgress<double>(f => ctx.Progress.Report(0.1 + f * 0.9)));
            if (pieces is null || !await EvenAsync(pieces))
                throw new ConversionException("ffmpeg couldn't split this video evenly.");
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

        // A keyframe cut lands up to one keyframe interval (typically 2 to 10 seconds) after the requested point.
        // That's accepted within a tenth of a part, at least 2 and at most 10 seconds, but never a quarter of a part.
        async Task<bool> EvenAsync(List<string> files)
        {
            if (files.Count != parts) return false;
            var tolerance = Math.Min(Math.Clamp(length / 10, 2, 10), length / 4);
            foreach (var file in files)
            {
                var piece = await Ffmpeg.ProbeAsync(tools, file, ctx.Cancellation);
                if (Math.Abs(piece.Duration - length) > tolerance) return false;
            }
            return true;
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
    static string[] FastStart(string path) => Formats.Key(path) is "mp4" or "m4v" or "mov" ? ["-movflags", "+faststart"] : [];
}

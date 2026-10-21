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
    ];

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

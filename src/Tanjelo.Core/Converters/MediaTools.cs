namespace Tanjelo.Core.Converters;

/// <summary>Audio and video tools through ffmpeg. Each writes a changed copy beside the original, named after the change.</summary>
public sealed class MediaTools(Toolbox tools) : IConverter
{
    static Target Tool(string key, string label, string description, Section section, string running, string done) =>
        new(key, label, description, section) { Kind = TargetKind.Tool, Running = running, Done = done };

    static readonly Target[] VideoTools =
    [
        Tool("mute", "Mute", "Remove the sound; the picture isn't re-encoded", Section.Video, "Muting…", "Muted"),
    ];

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

    /// <summary>Moves an MP4's index to the front so it starts playing before it has fully downloaded.</summary>
    static string[] FastStart(string path) => Formats.Key(path) is "mp4" or "m4v" or "mov" ? ["-movflags", "+faststart"] : [];
}

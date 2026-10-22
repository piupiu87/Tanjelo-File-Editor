namespace Tanjelo.Core.Converters;

/// <summary>
/// Tools that work at moments you pick in a video, such as saving those frames as pictures. The options dialog
/// shows a scrubber over the video, with the frame under it as the preview.
/// </summary>
public sealed class VideoMomentsTool(Toolbox tools) : IConverter, IPreviewer
{
    static readonly Target Frames = new Target("save-frames", "Save frames…", "Chosen moments as full-size PNGs", Section.Video)
    {
        Kind = TargetKind.Tool,
        Running = "Saving frames…",
        Done = "Frames saved",
        Confirm = "Save frames",
    }.WithOptions(new Option("times", "Moments", OptionKind.Times, "")
    {
        Remember = false,
        Hint = "Move the slider to a moment and press Add, or type times such as 0:05, 1:12.5.",
    });

    public IEnumerable<Target> TargetsFor(string input) =>
        tools.Ffmpeg is not null && tools.Ffprobe is not null && Formats.Videos.Contains(Formats.Key(input)) ? [Frames] : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        if (media.Video is null || media.Duration <= 0)
            throw new ConversionException("Couldn't find a picture and a length in this video.");
        var times = target.Times("times");
        if (times[^1] >= media.Duration) // times come sorted
            throw new ConversionException($"{OptionValues.FormatTime(times[^1])} is past the end of this video, which is {OptionValues.FormatTime(media.Duration)} long.");

        var outputs = new List<string>();
        try
        {
            for (var i = 0; i < times.Count; i++)
            {
                var frame = await Ffmpeg.FrameAsync(tools, input, times[i], 0, ctx.Cancellation);
                var output = OutputPaths.Suffixed(input, FileTime(times[i]), "png");
                await File.WriteAllBytesAsync(output, frame, ctx.Cancellation);
                outputs.Add(output);
                ctx.Progress.Report((i + 1.0) / times.Count);
            }
        }
        catch
        {
            outputs.ForEach(OutputPaths.TryDelete);
            throw;
        }
        return outputs;
    }

    /// <summary>"1m05.5s" for 65.5 seconds, "1h02m03s" past an hour: a time that fits in a file name and sorts well.</summary>
    internal static string FileTime(double seconds)
    {
        var text = OptionValues.FormatTime(seconds);
        var parts = text.Split(':');
        return parts.Length == 3 ? $"{parts[0]}h{parts[1]}m{parts[2]}s" : $"{parts[0]}m{parts[1]}s";
    }

    public async Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, inputs[0], ctx.Cancellation);
        if (media.Video is null || media.Duration <= 0)
            throw new ConversionException("Couldn't find a picture and a length in this video.");
        // Read loosely: the scrubber is useful before any moment has been added.
        OptionValues.TryTimes(target.Parameters["times"], out var times);
        var at = Math.Clamp(request.At ?? (times.Count > 0 ? times[0] : 0), 0, Math.Max(0, media.Duration - 0.05));
        var frame = await Ffmpeg.FrameAsync(tools, inputs[0], at, request.MaxSize, ctx.Cancellation);

        return new Preview(frame,
        [
            new("Showing", $"{OptionValues.FormatTime(at)} of {Describe.Length(media.Duration)}"),
            times.Count == 0
                ? new Fact("Frames", "None yet")
                : new Fact("Frames", $"{times.Count} PNG{(times.Count == 1 ? "" : "s")}, {Describe.Pixels(media.Width, media.Height)}"),
        ]) { Duration = media.Duration };
    }
}

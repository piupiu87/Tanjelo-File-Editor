namespace Tanjelo.Core.Converters;

/// <summary>
/// Tools that work at moments you pick in a video: save those frames as pictures, or split the video there. The
/// options dialog shows a scrubber over the video, with the frame under it as the preview.
/// </summary>
public sealed class VideoMomentsTool(Toolbox tools) : IConverter, IPreviewer
{
    static readonly Target Frames = new Target("save-frames", "Save frames…", "Moments as full-size PNGs", Section.Video)
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

    static readonly Target SplitAt = new Target("split-video-at", "Split at…", "At the moments you choose", Section.Video)
    {
        Kind = TargetKind.Tool,
        Running = "Splitting…",
        Done = "Split",
        Confirm = "Split",
    }.WithOptions(new Option("times", "Split at", OptionKind.Times, "")
    {
        Remember = false,
        Hint = "Each moment starts a new part. Move the slider and press Add, or type times such as 0:30, 1:45.",
    });

    /// <summary>Parts shorter than this are almost certainly a slip of the slider.</summary>
    const double ShortestPart = 0.5;

    public IEnumerable<Target> TargetsFor(string input) =>
        tools.Ffmpeg is not null && tools.Ffprobe is not null && Formats.Videos.Contains(Formats.Key(input)) ? [Frames, SplitAt] : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        if (media.Video is null || media.Duration <= 0)
            throw new ConversionException("Couldn't find a picture and a length in this video.");
        var times = target.Times("times");
        if (times[^1] >= media.Duration) // times come sorted
            throw new ConversionException($"{OptionValues.FormatTime(times[^1])} is past the end of this video, which is {OptionValues.FormatTime(media.Duration)} long.");

        if (target.Key == "save-frames")
        {
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

        var bounds = times.Prepend(0).Append(media.Duration).ToList();
        if (bounds.Zip(bounds.Skip(1)).Any(p => p.Second - p.First < ShortestPart))
            throw new ConversionException("Every part needs to be at least half a second long.");
        // Chosen moments should be met to the frame, so a lossless keyframe cut has to land within a tenth of a second.
        return await MediaTools.CutAtAsync(tools, input, media, times, 0.1, "ffmpeg couldn't split this video at those moments.", ctx);
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

        var facts = new List<Fact> { new("Showing", $"{OptionValues.FormatTime(at)} of {Describe.Length(media.Duration)}") };
        if (target.Key == "save-frames")
        {
            facts.Add(times.Count == 0
                ? new Fact("Frames", "None yet")
                : new Fact("Frames", $"{times.Count} PNG{(times.Count == 1 ? "" : "s")}, {Describe.Pixels(media.Width, media.Height)}"));
        }
        else
        {
            var bounds = times.Where(t => t > 0 && t < media.Duration).Prepend(0).Append(media.Duration).ToList();
            facts.AddRange(bounds.Count == 2
                ? [new Fact("Parts", "Add a moment to split at")]
                : bounds.Zip(bounds.Skip(1)).Select((p, i) => new Fact($"Part {i + 1}",
                    $"{OptionValues.FormatTime(p.First)} to {OptionValues.FormatTime(p.Second)} · {OptionValues.FormatTime(p.Second - p.First)} long")));
        }
        return new Preview(frame, facts) { Duration = media.Duration };
    }
}

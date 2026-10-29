using System.Globalization;
using System.Text;
using ImageMagick;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Joins clips one after another into one MP4. Clips that match (same codec, size, frame rate, sound) are joined
/// without re-encoding; otherwise every clip is fitted to one size and frame rate and encoded once, with silence
/// for clips that have no sound.
/// </summary>
public sealed class JoinVideosOperation(Toolbox tools) : IBatchOperation, IPreviewer
{
    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        if (tools.Ffmpeg is null || tools.Ffprobe is null) yield break;
        if (inputs.Count < 2 || !inputs.All(i => Formats.KindOf(i) == FileKind.Video)) yield break;
        yield return new Target("join-videos", Strings.JoinVideos, string.Format(Strings.ClipsIntoOneMP4, inputs.Count), Section.Video)
        {
            IsBatch = true,
            Kind = TargetKind.Tool,
            Running = Strings.Joining,
            Done = Strings.JoinedIntoOneVideo,
            Confirm = Strings.Join,
        }.WithOptions(
            new Option("order", Strings.Order, OptionKind.Order, "") { Remember = false },
            new Option("size", Strings.PictureSize, OptionKind.Choice, "first")
            {
                Choices = [new("first", Strings.LikeTheFirstClip), new("largest", Strings.LikeTheLargest), new("1080", "1080p"), new("720", "720p")],
            },
            new Option("fit", Strings.ClipsOfAnotherShape, OptionKind.Choice, "pad")
            {
                Choices = [new("pad", Strings.AddBlackBars), new("crop", Strings.FillAndCrop)],
            });
    }

    internal sealed record Plan(bool Copy, int Width, int Height, double FrameRate, string Why);

    /// <summary>Whether the clips can be joined as they are, and if not, the size and frame rate they're encoded at.</summary>
    /// <param name="containers">Each clip's file format; ffmpeg only joins clips losslessly from the same kind of file.</param>
    internal static Plan PlanFor(IReadOnlyList<MediaInfo> clips, IReadOnlyList<string> containers, Target target)
    {
        if (clips.Any(c => c.Video is null || c.Width <= 0))
            throw new ConversionException(Strings.EveryFileNeedsAPictureToBe);
        var first = clips[0];
        static int Even(double size) => Math.Max(2, (int)Math.Round(size / 2) * 2);
        var (width, height) = target.Choice("size") switch
        {
            "largest" => clips.Select(c => (c.Width, c.Height)).MaxBy(s => s.Width * s.Height),
            "1080" or "720" => Scaled(int.Parse(target.Choice("size"), CultureInfo.InvariantCulture)),
            _ => (first.Width, first.Height),
        };
        (width, height) = (Even(width), Even(height));
        var frameRate = first.FrameRate is > 0 and <= 120 ? first.FrameRate : 30;

        string? differs = null;
        if (clips.Any(c => c.Video != first.Video) || first.Video is not ("h264" or "hevc")) differs = Strings.TheirVideoFormatsDiffer;
        else if (clips.Any(c => c.Width != first.Width || c.Height != first.Height || c.Rotation != first.Rotation)) differs = Strings.TheirSizesDiffer;
        else if (width != first.Width || height != first.Height) differs = Strings.OfTheNewSize;
        else if (clips.Any(c => Math.Abs(c.FrameRate - first.FrameRate) > 0.01 || c.PixelFormat != first.PixelFormat)) differs = Strings.TheirFrameRatesDiffer;
        else if (clips.Any(c => c.Audio != first.Audio || c.SampleRate != first.SampleRate || c.Channels != first.Channels) || first.Audio is not (null or "aac"))
            differs = Strings.TheirSoundDiffers;
        else if (containers.Distinct().Count() > 1) differs = Strings.TheyReDifferentKindsOfFile;
        return differs is null
            ? new Plan(true, width, height, frameRate, Strings.AsTheyAreWithoutReEncoding)
            : new Plan(false, width, height, frameRate, string.Format(CultureInfo.InvariantCulture, Strings.ReEncodedToAtFpsBecause, Describe.Pixels(width, height), frameRate, differs));

        // 1080p and 720p name the short side, so portrait clips stay portrait.
        (int, int) Scaled(int shortSide)
        {
            var scale = (double)shortSide / Math.Min(first.Width, first.Height);
            return ((int)(first.Width * scale), (int)(first.Height * scale));
        }
    }

    public async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx)
    {
        var clips = target.Ordered("order", inputs);
        var media = new List<MediaInfo>();
        foreach (var clip in clips)
            media.Add(await ProbeAsync(clip, ctx.Cancellation));
        var plan = PlanFor(media, clips.Select(Formats.Key).ToList(), target);
        var total = media.Sum(m => m.Duration);
        var output = OutputPaths.Unique(OutputPaths.DirectoryOf(inputs[0]), "Joined", "mp4");
        try
        {
            if (plan.Copy)
            {
                // The concat demuxer reads a list of files and copies their streams end to end. It can drop a clip
                // without failing when the clips aren't quite alike, so the result's length is checked.
                using var temp = OutputPaths.CreateTemp();
                var list = Path.Combine(temp.Path, "clips.txt");
                await File.WriteAllTextAsync(list, string.Concat(clips.Select(c => $"file '{c.Replace("'", @"'\''")}'\n")), new UTF8Encoding(false), ctx.Cancellation);
                string[] tag = media[0].Video == "hevc" ? ["-tag:v", "hvc1"] : [];
                await Ffmpeg.RunRawAsync(tools, ["-f", "concat", "-safe", "0", "-i", list, "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", .. tag,
                    "-movflags", "+faststart", output], total, new ActionProgress<double>(f => ctx.Progress.Report(f / 2)), ctx.Cancellation);
                if (Math.Abs((await Ffmpeg.ProbeAsync(tools, output, ctx.Cancellation)).Duration - total) <= 0.5)
                    return [output];
                plan = plan with { Copy = false };
            }
            string[] encode = [.. clips.SelectMany(c => new[] { "-i", c }), "-filter_complex", Graph(media, plan, target.Choice("fit")),
                "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart", output];
            await Ffmpeg.RunRawAsync(tools, encode, total, ctx.Progress, ctx.Cancellation);
            return [output];
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
    }

    /// <summary>
    /// Each clip fitted to the plan's size and frame rate, with its sound (or silence as long as it) in one format,
    /// then all joined by the concat filter.
    /// </summary>
    static string Graph(IReadOnlyList<MediaInfo> clips, Plan plan, string fit)
    {
        var (w, h) = (plan.Width, plan.Height);
        var shape = fit == "crop"
            ? $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}"
            : $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black";
        var rate = plan.FrameRate.ToString("0.###", CultureInfo.InvariantCulture);
        var graph = new StringBuilder();
        for (var i = 0; i < clips.Count; i++)
        {
            graph.Append($"[{i}:v:0]{shape},setsar=1,fps={rate},format=yuv420p[v{i}];");
            graph.Append(clips[i].Audio is null
                ? $"aevalsrc=0|0:c=stereo:s=48000:d={Ffmpeg.Time(clips[i].Duration)}[a{i}];"
                : $"[{i}:a:0]aresample=48000,aformat=channel_layouts=stereo,apad=whole_dur={Ffmpeg.Time(clips[i].Duration)},atrim=end={Ffmpeg.Time(clips[i].Duration)}[a{i}];");
        }
        graph.Append(string.Concat(Enumerable.Range(0, clips.Count).Select(i => $"[v{i}][a{i}]")));
        graph.Append($"concat=n={clips.Count}:v=1:a=1[v][a]");
        return graph.ToString();
    }

    async Task<MediaInfo> ProbeAsync(string clip, CancellationToken ct)
    {
        try
        {
            return await Ffmpeg.ProbeAsync(tools, clip, ct);
        }
        catch (ConversionException ex)
        {
            throw new ConversionException($"{Path.GetFileName(clip)}: {ex.Message}", ex);
        }
    }

    /// <summary>The clips in order as a strip of first frames, with how each will be joined.</summary>
    public async Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx)
    {
        var clips = target.Ordered("order", inputs);
        var media = new List<MediaInfo>();
        foreach (var clip in clips)
            media.Add(await ProbeAsync(clip, ctx.Cancellation));
        var plan = PlanFor(media, clips.Select(Formats.Key).ToList(), target);

        // A strip of the clips' opening frames, all the same height, made to fit the preview.
        const int MaxShown = 6;
        var shown = Math.Min(clips.Count, MaxShown);
        var height = (uint)Math.Max(16, request.MaxSize / 3);
        using var strip = new MagickImageCollection();
        for (var i = 0; i < shown; i++)
        {
            var frame = await Ffmpeg.FrameAsync(tools, clips[i], Math.Min(1, media[i].Duration / 2), (int)height * 2, ctx.Cancellation);
            var picture = new MagickImage(frame);
            picture.Resize(new MagickGeometry(height * 4, height) { IgnoreAspectRatio = false });
            picture.BorderColor = MagickColors.Transparent;
            picture.Border(3);
            strip.Add(picture);
        }
        using var row = strip.AppendHorizontally();
        if (row.Width > request.MaxSize) row.Resize((uint)request.MaxSize, (uint)request.MaxSize);

        var facts = new List<Fact>
        {
            new(Strings.Length, string.Format(Strings.InAll, Describe.Length(media.Sum(m => m.Duration)))),
            new(Strings.Joined, plan.Why),
        };
        facts.AddRange(clips.Take(MaxShown).Select((clip, i) =>
            new Fact($"{i + 1}.", $"{Path.GetFileName(clip)} · {Describe.Length(media[i].Duration)} · {Describe.Pixels(media[i].Width, media[i].Height)}")));
        if (clips.Count > MaxShown) facts.Add(new Fact("", string.Format(Strings.AndMore, clips.Count - MaxShown)));
        return new Preview(row.ToByteArray(MagickFormat.Png), facts);
    }
}

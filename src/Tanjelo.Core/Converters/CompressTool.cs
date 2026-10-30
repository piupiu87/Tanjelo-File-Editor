using System.Globalization;
using ImageMagick;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Makes images, video and audio smaller: to a percentage of their size or to a target size, optionally at smaller
/// dimensions too. Images get the best quality that fits; video and audio get the bit rate that fits, video in two
/// passes so the size lands close to the target.
/// </summary>
public sealed class CompressTool(Toolbox tools) : IConverter, IPreviewer
{
    const long Megabyte = 1024 * 1024;

    /// <summary>Images that can be re-encoded smaller. Animated GIFs aren't, and SVG and ICO aren't photos.</summary>
    static readonly HashSet<string> Images = ["jpg", "png", "webp", "avif", "heic", "bmp", "tiff"];

    static Target Compress(Section section, bool dimensions) =>
        new Target("compress", Strings.Compress, Strings.ToASizeOrAPercentage, section)
        {
            Kind = TargetKind.Tool,
            Running = Strings.Compressing,
            Done = Strings.Compressed,
        }.WithOptions(
        [
            new("mode", Strings.CompressTo, OptionKind.Choice, "percent") { Choices = [new("percent", Strings.Percentage), new("size", Strings.TargetSize)] },
            new("percent", Strings.PercentageOfTheOriginalSize, OptionKind.Number, "50")
            {
                Min = 5, Max = 95, Whole = true, Unit = "%", Slider = true, ShownWhen = new("mode", "percent"),
            },
            new("size", Strings.TargetSize, OptionKind.Number, "1")
            {
                Min = 0.01, Max = 100_000, Unit = Strings.MB, ShownWhen = new("mode", "size"), Hint = Strings.EachFileEndsUpAtMostThis,
            },
            .. dimensions
                ? new[]
                {
                    new Option("scale", Strings.Dimensions, OptionKind.Number, "100")
                    {
                        Min = 10, Max = 100, Whole = true, Unit = "%", Slider = true,
                        Hint = Strings.N100KeepsTheWidthAndHeightSmaller,
                    },
                }
                : [],
        ]);

    static readonly Target ImageTarget = Compress(Section.Image, dimensions: true);
    static readonly Target VideoTarget = Compress(Section.Video, dimensions: true);
    static readonly Target AudioTarget = Compress(Section.Audio, dimensions: false);

    public IEnumerable<Target> TargetsFor(string input)
    {
        var key = Formats.Key(input);
        if (Images.Contains(key)) return [ImageTarget];
        if (tools.Ffmpeg is null || tools.Ffprobe is null) return [];
        if (Formats.Videos.Contains(key)) return [VideoTarget];
        if (Formats.Audio.Contains(key)) return [AudioTarget];
        return [];
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var original = new FileInfo(input).Length;
        if (Images.Contains(Formats.Key(input)))
        {
            var image = await Task.Run(() => ShrinkImage(input, Goal(target, original), Scale(target), ctx.Cancellation), ctx.Cancellation);
            var output = OutputPaths.Suffixed(input, "compressed", image.Ext);
            await File.WriteAllBytesAsync(output, image.Bytes, ctx.Cancellation);
            ctx.Progress.Report(1);
            return [output];
        }

        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        return Formats.Videos.Contains(Formats.Key(input))
            ? [await VideoAsync(input, media, PlanVideo(media, original, target), ctx)]
            : [await AudioAsync(input, media, PlanAudio(input, media, original, target), ctx)];
    }

    public async Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx)
    {
        var input = inputs[0];
        var original = new FileInfo(input).Length;
        if (Images.Contains(Formats.Key(input)))
        {
            var before = new MagickImageInfo(input);
            var result = await Task.Run(() => ShrinkImage(input, Goal(target, original), Scale(target), ctx.Cancellation), ctx.Cancellation);
            using var picture = new MagickImage(result.Bytes);
            if (picture.Width > request.MaxSize || picture.Height > request.MaxSize)
                picture.Resize((uint)request.MaxSize, (uint)request.MaxSize);
            return new Preview(picture.ToByteArray(MagickFormat.Png),
            [
                new(Strings.Now, $"{Describe.Size(original)} · {Describe.Pixels((int)before.Width, (int)before.Height)}"),
                new(Strings.After, $"{Describe.Size(result.Bytes.Length)} · {Describe.Pixels(result.Width, result.Height)} ({Percent(result.Bytes.Length, original)})"),
                new(Strings.SavedAs, $"{result.Ext.ToUpperInvariant()}, {result.How}"),
            ]);
        }

        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        var now = new Fact(Strings.Now, $"{Describe.Size(original)} · {Describe.Length(media.Duration)}" + (media.Bitrate > 0 ? $" · {Describe.Rate(media.Bitrate)}" : ""));
        if (Formats.Videos.Contains(Formats.Key(input)))
        {
            var plan = PlanVideo(media, original, target);
            var frame = await Ffmpeg.FrameAsync(tools, input, media.Duration / 3, request.MaxSize, ctx.Cancellation, plan.Filter);
            return new Preview(frame,
            [
                now,
                new(Strings.After, string.Format(Strings.AboutMP4, Describe.Size(plan.Goal), Percent(plan.Goal, original))),
                new(Strings.Video, $"{Describe.Rate(plan.VideoKbps * 1000)}" + (plan.Width > 0 ? $" · {Describe.Pixels(plan.Width, plan.Height)}" : "")),
                new(Strings.Sound, plan.AudioKbps > 0 ? string.Format(Strings.AAC, Describe.Rate(plan.AudioKbps * 1000)) : Strings.None),
            ]);
        }
        var audio = PlanAudio(input, media, original, target);
        var estimate = (long)(audio.Kbps * 1000 / 8.0 * media.Duration);
        return new Preview(null,
        [
            now,
            new(Strings.After, string.Format(Strings.About, Describe.Size(estimate), Percent(estimate, original))),
            new(Strings.SavedAs, $"{audio.Ext.ToUpperInvariant()}, {Describe.Rate(audio.Kbps * 1000)}"),
        ]);
    }

    /// <summary>The size each file should end up at most, in bytes.</summary>
    static long Goal(Target target, long original)
    {
        var goal = target.Choice("mode") == "size"
            ? (long)(target.Number("size") * Megabyte)
            : (long)(original * target.Number("percent") / 100);
        if (goal >= original)
            throw new ConversionException(string.Format(Strings.ItSAlreadyWhichIsUnderThe, Describe.Size(original)));
        return goal;
    }

    static double Scale(Target target) => target.Options.Any(o => o.Name == "scale") ? target.Number("scale") / 100 : 1;

    static string Percent(long size, long original) => $"{Math.Max(1, Math.Round(100.0 * size / original)):0}%";

    internal sealed record ImageResult(byte[] Bytes, string Ext, int Width, int Height, string How);

    /// <summary>
    /// Re-encodes the picture at the best quality that fits in <paramref name="goal"/> bytes. When even the lowest
    /// quality is too big, the picture is made smaller by about the right amount and tried again.
    /// </summary>
    internal static ImageResult ShrinkImage(string input, long goal, double scale, CancellationToken ct)
    {
        using var frames = ImageConverter.Read(input);
        using var image = frames[0].Clone();
        image.AutoOrient();
        if (image.GetExifProfile() is { } exif)
        {
            exif.RemoveThumbnail(); // a preview copy inside the file, often tens of KB
            image.SetProfile(exif);
        }
        if (scale < 1)
            image.Resize(new Percentage(scale * 100));

        var key = Formats.Key(input) is "jpg" or "png" or "webp" or "avif" ? Formats.Key(input) : image.HasAlpha ? "png" : "jpg";
        if (key == "jpg" && image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var (bytes, how) = key == "png" ? Png(image, goal) : Lossy(image, key, goal, ct);
            if (bytes.Length <= goal)
                return new ImageResult(bytes, key, (int)image.Width, (int)image.Height, how);
            if (attempt == 10 || Math.Min(image.Width, image.Height) <= 16)
                throw new ConversionException(string.Format(Strings.CouldnTGetThisImageDownTo, Describe.Size(goal)));
            var factor = Math.Clamp(Math.Sqrt((double)goal / bytes.Length) * 0.95, 0.3, 0.9);
            image.Resize(new Percentage(factor * 100));
        }
    }

    /// <summary>The highest quality from 5 to 95 that fits, found by halving the range; the lowest if none does.</summary>
    static (byte[] Bytes, string How) Lossy(IMagickImage<byte> image, string key, long goal, CancellationToken ct)
    {
        var format = key switch { "jpg" => MagickFormat.Jpeg, "webp" => MagickFormat.WebP, _ => MagickFormat.Avif };
        byte[]? best = null;
        int low = 5, high = 95, quality = 0;
        while (low <= high)
        {
            ct.ThrowIfCancellationRequested();
            var q = (low + high) / 2;
            image.Quality = (uint)q;
            var bytes = image.ToByteArray(format);
            if (bytes.Length <= goal)
            {
                (best, quality) = (bytes, q);
                low = q + 1;
            }
            else
            {
                high = q - 1;
            }
        }
        if (best is not null) return (best, string.Format(Strings.Quality, quality));
        image.Quality = 5;
        return (image.ToByteArray(format), Strings.Quality5);
    }

    /// <summary>PNG is lossless, so it shrinks by packing tighter, then by using at most 256 colours.</summary>
    static (byte[] Bytes, string How) Png(IMagickImage<byte> image, long goal)
    {
        image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        var packed = image.ToByteArray(MagickFormat.Png);
        if (packed.Length <= goal) return (packed, Strings.AllColours);
        using var reduced = image.Clone();
        reduced.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
        reduced.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        return (reduced.ToByteArray(MagickFormat.Png), Strings.N256Colours);
    }

    internal sealed record VideoPlan(long Goal, int VideoKbps, int AudioKbps, int Width, int Height, string Filter);

    /// <summary>The bit rates that make the video come out at its goal size, leaving a little for the container.</summary>
    static VideoPlan PlanVideo(MediaInfo media, long original, Target target)
    {
        if (media.Duration <= 0)
            throw new ConversionException(Strings.CouldnTTellHowLongThisVideo);
        var goal = Goal(target, original);
        var total = Math.Max(0, goal - 16384) * 8 / media.Duration / 1000 * 0.96;
        var audio = media.Audio is null ? 0 : total >= 1500 ? 160 : total >= 400 ? 128 : total >= 200 ? 96 : 64;
        var video = (int)(total - audio);
        if (video < 50)
        {
            var least = (long)((50 + audio) * 1000 / 8.0 * media.Duration / 0.96) + 16384;
            throw new ConversionException(string.Format(Strings.IsTooSmallForOfVideoIt, Describe.Size(goal), Describe.Length(media.Duration), Describe.Size(least)));
        }
        var scale = Scale(target);
        var text = scale.ToString("0.###", CultureInfo.InvariantCulture);
        static int Even(double size) => (int)(size / 2) * 2;
        return new VideoPlan(goal, video, audio, Even(media.Width * scale), Even(media.Height * scale), $"scale=trunc(iw*{text}/2)*2:trunc(ih*{text}/2)*2");
    }

    async Task<string> VideoAsync(string input, MediaInfo media, VideoPlan plan, JobContext ctx)
    {
        var output = OutputPaths.Suffixed(input, "compressed", "mp4");
        using var temp = OutputPaths.CreateTemp();
        string[] Video(int kbps) => ["-map", "0:v:0", "-c:v", "libx264", "-preset", "medium", "-b:v", $"{kbps}k", "-pix_fmt", "yuv420p",
            "-vf", plan.Filter, "-passlogfile", Path.Combine(temp.Path, "pass")];
        string[] audio = plan.AudioKbps > 0 ? ["-map", "0:a:0", "-c:a", "aac", "-b:a", $"{plan.AudioKbps}k"] : ["-an"];
        // The first pass only measures how hard each part of the video is to encode; the second spends the bits accordingly.
        await Ffmpeg.RunAsync(tools, input, "-", [.. Video(plan.VideoKbps), "-pass", "1", "-an", "-f", "null"], media.Duration,
            new ActionProgress<double>(f => ctx.Progress.Report(f / 2)), ctx.Cancellation);

        // At very low rates x264 can overshoot by several percent. The first pass's measurements still hold, so a
        // second pass at a lower rate fixes that without starting over.
        var kbps = plan.VideoKbps;
        for (var attempt = 0; ; attempt++)
        {
            await Ffmpeg.RunFirstWorkingAsync(tools, input, output, [[.. Video(kbps), "-pass", "2", .. audio, "-sn", "-movflags", "+faststart"]], media.Duration, ctx,
                new ActionProgress<double>(f => ctx.Progress.Report(0.5 + f / 2)));
            var size = new FileInfo(output).Length;
            if (size <= plan.Goal) return output;
            var over = (size - plan.Goal) * 8 / media.Duration / 1000;
            kbps = (int)((kbps - over) * 0.97);
            if (attempt == 2 || kbps < 50)
            {
                OutputPaths.TryDelete(output);
                throw new ConversionException(string.Format(Strings.CouldnTGetThisVideoDownTo, Describe.Size(plan.Goal), Describe.Size(size)));
            }
        }
    }

    internal sealed record AudioPlan(string Ext, int Kbps, string[] Encoder);

    /// <summary>Lossy formats stay as they are; lossless ones (WAV, FLAC, AIFF) become MP3.</summary>
    static AudioPlan PlanAudio(string input, MediaInfo media, long original, Target target)
    {
        if (media.Duration <= 0)
            throw new ConversionException(Strings.CouldnTTellHowLongThisFile);
        var goal = Goal(target, original);
        var key = Formats.Key(input) is "mp3" or "m4a" or "aac" or "ogg" or "opus" or "wma" ? Formats.Key(input) : "mp3";
        var (codec, least, most) = key switch
        {
            "m4a" or "aac" => ("aac", 24, 320),
            "ogg" => ("libvorbis", 48, 320),
            "opus" => ("libopus", 8, 256),
            "wma" => ("wmav2", 32, 192),
            _ => ("libmp3lame", 32, 320),
        };
        // A few KB go to the container and tags whatever the length, and encoders overshoot a little.
        var kbps = (int)Math.Min(most, Math.Max(0, goal - 4096) * 8 / media.Duration / 1000 * 0.97);
        // MP3 only has a few bit rates and libmp3lame rounds to the nearest one, which could be over the target.
        if (key == "mp3")
            kbps = new[] { 320, 256, 224, 192, 160, 128, 112, 96, 80, 64, 56, 48, 40, 32 }.FirstOrDefault(r => r <= kbps);
        if (kbps < least)
        {
            var size = (long)(least * 1000 / 8.0 * media.Duration / 0.97) + 4096;
            throw new ConversionException(string.Format(Strings.IsTooSmallForOfItNeeds, Describe.Size(goal), Describe.Length(media.Duration), key.ToUpperInvariant(), Describe.Size(size)));
        }
        return new AudioPlan(key, kbps, ["-c:a", codec, "-b:a", $"{kbps}k"]);
    }

    async Task<string> AudioAsync(string input, MediaInfo media, AudioPlan plan, JobContext ctx)
    {
        var output = OutputPaths.Suffixed(input, "compressed", plan.Ext);
        // Cover art is left out: it can be a good part of the size.
        await Ffmpeg.RunFirstWorkingAsync(tools, input, output, [["-map", "0:a:0", .. plan.Encoder]], media.Duration, ctx);
        return output;
    }
}

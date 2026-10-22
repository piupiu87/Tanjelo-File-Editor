using System.Globalization;
using ImageMagick;
using ImageMagick.Drawing;

namespace Tanjelo.Core.Converters;

/// <summary>Where a crop goes: by aspect ratio or exact pixels, anchored to a side or corner, or at a given offset.</summary>
public static class Crop
{
    public readonly record struct Box(int X, int Y, int Width, int Height);

    static readonly string[] Ratios = ["1:1", "4:5", "4:3", "3:2", "16:9", "9:16", "3:4", "2:3"];

    static readonly Choice[] Positions =
    [
        new("center", "Centre"), new("top", "Top"), new("bottom", "Bottom"), new("left", "Left"), new("right", "Right"),
        new("top-left", "Top left"), new("top-right", "Top right"), new("bottom-left", "Bottom left"), new("bottom-right", "Bottom right"),
        new("custom", "Exact offset"),
    ];

    /// <summary>The crop settings shared by pictures and video.</summary>
    public static Option[] Options =>
    [
        new("mode", "Crop to", OptionKind.Choice, "aspect") { Choices = [new("aspect", "Aspect ratio"), new("pixels", "Exact size")] },
        new("aspect", "Aspect ratio", OptionKind.Choice, "1:1") { Choices = Ratios.Select(r => new Choice(r, r)).ToArray(), ShownWhen = new("mode", "aspect") },
        new("width", "Width", OptionKind.Number, "1080") { Min = 1, Max = 100_000, Whole = true, Unit = "px", ShownWhen = new("mode", "pixels") },
        new("height", "Height", OptionKind.Number, "1080")
        {
            Min = 1, Max = 100_000, Whole = true, Unit = "px", ShownWhen = new("mode", "pixels"),
            Hint = "Larger than the picture? It's cut to fit.",
        },
        new("position", "Keep", OptionKind.Choice, "center") { Choices = Positions },
        new("x", "From the left", OptionKind.Number, "0") { Min = 0, Max = 100_000, Whole = true, Unit = "px", ShownWhen = new("position", "custom") },
        new("y", "From the top", OptionKind.Number, "0") { Min = 0, Max = 100_000, Whole = true, Unit = "px", ShownWhen = new("position", "custom") },
    ];

    /// <param name="even">Round to even numbers, which video with 4:2:0 colour needs.</param>
    public static Box Compute(int width, int height, Target target, bool even = false)
    {
        int Round(double value) => even ? (int)(value / 2) * 2 : (int)Math.Round(value);
        int w, h;
        if (target.Choice("mode") == "aspect")
        {
            var parts = target.Choice("aspect").Split(':');
            var ratio = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
            (w, h) = (double)width / height > ratio ? (Round(height * ratio), Round(height)) : (Round(width), Round(width / ratio));
        }
        else
        {
            (w, h) = (Round(Math.Min(target.Number("width"), width)), Round(Math.Min(target.Number("height"), height)));
        }
        if (w < 2 || h < 2)
            throw new ConversionException("That leaves too little of the picture.");

        var position = target.Choice("position");
        int x = position switch
        {
            "custom" => (int)Math.Min(target.Number("x"), width - w),
            _ when position.EndsWith("left", StringComparison.Ordinal) => 0,
            _ when position.EndsWith("right", StringComparison.Ordinal) => width - w,
            _ => (width - w) / 2,
        };
        int y = position switch
        {
            "custom" => (int)Math.Min(target.Number("y"), height - h),
            _ when position.StartsWith("top", StringComparison.Ordinal) => 0,
            _ when position.StartsWith("bottom", StringComparison.Ordinal) => height - h,
            _ => (height - h) / 2,
        };
        return even ? new Box(x / 2 * 2, y / 2 * 2, w, h) : new Box(x, y, w, h);
    }

    /// <summary>
    /// For previews: the whole picture made to fit <paramref name="maxSize"/>, darkened outside the crop and with
    /// the crop outlined, so you see what's kept and what goes.
    /// </summary>
    internal static byte[] Outline(IMagickImage<byte> picture, Box box, int fullWidth, int maxSize)
    {
        if (picture.Width > maxSize || picture.Height > maxSize)
            picture.Resize((uint)maxSize, (uint)maxSize);
        var scale = (double)picture.Width / fullWidth;
        int left = (int)Math.Round(box.X * scale), top = (int)Math.Round(box.Y * scale);
        int width = Math.Max(1, (int)Math.Round(box.Width * scale)), height = Math.Max(1, (int)Math.Round(box.Height * scale));

        // A darkened copy of the whole picture, with the part that's kept pasted back at full brightness.
        using var shown = picture.Clone();
        shown.Evaluate(Channels.RGB, EvaluateOperator.Multiply, 0.4);
        using var kept = picture.Clone();
        kept.Crop(new MagickGeometry(left, top, (uint)width, (uint)height));
        kept.ResetPage();
        shown.Composite(kept, left, top, CompositeOperator.Over);
        new Drawables()
            .FillColor(MagickColors.Transparent)
            .StrokeColor(MagickColors.White)
            .StrokeWidth(Math.Max(1.5, picture.Width / 300.0))
            .Rectangle(left, top, left + width - 1, top + height - 1)
            .Draw(shown);
        return shown.ToByteArray(MagickFormat.Png);
    }

    internal static IReadOnlyList<Fact> Facts(Box box, int width, int height) =>
    [
        new("Keeps", $"{Describe.Pixels(box.Width, box.Height)}, from {box.X}, {box.Y}"),
        new("Of", Describe.Pixels(width, height)),
    ];
}

/// <summary>Crops pictures, animations included, after turning them the way they're meant to be seen.</summary>
public sealed class CropImageTool : IConverter, IPreviewer
{
    static readonly Target Entry = new Target("crop", "Crop…", "To an aspect ratio or exact size", Section.Image)
    {
        Kind = TargetKind.Tool,
        Running = "Cropping…",
        Done = "Cropped",
    }.WithOptions(Crop.Options);

    public IEnumerable<Target> TargetsFor(string input) =>
        Formats.Images.Contains(Formats.Key(input)) && Formats.Key(input) is not ("svg" or "ico") ? [Entry] : [];

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var frames = ImageConverter.Read(input);
        var animated = frames.Count > 1 && Formats.Key(input) is "gif" or "webp";
        if (animated) frames.Coalesce();
        var first = frames[0];
        first.AutoOrient();
        var box = Crop.Compute((int)first.Width, (int)first.Height, target);
        var key = ImageConverter.ChangedCopyKey(input, first.HasAlpha);
        var output = OutputPaths.Suffixed(input, "cropped", key);
        try
        {
            foreach (var frame in animated ? frames : frames.Take(1))
            {
                frame.Crop(new MagickGeometry(box.X, box.Y, (uint)box.Width, (uint)box.Height));
                frame.ResetPage();
            }
            if (animated)
            {
                if (key == "gif") frames.Optimize();
                frames.Write(output, ImageConverter.FormatFor(key));
            }
            else
            {
                first.Write(output, ImageConverter.FormatFor(key));
            }
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        ctx.Progress.Report(1);
        return [output];
    }, ctx.Cancellation);

    public Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx) => Task.Run(() =>
    {
        using var frames = ImageConverter.Read(inputs[0]);
        using var picture = frames[0].Clone();
        picture.AutoOrient();
        var (width, height) = ((int)picture.Width, (int)picture.Height);
        var box = Crop.Compute(width, height, target);
        return new Preview(Crop.Outline(picture, box, width, request.MaxSize), Crop.Facts(box, width, height));
    }, ctx.Cancellation);
}

/// <summary>
/// Crops video through ffmpeg, re-encoding the picture and copying the sound. The crop is worked out on the picture
/// as it plays, after any rotation a phone asked for.
/// </summary>
public sealed class CropVideoTool(Toolbox tools) : IConverter, IPreviewer
{
    static readonly Target Entry = new Target("crop", "Crop…", "To an aspect ratio or exact size", Section.Video)
    {
        Kind = TargetKind.Tool,
        Running = "Cropping…",
        Done = "Cropped",
    }.WithOptions(Crop.Options);

    public IEnumerable<Target> TargetsFor(string input) =>
        tools.Ffmpeg is not null && tools.Ffprobe is not null && Formats.Videos.Contains(Formats.Key(input)) ? [Entry] : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        var box = BoxFor(media, target);
        if (box.Width == media.Width && box.Height == media.Height)
            throw new ConversionException("That keeps the whole picture, so there's nothing to crop.");

        var key = Ffmpeg.CanEncodeVideo(Formats.Key(input)) ? Formats.Key(input) : "mp4";
        var output = OutputPaths.Suffixed(input, "cropped", key);
        string[] encode = ["-map", "0:v:0", "-map", "0:a?", "-sn", .. Ffmpeg.VideoEncoder(key, Filter(box)), .. MediaTools.FastStart(output)];
        // The sound is copied when the container is the same; otherwise, or if that fails, it's re-encoded too.
        IEnumerable<string[]> plans = key == Formats.Key(input) ? [[.. encode, "-c:a", "copy"], encode] : [encode];
        await Ffmpeg.RunFirstWorkingAsync(tools, input, output, plans, media.Duration, ctx);
        return [output];
    }

    public async Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, inputs[0], ctx.Cancellation);
        var box = BoxFor(media, target);
        var frame = await Ffmpeg.FrameAsync(tools, inputs[0], media.Duration / 3, 0, ctx.Cancellation);
        using var picture = new MagickImage(frame);
        return new Preview(Crop.Outline(picture, box, media.Width, request.MaxSize), Crop.Facts(box, media.Width, media.Height));
    }

    static Crop.Box BoxFor(MediaInfo media, Target target)
    {
        if (media.Video is null || media.Width <= 0 || media.Height <= 0)
            throw new ConversionException("Couldn't tell how big this video's picture is.");
        return Crop.Compute(media.Width, media.Height, target, even: true);
    }

    static string Filter(Crop.Box box) => $"crop={box.Width}:{box.Height}:{box.X}:{box.Y}";
}

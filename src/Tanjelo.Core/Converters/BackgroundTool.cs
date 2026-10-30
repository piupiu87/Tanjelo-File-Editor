using System.Globalization;
using ImageMagick;
using ImageMagick.Drawing;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Sets a picture on a background of a colour, a gradient or another picture, with space around it, rounded
/// corners and a shadow: the look of a screenshot ready to share. Sizes are shares of the picture, so the result
/// looks the same whatever its resolution.
/// </summary>
public sealed class BackgroundTool : IConverter, IPreviewer
{
    internal const string Pictures = "Pictures|*.png;*.jpg;*.jpeg;*.webp;*.avif;*.heic;*.bmp;*.gif;*.tif;*.tiff|All files|*.*";

    static readonly Target Entry = new Target("background", Strings.AddBackground, Strings.ColourGradientOrPicture, Section.Image)
    {
        Kind = TargetKind.Tool,
        Running = Strings.AddingABackground,
        Done = Strings.BackgroundAdded,
        Confirm = Strings.AddBackground2,
    }.WithOptions(
        new Option("fill", Strings.Background, OptionKind.Choice, "gradient") { Choices = [new("colour", Strings.Colour), new("gradient", Strings.Gradient), new("image", Strings.Picture)] },
        new Option("colour", Strings.Colour, OptionKind.Colour, "#F3EFEA") { ShownWhen = new("fill", "colour", "gradient") },
        new Option("colour2", Strings.FadingTo, OptionKind.Colour, "#F97A3D") { ShownWhen = new("fill", "gradient") },
        new Option("direction", Strings.Direction, OptionKind.Choice, "diagonal")
        {
            Choices = [new("down", Strings.TopToBottom), new("right", Strings.LeftToRight), new("diagonal", Strings.Diagonal)],
            ShownWhen = new("fill", "gradient"),
        },
        new Option("image", Strings.Picture, OptionKind.File, "") { FileFilter = Pictures, ShownWhen = new("fill", "image"), Hint = Strings.FillsTheBackgroundCutToFit },
        new Option("padding", Strings.SpaceAround, OptionKind.Number, "8") { Min = 0, Max = 50, Whole = true, Unit = "%", Slider = true },
        new Option("radius", Strings.RoundedCorners, OptionKind.Number, "3") { Min = 0, Max = 50, Whole = true, Unit = "%", Slider = true },
        new Option("shadow", Strings.Shadow, OptionKind.Choice, "soft") { Choices = [new("none", Strings.None), new("soft", Strings.Soft), new("strong", Strings.Strong)] },
        new Option("shape", Strings.Shape, OptionKind.Choice, "fit")
        {
            Choices = [new("fit", Strings.FitThePicture), new("1:1", "1:1"), new("4:5", "4:5"), new("16:9", "16:9"), new("9:16", "9:16")],
            Hint = Strings.FitKeepsTheSameSpaceOnEvery,
        });

    public IEnumerable<Target> TargetsFor(string input) => Formats.Images.Contains(Formats.Key(input)) && Formats.Key(input) != "ico" ? [Entry] : [];

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var frames = ImageConverter.Read(input);
        using var picture = frames[0].Clone();
        picture.AutoOrient();
        using var result = Compose(picture, target);
        // An opaque photo stays a JPG; anything else, or a see-through background, becomes a PNG.
        var key = Formats.Key(input) == "jpg" && !result.HasAlpha ? "jpg" : "png";
        var output = OutputPaths.Suffixed(input, "background", key);
        try
        {
            if (key == "jpg") result.Quality = 92;
            result.Write(output, ImageConverter.FormatFor(key));
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
        // Composed small for speed: every size is a share of the picture, so it looks the same.
        picture.Thumbnail((uint)request.MaxSize, (uint)request.MaxSize);
        using var result = Compose(picture, target);
        result.Thumbnail((uint)request.MaxSize, (uint)request.MaxSize);
        var (canvasWidth, canvasHeight) = Canvas(width, height, target);
        return new Preview(result.ToByteArray(MagickFormat.Png),
        [
            new Fact(Strings.Size, $"{Describe.Pixels(width, height)} → {Describe.Pixels(canvasWidth, canvasHeight)}"),
        ]);
    }, ctx.Cancellation);

    /// <summary>The finished picture's size: the picture plus its space around, widened or heightened to the chosen shape.</summary>
    internal static (int Width, int Height) Canvas(int width, int height, Target target)
    {
        var pad = (int)Math.Round(Math.Max(width, height) * target.Number("padding") / 100);
        int w = width + 2 * pad, h = height + 2 * pad;
        if (target.Choice("shape") is var shape && shape != "fit")
        {
            var parts = shape.Split(':');
            var ratio = double.Parse(parts[0], CultureInfo.InvariantCulture) / double.Parse(parts[1], CultureInfo.InvariantCulture);
            if ((double)w / h < ratio) w = (int)Math.Round(h * ratio);
            else h = (int)Math.Round(w / ratio);
        }
        return (w, h);
    }

    internal static IMagickImage<byte> Compose(IMagickImage<byte> picture, Target target)
    {
        var (width, height) = ((int)picture.Width, (int)picture.Height);
        var (canvasWidth, canvasHeight) = Canvas(width, height, target);
        var canvas = Fill(canvasWidth, canvasHeight, target);
        try
        {
            var x = (canvasWidth - width) / 2;
            var y = (canvasHeight - height) / 2;
            var radius = Math.Min(width, height) * target.Number("radius") / 100;

            using var shaped = picture.Clone();
            if (radius > 0)
            {
                shaped.Alpha(AlphaAction.Set);
                using var mask = new MagickImage(MagickColors.Transparent, (uint)width, (uint)height);
                new Drawables().FillColor(MagickColors.White).RoundRectangle(0, 0, width - 1, height - 1, radius, radius).Draw(mask);
                shaped.Composite(mask, CompositeOperator.DstIn);
            }

            if (target.Choice("shadow") is "soft" or "strong")
            {
                var strong = target.Choice("shadow") == "strong";
                var sigma = Math.Max(1, Math.Max(width, height) * (strong ? 0.03 : 0.02));
                var margin = (int)Math.Ceiling(sigma * 3);
                using var shadow = new MagickImage(MagickColors.Transparent, (uint)(width + 2 * margin), (uint)(height + 2 * margin));
                new Drawables()
                    .FillColor(new MagickColor(0, 0, 0, (byte)(strong ? 150 : 90)))
                    .RoundRectangle(margin, margin, margin + width - 1, margin + height - 1, radius, radius)
                    .Draw(shadow);
                shadow.Blur(0, sigma);
                canvas.Composite(shadow, x - margin, y - margin + (int)(sigma * (strong ? 1 : 0.5)), CompositeOperator.Over);
            }
            canvas.Composite(shaped, x, y, CompositeOperator.Over);
            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    static IMagickImage<byte> Fill(int width, int height, Target target)
    {
        switch (target.Choice("fill"))
        {
            case "gradient":
                var settings = new MagickReadSettings { Width = (uint)width, Height = (uint)height };
                settings.SetDefine("gradient:angle", target.Choice("direction") switch { "down" => "180", "right" => "90", _ => "135" });
                return new MagickImage($"gradient:{Hex(target.Colour("colour"))}-{Hex(target.Colour("colour2"))}", settings);
            case "image":
                var path = target.File("image") ?? throw new ConversionException(Strings.ChooseAPictureForTheBackground);
                using (var frames = ImageConverter.Read(path))
                {
                    var backdrop = frames[0].Clone();
                    backdrop.AutoOrient();
                    // Cover the canvas, then cut what's left over evenly from both sides.
                    backdrop.Resize(new MagickGeometry((uint)width, (uint)height) { FillArea = true });
                    backdrop.Crop(new MagickGeometry((uint)width, (uint)height), Gravity.Center);
                    backdrop.ResetPage();
                    return backdrop;
                }
            default:
                var colour = target.Colour("colour");
                return new MagickImage(new MagickColor(colour.R, colour.G, colour.B, colour.A), (uint)width, (uint)height);
        }
    }

    /// <summary>ImageMagick reads #RRGGBBAA the same way the options write it.</summary>
    static string Hex(Colour colour) => colour.ToString();
}

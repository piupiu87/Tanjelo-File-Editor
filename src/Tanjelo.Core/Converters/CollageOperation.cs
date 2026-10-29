using ImageMagick;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Puts several pictures into one: in a grid, a row, a column, or one large picture with the rest beside it.
/// Each picture is read, turned upright and made small as soon as its place is known, so a big selection doesn't
/// need all of its pictures in memory at full size.
/// </summary>
public sealed class CollageOperation : IBatchOperation, IPreviewer
{
    public readonly record struct Cell(int X, int Y, int Width, int Height);

    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        if (inputs.Count < 2 || !inputs.All(i => Formats.KindOf(i) == FileKind.Image)) yield break;
        yield return new Target("collage", Strings.Collage, string.Format(Strings.PicturesInOne, inputs.Count), Section.Image)
        {
            IsBatch = true,
            Kind = TargetKind.Tool,
            Running = Strings.MakingACollage,
            Done = Strings.CollageMade,
            Confirm = Strings.MakeCollage,
        }.WithOptions(
            new Option("layout", Strings.Layout, OptionKind.Choice, "grid")
            {
                Choices = [new("grid", Strings.Grid), new("row", Strings.Row), new("column", Strings.Column), new("featured", Strings.Featured)],
                Hint = Strings.FeaturedShowsTheFirstPictureLargeWith,
            },
            new Option("columns", Strings.Columns, OptionKind.Number, "0") { Min = 0, Max = 20, Whole = true, ShownWhen = new("layout", "grid"), Hint = Strings.N0PicksABalancedGrid },
            new Option("fit", Strings.Pictures, OptionKind.Choice, "fill")
            {
                Choices = [new("fill", Strings.FillTheirCells), new("whole", Strings.ShowThemWhole)],
                ShownWhen = new("layout", "grid", "featured"),
            },
            new Option("width", Strings.Width, OptionKind.Number, "2400") { Min = 200, Max = 20_000, Whole = true, Unit = "px" },
            new Option("gap", Strings.SpaceBetween, OptionKind.Number, "16") { Min = 0, Max = 500, Whole = true, Unit = "px" },
            new Option("background", Strings.Background, OptionKind.Colour, "#FFFFFF"),
            new Option("order", Strings.Order, OptionKind.Order, "") { Remember = false });
    }

    public Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var ordered = target.Ordered("order", inputs);
        using var collage = Compose(ordered, target, (int)target.Number("width"), ctx.Progress, ctx.Cancellation);
        var key = collage.HasAlpha ? "png" : "jpg";
        var output = OutputPaths.Unique(OutputPaths.DirectoryOf(inputs[0]), "Collage", key);
        try
        {
            collage.Quality = 92;
            collage.Write(output, ImageConverter.FormatFor(key));
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        return [output];
    }, ctx.Cancellation);

    public Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx) => Task.Run(() =>
    {
        var ordered = target.Ordered("order", inputs);
        var width = (int)target.Number("width");
        var (_, height) = Layout(ordered.Select(Shape).ToList(), target, width);
        // Made at preview size: the space between is scaled down with everything else.
        using var collage = Compose(ordered, target, Math.Min(width, request.MaxSize), null, ctx.Cancellation);
        return new Preview(collage.ToByteArray(MagickFormat.Png),
        [
            new Fact(Strings.Size, Describe.Pixels(width, height)),
            new Fact(Strings.SavedAs, string.Format(Strings.BesideTheFirstPicture, (target.Colour("background").A < 255 ? "Collage.png" : "Collage.jpg"))),
        ]);
    }, ctx.Cancellation);

    /// <summary>The collage at <paramref name="width"/> pixels wide; the space between scales with it.</summary>
    static IMagickImage<byte> Compose(IReadOnlyList<string> inputs, Target target, int width, IProgress<double>? progress, CancellationToken ct)
    {
        var scale = width / target.Number("width");
        var (cells, height) = Layout(inputs.Select(Shape).ToList(), target, width, scale);
        var background = target.Colour("background");
        var canvas = new MagickImage(new MagickColor(background.R, background.G, background.B, background.A), (uint)width, (uint)height);
        try
        {
            var whole = target.Choice("fit") == "whole";
            for (var i = 0; i < inputs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cell = cells[i];
                using var frames = ImageConverter.Read(inputs[i]);
                using var picture = frames[0].Clone();
                picture.AutoOrient();
                if (whole)
                {
                    picture.Resize((uint)cell.Width, (uint)cell.Height);
                }
                else
                {
                    picture.Resize(new MagickGeometry((uint)cell.Width, (uint)cell.Height) { FillArea = true });
                    picture.Crop(new MagickGeometry((uint)cell.Width, (uint)cell.Height), Gravity.Center);
                    picture.ResetPage();
                }
                canvas.Composite(picture, cell.X + (cell.Width - (int)picture.Width) / 2, cell.Y + (cell.Height - (int)picture.Height) / 2, CompositeOperator.Over);
                progress?.Report((i + 1.0) / (inputs.Count + 1));
            }
            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    /// <summary>A picture's width and height as it's shown, after any turn its EXIF asks for, read without decoding it.</summary>
    static (int Width, int Height) Shape(string path)
    {
        try
        {
            var info = new MagickImageInfo(path);
            var sideways = info.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom;
            return sideways ? ((int)info.Height, (int)info.Width) : ((int)info.Width, (int)info.Height);
        }
        catch (MagickException ex)
        {
            throw new ConversionException(string.Format(Strings.DoesnTLookLikeAValidImage, Path.GetFileName(path), ImageConverter.Reason(ex)), ex);
        }
    }

    static (IReadOnlyList<Cell> Cells, int Height) Layout(IReadOnlyList<(int Width, int Height)> shapes, Target target, int width, double scale = 1) =>
        Layout(shapes, target.Choice("layout"), (int)target.Number("columns"), width, (int)Math.Round(target.Number("gap") * scale));

    /// <summary>Where each picture goes, and how tall the collage is, for a collage <paramref name="width"/> pixels wide.</summary>
    public static (IReadOnlyList<Cell> Cells, int Height) Layout(IReadOnlyList<(int Width, int Height)> shapes, string layout, int columns, int width, int gap)
    {
        var aspects = shapes.Select(s => (double)s.Width / Math.Max(1, s.Height)).ToList();
        var cells = new List<Cell>();
        switch (layout)
        {
            case "row":
            {
                // One height for all, chosen so the row fills the width.
                var height = Math.Max(1, (int)Math.Round((width - gap * (shapes.Count + 1)) / aspects.Sum()));
                var x = (double)gap;
                foreach (var aspect in aspects)
                {
                    var w = Math.Max(1, (int)Math.Round(aspect * height));
                    cells.Add(new Cell((int)Math.Round(x), gap, w, height));
                    x += aspect * height + gap;
                }
                return (cells, height + 2 * gap);
            }
            case "column":
            {
                var w = Math.Max(1, width - 2 * gap);
                var y = gap;
                foreach (var aspect in aspects)
                {
                    var h = Math.Max(1, (int)Math.Round(w / aspect));
                    cells.Add(new Cell(gap, y, w, h));
                    y += h + gap;
                }
                return (cells, y);
            }
            case "featured":
            {
                // The first picture whole on the left, two thirds of the width; the rest stacked on the right.
                var left = Math.Max(1, (int)Math.Round((width - 3 * gap) * 2 / 3.0));
                var right = Math.Max(1, width - 3 * gap - left);
                var height = Math.Max(1, (int)Math.Round(left / aspects[0]));
                cells.Add(new Cell(gap, gap, left, height));
                var rest = shapes.Count - 1;
                var each = Math.Max(1, (height - gap * (rest - 1)) / rest);
                for (var i = 0; i < rest; i++)
                    cells.Add(new Cell(2 * gap + left, gap + i * (each + gap), right, each));
                return (cells, height + 2 * gap);
            }
            default:
            {
                var count = shapes.Count;
                var cols = columns > 0 ? Math.Min(columns, count) : (int)Math.Ceiling(Math.Sqrt(count));
                var rows = (int)Math.Ceiling(count / (double)cols);
                // Cells take the pictures' average shape, kept between 1:2 and 2:1.
                var aspect = Math.Clamp(aspects.Average(), 0.5, 2);
                var w = Math.Max(1, (width - gap * (cols + 1)) / cols);
                var h = Math.Max(1, (int)Math.Round(w / aspect));
                for (var i = 0; i < count; i++)
                {
                    var row = i / cols;
                    var inRow = row == rows - 1 ? count - row * cols : cols;
                    var indent = (cols - inRow) * (w + gap) / 2; // a short last row is centred
                    cells.Add(new Cell(gap + indent + i % cols * (w + gap), gap + row * (h + gap), w, h));
                }
                return (cells, gap + rows * (h + gap));
            }
        }
    }
}

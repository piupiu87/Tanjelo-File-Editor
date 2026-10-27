using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;

namespace Tanjelo.Core.Converters;

public enum MarkKind { Arrow, Line, Rectangle, Ellipse, Text, Callout, Magnifier, Pen, Highlighter }

/// <summary>A point in the picture's own pixels, written [x, y].</summary>
[JsonConverter(typeof(MarkPointConverter))]
public readonly record struct MarkPoint(double X, double Y)
{
    public Point ToPoint() => new(X, Y);

    public static MarkPoint From(Point point) => new(point.X, point.Y);
}

/// <summary>
/// One mark drawn on a picture, in its own pixels. Arrows and lines run from the first point to the second; rectangles
/// and ellipses fill the box between them. Text starts at its point (top left), a callout and a magnifier are centred on
/// theirs, and a pen or highlighter stroke goes through all of them.
/// </summary>
public sealed record Mark
{
    [JsonPropertyName("type")]
    public MarkKind Kind { get; init; }

    public ImmutableArray<MarkPoint> Points { get; init; } = [];

    public string Colour { get; init; } = "#E5383B";

    /// <summary>The line's width in pixels; for a magnifier, its ring's.</summary>
    public double Width { get; init; } = 6;

    public string? Text { get; init; }

    /// <summary>Text's height, a callout's diameter or a magnifier's diameter, in pixels.</summary>
    public double Size { get; init; } = 48;

    /// <summary>How much a magnifier enlarges.</summary>
    public double Zoom { get; init; } = 2;

    public Point Start => Points[0].ToPoint();
    public Point End => Points[^1].ToPoint();

    public Mark Moved(Vector by) => this with { Points = [.. Points.Select(p => new MarkPoint(p.X + by.X, p.Y + by.Y))] };
}

/// <summary>The marks for a picture, as the editor saves them and tanj reads them: {"marks": [...]}.</summary>
public static class MarkSpec
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    sealed record File(IReadOnlyList<Mark> Marks);

    public static string Write(IEnumerable<Mark> marks) => JsonSerializer.Serialize(new File(marks.ToList()), Json);

    /// <summary>Marks from JSON, or from a .json file when <paramref name="text"/> is a path (found beside <paramref name="besides"/>).</summary>
    public static IReadOnlyList<Mark> Read(string text, string besides)
    {
        text = text.Trim();
        if (!text.StartsWith('{') && !text.StartsWith('['))
        {
            var path = Path.GetFullPath(text, OutputPaths.DirectoryOf(besides));
            if (!System.IO.File.Exists(path)) throw new ConversionException($"Couldn't find the marks file {path}.");
            text = System.IO.File.ReadAllText(path);
        }
        IReadOnlyList<Mark>? marks;
        try
        {
            marks = text.TrimStart().StartsWith('[')
                ? JsonSerializer.Deserialize<List<Mark>>(text, Json)
                : JsonSerializer.Deserialize<File>(text, Json)?.Marks;
        }
        catch (JsonException ex)
        {
            throw new ConversionException($"Couldn't read the marks: {ex.Message.Split(" Path:")[0].Split(" LineNumber:")[0].TrimEnd('.')}" +
                (ex.LineNumber is { } line ? $", on line {line + 1}." : "."), ex);
        }
        marks ??= [];
        for (var i = 0; i < marks.Count; i++)
            Check(marks[i], i + 1);
        return marks;
    }

    static void Check(Mark mark, int number)
    {
        var needs = mark.Kind switch
        {
            MarkKind.Arrow or MarkKind.Line or MarkKind.Rectangle or MarkKind.Ellipse => 2,
            _ => 1,
        };
        var name = mark.Kind.ToString().ToLowerInvariant();
        if (mark.Points.IsDefault || mark.Points.Length < needs || (needs == 2 && mark.Points.Length != 2))
            throw new ConversionException($"Mark {number} ({name}) needs {(needs == 2 ? "two points" : "a point")}, as [x, y].");
        if (!Core.Colour.TryParse(mark.Colour, out _))
            throw new ConversionException($"Mark {number} ({name}) has the colour '{mark.Colour}'; use one such as #E5383B.");
        if (mark.Width <= 0 || mark.Size <= 0 || mark.Zoom < 1)
            throw new ConversionException($"Mark {number} ({name}) needs a width and size above 0, and a zoom of at least 1.");
        if (mark.Kind == MarkKind.Text && string.IsNullOrWhiteSpace(mark.Text))
            throw new ConversionException($"Mark {number} (text) has no text.");
    }
}

/// <summary>Reads and writes a point as [x, y].</summary>
public sealed class MarkPointConverter : JsonConverter<MarkPoint>
{
    public override MarkPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A point is written as [x, y]");
        reader.Read();
        var x = reader.GetDouble();
        reader.Read();
        var y = reader.GetDouble();
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("A point is written as [x, y]");
        return new MarkPoint(x, y);
    }

    public override void Write(Utf8JsonWriter writer, MarkPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(Math.Round(value.X, 1));
        writer.WriteNumberValue(Math.Round(value.Y, 1));
        writer.WriteEndArray();
    }
}

/// <summary>
/// Draws marks with WPF, in the picture's own pixels. The editor draws with it on screen and the engine into the saved
/// file, so the two look the same.
/// </summary>
public static class MarkRenderer
{
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface Numbers = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <param name="picture">The picture under the marks, which a magnifier enlarges.</param>
    public static void Draw(DrawingContext context, IReadOnlyList<Mark> marks, ImageSource? picture, Size size)
    {
        var callouts = 0;
        foreach (var mark in marks)
        {
            var colour = Parse(mark.Colour);
            var brush = Frozen(new SolidColorBrush(colour));
            switch (mark.Kind)
            {
                case MarkKind.Arrow:
                    DrawArrow(context, mark, brush);
                    break;
                case MarkKind.Line:
                    context.DrawLine(Stroke(brush, mark.Width), mark.Start, mark.End);
                    break;
                case MarkKind.Rectangle:
                    context.DrawRectangle(null, Stroke(brush, mark.Width, PenLineJoin.Miter), new Rect(mark.Start, mark.End));
                    break;
                case MarkKind.Ellipse:
                    var box = new Rect(mark.Start, mark.End);
                    context.DrawEllipse(null, Stroke(brush, mark.Width), new Point(box.X + box.Width / 2, box.Y + box.Height / 2), box.Width / 2, box.Height / 2);
                    break;
                case MarkKind.Text:
                    var text = Text(mark);
                    var outline = text.BuildGeometry(mark.Start);
                    // A thin halo in the opposite tone keeps text readable on a busy photo.
                    context.DrawGeometry(null, Stroke(Frozen(new SolidColorBrush(Opposite(colour, 170))), Math.Max(1, mark.Size / 9)), outline);
                    context.DrawGeometry(brush, null, outline);
                    break;
                case MarkKind.Callout:
                    callouts++;
                    var radius = mark.Size / 2;
                    context.DrawEllipse(brush, Stroke(Frozen(new SolidColorBrush(Opposite(colour, 230))), Math.Max(1, mark.Size / 16)), mark.Start, radius, radius);
                    var ink = Frozen(new SolidColorBrush(Opposite(colour, 255)));
                    var number = new FormattedText(callouts.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        Numbers, mark.Size * (callouts < 10 ? 0.58 : 0.46), ink, 1);
                    var glyphs = number.BuildGeometry(new Point());
                    var bounds = glyphs.Bounds;
                    // Centred on the ink, not the line box, so the number sits in the middle of the circle.
                    context.PushTransform(new TranslateTransform(mark.Start.X - bounds.X - bounds.Width / 2, mark.Start.Y - bounds.Y - bounds.Height / 2));
                    context.DrawGeometry(ink, null, glyphs);
                    context.Pop();
                    break;
                case MarkKind.Magnifier:
                    DrawMagnifier(context, mark, brush, picture, size);
                    break;
                case MarkKind.Pen:
                    context.DrawGeometry(null, Stroke(brush, mark.Width), Path(mark));
                    break;
                case MarkKind.Highlighter:
                    var marker = Frozen(new SolidColorBrush(Color.FromArgb((byte)Math.Min((int)colour.A, 110), colour.R, colour.G, colour.B)));
                    context.DrawGeometry(null, Stroke(marker, mark.Width, PenLineJoin.Round, PenLineCap.Square), Path(mark));
                    break;
            }
        }
    }

    /// <summary>The space a mark takes, in the picture's pixels, for picking and framing it in the editor.</summary>
    public static Rect Bounds(Mark mark)
    {
        var half = mark.Width / 2;
        switch (mark.Kind)
        {
            case MarkKind.Text:
                var text = Text(mark);
                return new Rect(mark.Start, new Size(text.WidthIncludingTrailingWhitespace, text.Height));
            case MarkKind.Callout or MarkKind.Magnifier:
                return new Rect(mark.Start.X - mark.Size / 2, mark.Start.Y - mark.Size / 2, mark.Size, mark.Size);
            case MarkKind.Arrow:
                var head = Head(mark.Width);
                var line = Rect.Union(new Rect(mark.Start, new Size()), mark.End);
                line.Inflate(head, head);
                return line;
            default:
                var box = mark.Points.Select(p => p.ToPoint()).Aggregate(new Rect(mark.Start, new Size()), (r, p) => Rect.Union(r, p));
                box.Inflate(half, half);
                return box;
        }
    }

    /// <summary>Whether a point is on a mark, within <paramref name="tolerance"/> pixels of its line.</summary>
    public static bool Hits(Mark mark, Point at, double tolerance)
    {
        switch (mark.Kind)
        {
            case MarkKind.Text or MarkKind.Callout or MarkKind.Magnifier:
                var bounds = Bounds(mark);
                bounds.Inflate(tolerance, tolerance);
                return mark.Kind == MarkKind.Text
                    ? bounds.Contains(at)
                    : (at - mark.Start).Length <= mark.Size / 2 + tolerance;
            case MarkKind.Rectangle:
                return Shape(mark).StrokeContains(Stroke(Brushes.Black, mark.Width + 2 * tolerance), at);
            case MarkKind.Ellipse:
                return Shape(mark).StrokeContains(Stroke(Brushes.Black, mark.Width + 2 * tolerance), at);
            default:
                return Shape(mark).StrokeContains(Stroke(Brushes.Black, Math.Max(mark.Width, Head(mark.Width) * (mark.Kind == MarkKind.Arrow ? 1 : 0)) + 2 * tolerance), at);
        }
    }

    static Geometry Shape(Mark mark) => mark.Kind switch
    {
        MarkKind.Rectangle => new RectangleGeometry(new Rect(mark.Start, mark.End)),
        MarkKind.Ellipse => new EllipseGeometry(new Rect(mark.Start, mark.End)),
        MarkKind.Arrow or MarkKind.Line => new LineGeometry(mark.Start, mark.End),
        _ => Path(mark),
    };

    /// <summary>An arrowhead's length for a line this wide.</summary>
    static double Head(double width) => Math.Max(width * 3.6, 14);

    static void DrawArrow(DrawingContext context, Mark mark, Brush brush)
    {
        var (from, to) = (mark.Start, mark.End);
        var along = to - from;
        var length = along.Length;
        if (length < 0.5) return;
        along /= length;
        var head = Math.Min(Head(mark.Width), length);
        var across = new Vector(-along.Y, along.X) * head * 0.42;
        var bottom = to - along * head;
        // The shaft stops inside the head, so its square end never shows past the point.
        context.DrawLine(Stroke(brush, mark.Width, PenLineJoin.Round, PenLineCap.Round, PenLineCap.Flat), from, to - along * head * 0.6);
        var tip = new StreamGeometry();
        using (var figure = tip.Open())
        {
            figure.BeginFigure(to, isFilled: true, isClosed: true);
            figure.LineTo(bottom + across, true, true);
            figure.LineTo(bottom - across, true, true);
        }
        tip.Freeze();
        context.DrawGeometry(brush, Stroke(brush, Math.Max(1, mark.Width / 3)), tip);
    }

    static void DrawMagnifier(DrawingContext context, Mark mark, Brush brush, ImageSource? picture, Size size)
    {
        var centre = mark.Start;
        var radius = mark.Size / 2;
        if (picture is not null)
        {
            context.PushClip(new EllipseGeometry(centre, radius, radius));
            // The picture enlarged around the centre, so the middle of the circle shows what's under it.
            context.PushTransform(new MatrixTransform(mark.Zoom, 0, 0, mark.Zoom, centre.X * (1 - mark.Zoom), centre.Y * (1 - mark.Zoom)));
            context.DrawImage(picture, new Rect(size));
            context.Pop();
            context.Pop();
        }
        context.DrawEllipse(null, Stroke(Frozen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0))), mark.Width + 2), centre, radius + 1, radius + 1);
        context.DrawEllipse(null, Stroke(brush, mark.Width), centre, radius, radius);
    }

    static StreamGeometry Path(Mark mark)
    {
        var path = new StreamGeometry();
        using (var figure = path.Open())
        {
            figure.BeginFigure(mark.Start, isFilled: false, isClosed: false);
            // A single dot still shows: a stroke needs two points.
            if (mark.Points.Length == 1) figure.LineTo(mark.Start + new Vector(0.01, 0), true, true);
            else figure.PolyLineTo(mark.Points.Skip(1).Select(p => p.ToPoint()).ToList(), true, true);
        }
        path.Freeze();
        return path;
    }

    static FormattedText Text(Mark mark) =>
        new(mark.Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, mark.Size, Brushes.Black, 1);

    static Pen Stroke(Brush brush, double width, PenLineJoin join = PenLineJoin.Round, PenLineCap cap = PenLineCap.Round, PenLineCap? end = null)
    {
        var pen = new Pen(brush, width) { LineJoin = join, StartLineCap = cap, EndLineCap = end ?? cap };
        pen.Freeze();
        return pen;
    }

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    static Color Parse(string text) =>
        Core.Colour.TryParse(text, out var c) ? Color.FromArgb(c.A, c.R, c.G, c.B) : Colors.Red;

    /// <summary>White on dark colours and near-black on light ones.</summary>
    static Color Opposite(Color colour, byte alpha) =>
        0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B > 150 ? Color.FromArgb(alpha, 20, 20, 20) : Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>
    /// The marks over a picture at its full size, as premultiplied BGRA: see-through wherever nothing is drawn. Call it
    /// on an STA thread.
    /// </summary>
    internal static byte[] Layer(IReadOnlyList<Mark> marks, BitmapSource picture)
    {
        var (width, height) = (picture.PixelWidth, picture.PixelHeight);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            Draw(context, marks, picture, new Size(width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}

/// <summary>
/// Draws arrows, shapes, text, numbered callouts, magnifiers and pen strokes on a picture. The marks are drawn with WPF
/// at the picture's full size on an STA thread of their own, the same way the editor shows them, then laid over the
/// picture, whose other pixels and metadata stay as they were.
/// </summary>
public sealed class AnnotateTool : IConverter
{
    static readonly Lazy<StaWorker> Painter = new(() => new StaWorker("Tanjelo marks"));

    static readonly Target Entry = new Target("annotate", "Annotate…", "Arrows, shapes and text", Section.Image)
    {
        Kind = TargetKind.Tool,
        Running = "Annotating…",
        Done = "Annotated",
        Confirm = "Save",
        Editor = EditorKind.Annotate,
    }.WithOptions(new Option("spec", "Marks", OptionKind.Text, "")
    {
        Remember = false,
        Hint = "The marks as JSON, or a .json file of them: {\"marks\": [{\"type\": \"arrow\", \"points\": [[10, 10], [200, 120]], \"colour\": \"#E5383B\", \"width\": 8}]}. " +
            "Types: arrow, line, rectangle, ellipse (two points); text (\"text\", \"size\"), callout and magnifier (one point, \"size\"); pen and highlighter (any points).",
    });

    public IEnumerable<Target> TargetsFor(string input) =>
        Formats.Images.Contains(Formats.Key(input)) && Formats.Key(input) is not ("svg" or "ico") ? [Entry] : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var marks = MarkSpec.Read(target.Text("spec"), input);
        if (marks.Count == 0) throw new ConversionException("There are no marks to draw.");
        using var frames = ImageConverter.Read(input);
        var image = frames[0];
        image.AutoOrient();
        var (width, height) = ((int)image.Width, (int)image.Height);
        byte[] layer;
        using (var pixels = image.GetPixelsUnsafe())
        {
            var bgra = pixels.ToByteArray("BGRA")!;
            layer = await Painter.Value.InvokeAsync(() =>
                MarkRenderer.Layer(marks, BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4)));
        }
        ctx.Cancellation.ThrowIfCancellationRequested();
        Unpremultiply(layer);
        using var marksImage = new MagickImage();
        marksImage.ReadPixels(layer, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "BGRA"));
        image.Composite(marksImage, CompositeOperator.Over);

        var key = ImageConverter.ChangedCopyKey(input, image.HasAlpha);
        var output = OutputPaths.Suffixed(input, "annotated", key);
        try
        {
            if (key == "jpg") image.Quality = (uint)ctx.Options.JpegQuality;
            image.Write(output, ImageConverter.FormatFor(key));
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        ctx.Progress.Report(1);
        return [output];
    }

    /// <summary>Premultiplied BGRA, as WPF draws it, to the straight BGRA ImageMagick reads.</summary>
    static void Unpremultiply(byte[] pixels)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha is 0 or 255) continue;
            for (var c = 0; c < 3; c++)
                pixels[i + c] = (byte)Math.Min(255, (pixels[i + c] * 255 + alpha / 2) / alpha);
        }
    }
}

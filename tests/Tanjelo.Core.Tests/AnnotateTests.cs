using ImageMagick;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class AnnotateTests
{
    static Mark Make(MarkKind kind, string colour, params (double X, double Y)[] points) =>
        new() { Kind = kind, Colour = colour, Points = [.. points.Select(p => new MarkPoint(p.X, p.Y))] };

    [Fact]
    public void Marks_round_trip_through_json()
    {
        Mark[] marks =
        [
            Make(MarkKind.Arrow, "#E5383B", (10, 10), (200, 120)) with { Width = 8 },
            Make(MarkKind.Text, "#FFFFFF", (30, 40)) with { Text = "Look here", Size = 36 },
            Make(MarkKind.Callout, "#2E8540", (300, 200)) with { Size = 60 },
            Make(MarkKind.Magnifier, "#000000", (100, 100)) with { Size = 120, Zoom = 3 },
            Make(MarkKind.Highlighter, "#FFD400", (0, 0), (5, 5), (10, 3)) with { Width = 20 },
        ];
        var json = MarkSpec.Write(marks);
        Assert.Contains("\"type\":\"arrow\"", json);
        Assert.Contains("\"points\":[[10,10],[200,120]]", json);
        var back = MarkSpec.Read(json, Path.Combine(Path.GetTempPath(), "x.png"));
        Assert.Equal(json, MarkSpec.Write(back));
        Assert.Equal("Look here", back[1].Text);
        Assert.Equal(3, back[3].Zoom);
        // A plain list of marks reads too, as does a file of them.
        Assert.Equal(json, MarkSpec.Write(MarkSpec.Read(json[(json.IndexOf('[') )..^1], Path.Combine(Path.GetTempPath(), "x.png"))));
        using var scratch = new Scratch();
        File.WriteAllText(scratch.PathOf("marks.json"), json);
        Assert.Equal(json, MarkSpec.Write(MarkSpec.Read("marks.json", scratch.PathOf("photo.png"))));
    }

    [Theory]
    [InlineData("""{"marks": [{"type": "arrow", "points": [[1, 2]]}]}""", "Mark 1 (arrow) needs two points, as [x, y].")]
    [InlineData("""{"marks": [{"type": "callout", "points": [[1, 2]], "colour": "red"}]}""", "Mark 1 (callout) has the colour 'red'; use one such as #E5383B.")]
    [InlineData("""{"marks": [{"type": "text", "points": [[1, 2]]}]}""", "Mark 1 (text) has no text.")]
    [InlineData("""{"marks": [{"type": "star", "points": [[1, 2]]}]}""", "Couldn't read the marks:")]
    [InlineData("""{"marks": [ """, "Couldn't read the marks:")]
    [InlineData("missing.json", "Couldn't find the marks file")]
    public void Bad_marks_say_what_is_wrong(string spec, string message)
    {
        var error = Assert.Throws<ConversionException>(() => MarkSpec.Read(spec, Path.Combine(Path.GetTempPath(), "x.png")));
        Assert.StartsWith(message, error.Message);
    }

    static async Task<string> Annotate(string input, IEnumerable<Mark> marks)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "annotate", [input]).WithParameters(new Dictionary<string, string> { ["spec"] = MarkSpec.Write(marks) });
        var result = await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    static MagickColor At(IPixelCollection<byte> pixels, int x, int y) => (MagickColor)pixels.GetPixel(x, y).ToColor()!;

    static bool Near(MagickColor colour, byte r, byte g, byte b, int tolerance = 10) =>
        Math.Abs(colour.R - r) <= tolerance && Math.Abs(colour.G - g) <= tolerance && Math.Abs(colour.B - b) <= tolerance;

    [Fact]
    public async Task Each_kind_of_mark_lands_where_its_points_say()
    {
        using var scratch = new Scratch();
        var white = scratch.Image("white.png", 600, 400, System.Drawing.Color.White);
        var output = await Annotate(white,
        [
            Make(MarkKind.Rectangle, "#FF0000", (50, 50), (150, 120)) with { Width = 10 },
            Make(MarkKind.Line, "#0000FF", (200, 40), (380, 40)) with { Width = 8 },
            Make(MarkKind.Arrow, "#0000FF", (200, 100), (380, 100)) with { Width = 8 },
            Make(MarkKind.Ellipse, "#00A000", (420, 40), (560, 140)) with { Width = 8 },
            Make(MarkKind.Text, "#000000", (60, 200)) with { Text = "Hello", Size = 60 },
            Make(MarkKind.Pen, "#FF00FF", (300, 250), (350, 300), (400, 250)) with { Width = 12 },
            Make(MarkKind.Highlighter, "#FFD400", (420, 320), (580, 320)) with { Width = 30 },
        ]);
        Assert.Equal("white-annotated.png", Path.GetFileName(output));
        using var image = new MagickImage(output);
        using var p = image.GetPixels();
        Assert.True(Near(At(p, 50, 85), 255, 0, 0), "the rectangle's left edge");
        Assert.True(Near(At(p, 100, 85), 255, 255, 255), "inside the rectangle stays white");
        Assert.True(Near(At(p, 290, 40), 0, 0, 255), "the line");
        Assert.True(Near(At(p, 250, 100), 0, 0, 255), "the arrow's shaft");
        Assert.True(Near(At(p, 375, 100), 0, 0, 255), "the arrowhead");
        Assert.True(Near(At(p, 360, 108), 0, 0, 255), "the arrowhead is wider than the shaft");
        Assert.True(Near(At(p, 250, 108), 255, 255, 255), "beside the shaft");
        Assert.True(Near(At(p, 420, 90), 0, 160, 0), "the ellipse's left side");
        Assert.True(Near(At(p, 350, 300), 255, 0, 255), "the pen's turning point");
        var highlight = At(p, 500, 320);
        Assert.True(highlight.B < 200 && highlight.R > 240 && highlight.G > 230, $"the highlighter shows through: {highlight}");
        var ink = Enumerable.Range(60, 150).Count(x => At(p, x, 240).R < 60);
        Assert.True(ink > 10, "the text has ink across its middle");
        Assert.True(Near(At(p, 590, 10), 255, 255, 255, 0), "untouched pixels stay exactly as they were");
    }

    [Fact]
    public async Task A_callout_is_numbered_by_when_it_was_made()
    {
        using var scratch = new Scratch();
        var white = scratch.Image("white.png", 400, 200, System.Drawing.Color.White);
        var a = Make(MarkKind.Callout, "#C2410C", (100, 100)) with { Size = 80 };
        var b = Make(MarkKind.Callout, "#C2410C", (300, 100)) with { Size = 80 };
        using var first = new MagickImage(await Annotate(white, [a, b]));   // a is 1, b is 2
        using var second = new MagickImage(await Annotate(white, [b, a]));  // b is 1, a is 2
        using var one = first.CloneArea(new MagickGeometry(60, 60, 80, 80));
        using var two = first.CloneArea(new MagickGeometry(260, 60, 80, 80));
        using var oneAgain = second.CloneArea(new MagickGeometry(260, 60, 80, 80));
        using var twoAgain = second.CloneArea(new MagickGeometry(60, 60, 80, 80));
        Assert.Equal(0, one.Compare(oneAgain, ErrorMetric.RootMeanSquared), 6);
        Assert.Equal(0, two.Compare(twoAgain, ErrorMetric.RootMeanSquared), 6);
        Assert.True(one.Compare(two, ErrorMetric.RootMeanSquared) > 0.01, "1 and 2 look different");
    }

    [Fact]
    public async Task A_magnifier_shows_what_is_under_it_twice_as_big()
    {
        using var scratch = new Scratch();
        var path = scratch.PathOf("dot.png");
        using (var dot = new MagickImage(MagickColors.White, 400, 300))
        {
            new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black).Rectangle(196, 146, 203, 153).Draw(dot); // 8 × 8 at the middle
            dot.Write(path);
        }
        var output = await Annotate(path, [Make(MarkKind.Magnifier, "#000000", (200, 150)) with { Size = 120, Width = 3 }]);
        using var image = new MagickImage(output);
        using var p = image.GetPixels();
        Assert.True(Near(At(p, 200 + 6, 150), 0, 0, 0), "4 pixels from the middle was white, but is now inside the doubled square");
        Assert.True(Near(At(p, 200 + 10, 150), 255, 255, 255), "10 pixels out is past the doubled square");
        Assert.True(Near(At(p, 200, 150 - 60), 0, 0, 0, 40), "the ring");
    }

    [Fact]
    public async Task A_photo_keeps_its_format_and_metadata()
    {
        using var scratch = new Scratch();
        var photo = scratch.PathOf("photo.jpg");
        using (var image = new MagickImage("plasma:", 300, 200))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "Tanjelo Camera Co");
            image.SetProfile(exif);
            image.Write(photo);
        }
        var output = await Annotate(photo, [Make(MarkKind.Arrow, "#E5383B", (10, 10), (100, 80))]);
        Assert.Equal("photo-annotated.jpg", Path.GetFileName(output));
        using var annotated = new MagickImage(output);
        Assert.Equal("Tanjelo Camera Co", annotated.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
    }
}

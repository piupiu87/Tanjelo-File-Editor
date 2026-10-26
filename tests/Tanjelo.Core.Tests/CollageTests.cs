using System.Drawing;
using ImageMagick;
using Tanjelo.Core.Converters;
using Cell = Tanjelo.Core.Converters.CollageOperation.Cell;

namespace Tanjelo.Core.Tests;

public class CollageTests
{
    [Fact]
    public void A_grid_is_balanced_and_centres_a_short_last_row()
    {
        var (cells, height) = CollageOperation.Layout(Enumerable.Repeat((100, 100), 5).ToList(), "grid", 0, 1000, 10);

        Assert.Equal(
        [
            new Cell(10, 10, 320, 320), new Cell(340, 10, 320, 320), new Cell(670, 10, 320, 320),
            new Cell(175, 340, 320, 320), new Cell(505, 340, 320, 320),
        ], cells);
        Assert.Equal(670, height);
    }

    [Fact]
    public void A_row_gives_every_picture_the_same_height_and_fills_the_width()
    {
        var (cells, height) = CollageOperation.Layout([(200, 100), (100, 100)], "row", 0, 1000, 10);

        Assert.Equal([new Cell(10, 10, 646, 323), new Cell(666, 10, 323, 323)], cells);
        Assert.Equal(343, height);
    }

    [Fact]
    public void A_column_gives_every_picture_the_full_width()
    {
        var (cells, height) = CollageOperation.Layout([(200, 100), (100, 200)], "column", 0, 500, 0);

        Assert.Equal([new Cell(0, 0, 500, 250), new Cell(0, 250, 500, 1000)], cells);
        Assert.Equal(1250, height);
    }

    [Fact]
    public void Featured_shows_the_first_picture_large_and_stacks_the_rest()
    {
        var (cells, height) = CollageOperation.Layout([(100, 100), (100, 100), (100, 100)], "featured", 0, 900, 0);

        Assert.Equal([new Cell(0, 0, 600, 600), new Cell(600, 0, 300, 300), new Cell(600, 300, 300, 300)], cells);
        Assert.Equal(600, height);
    }

    [Fact]
    public void Collage_is_offered_for_two_or_more_pictures_only()
    {
        using var scratch = new Scratch();
        var a = scratch.Image("a.png");

        Assert.DoesNotContain("collage", Env.Keys(a));
        Assert.Contains("collage", Env.Keys(a, scratch.Image("b.jpg")));
        Assert.DoesNotContain("collage", Env.Keys(a, scratch.Pdf("c.pdf")));
    }

    static async Task<string> Run(string[] inputs, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "collage", inputs).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        var result = await engine.RunAsync(inputs, target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    static IMagickColor<byte> Pixel(string path, int x, int y)
    {
        using var image = new MagickImage(path);
        return image.GetPixels().GetPixel(x, y).ToColor()!;
    }

    [Fact]
    public async Task Pictures_land_in_their_cells_in_the_chosen_order()
    {
        using var scratch = new Scratch();
        string[] inputs = [scratch.Image("red.png", 100, 100, Color.Red), scratch.Image("lime.png", 100, 100, Color.Lime), scratch.Image("blue.jpg", 100, 50, Color.Blue)];

        var output = await Run(inputs, ("layout", "row"), ("width", "500"), ("gap", "0"), ("order", "3,1,2"));

        Assert.Equal(scratch.PathOf("Collage.jpg"), output);
        var info = Inspect.Image(output);
        Assert.Equal((500u, 125u), (info.Width, info.Height));
        Assert.True(Pixel(output, 100, 60).FuzzyEquals(MagickColors.Blue, new Percentage(8)));
        Assert.True(Pixel(output, 300, 60).FuzzyEquals(MagickColors.Red, new Percentage(8)));
        Assert.True(Pixel(output, 440, 60).FuzzyEquals(MagickColors.Lime, new Percentage(8)));
    }

    [Fact]
    public async Task Whole_pictures_leave_the_background_around_them()
    {
        using var scratch = new Scratch();
        string[] inputs = [scratch.Image("wide.png", 200, 50, Color.Black), scratch.Image("square.png", 100, 100, Color.Black)];

        var output = await Run(inputs, ("columns", "2"), ("fit", "whole"), ("width", "420"), ("gap", "10"), ("background", "#FF0000"));

        Assert.True(Pixel(output, 100, 15).FuzzyEquals(MagickColors.Red, new Percentage(3))); // above the wide picture, inside its cell
        Assert.True(Pixel(output, 100, 70).FuzzyEquals(MagickColors.Black, new Percentage(3)));
    }

    [Fact]
    public async Task A_see_through_background_makes_a_png()
    {
        using var scratch = new Scratch();
        var output = await Run([scratch.Image("a.png"), scratch.Image("b.png")], ("width", "300"), ("background", "transparent"));

        Assert.Equal(scratch.PathOf("Collage.png"), output);
        Assert.Equal(0, Pixel(output, 0, 0).A);
    }

    [Fact]
    public async Task The_preview_is_small_but_reports_the_full_size()
    {
        using var scratch = new Scratch();
        string[] inputs = [scratch.Photo("a.jpg"), scratch.Photo("b.jpg"), scratch.Photo("c.jpg")];
        var engine = Env.Engine();

        var preview = await engine.PreviewAsync(inputs, Env.Offered(engine, "collage", inputs), new PreviewRequest(320), TestContext.Current.CancellationToken);

        using var picture = new MagickImage(preview!.Image!);
        Assert.Equal(320u, picture.Width);
        Assert.Equal(new Fact("Size", "2400 × 1812"), preview.Facts[0]); // two columns, two rows of 1176 × 882, 16 px apart
    }
}

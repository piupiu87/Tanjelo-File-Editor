using ImageMagick;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class BackgroundToolTests
{
    static Dictionary<string, string> Set(params (string Name, string Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);

    static Target Settings(params (string Name, string Value)[] values)
    {
        using var scratch = new Scratch();
        return Env.Offered(Env.Engine(), "background", [scratch.Image("a.png")]).WithParameters(Set(values));
    }

    static async Task<BatchResult> Run(string input, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "background", [input]).WithParameters(Set(values));
        return await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
    }

    static async Task<string> RunOne(string input, params (string Name, string Value)[] values)
    {
        var result = await Run(input, values);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    static IMagickColor<byte> Pixel(string path, int x, int y)
    {
        using var image = new MagickImage(path);
        return image.GetPixels().GetPixel(x, y).ToColor()!;
    }

    [Theory]
    [InlineData("fit", 480, 380)]
    [InlineData("1:1", 480, 480)]
    [InlineData("16:9", 676, 380)]
    [InlineData("9:16", 480, 853)]
    public void The_space_around_is_a_share_of_the_longer_side_and_the_shape_adds_to_it(string shape, int width, int height)
    {
        Assert.Equal((width, height), BackgroundTool.Canvas(400, 300, Settings(("padding", "10"), ("shape", shape))));
    }

    [Fact]
    public async Task A_photo_on_a_plain_colour_stays_a_jpg_with_the_picture_in_the_middle()
    {
        using var scratch = new Scratch();
        var photo = scratch.Image("photo.jpg", 400, 300, System.Drawing.Color.FromArgb(20, 40, 200));

        var output = await RunOne(photo, ("fill", "colour"), ("colour", "#FFFFFF"), ("padding", "10"), ("radius", "0"), ("shadow", "none"));

        Assert.Equal(scratch.PathOf("photo-background.jpg"), output);
        var info = Inspect.Image(output);
        Assert.Equal((480u, 380u), (info.Width, info.Height));
        Assert.True(Pixel(output, 5, 5).FuzzyEquals(MagickColors.White, new Percentage(3)));
        Assert.True(Pixel(output, 240, 190).FuzzyEquals(new MagickColor(20, 40, 200), new Percentage(6)));
    }

    [Fact]
    public async Task Rounded_corners_show_the_background_where_the_pictures_corner_was()
    {
        using var scratch = new Scratch();
        var photo = scratch.Image("photo.png", 400, 300, System.Drawing.Color.Black);

        var output = await RunOne(photo, ("fill", "colour"), ("colour", "#FFFFFF"), ("padding", "10"), ("radius", "20"), ("shadow", "none"));

        Assert.True(Pixel(output, 41, 41).FuzzyEquals(MagickColors.White, new Percentage(3))); // the picture's top-left corner
        Assert.True(Pixel(output, 240, 190).FuzzyEquals(MagickColors.Black, new Percentage(3)));
    }

    [Fact]
    public async Task A_see_through_background_makes_a_png()
    {
        using var scratch = new Scratch();
        var output = await RunOne(scratch.Image("photo.jpg"), ("fill", "colour"), ("colour", "transparent"), ("shadow", "none"));

        Assert.Equal(scratch.PathOf("photo-background.png"), output);
        Assert.Equal(0, Pixel(output, 0, 0).A);
    }

    [Fact]
    public async Task A_gradient_runs_between_its_two_colours()
    {
        using var scratch = new Scratch();
        var output = await RunOne(scratch.Image("photo.png", 100, 100), ("colour", "#000000"), ("colour2", "#FFFFFF"), ("direction", "right"), ("padding", "20"));

        Assert.True(Pixel(output, 0, 70).R < 20);
        Assert.True(Pixel(output, 139, 70).R > 235);
    }

    [Fact]
    public async Task A_picture_background_is_cut_to_cover_the_canvas()
    {
        using var scratch = new Scratch();
        var backdrop = scratch.Image("backdrop.png", 50, 400, System.Drawing.Color.Lime);

        var output = await RunOne(scratch.Image("photo.png", 400, 300), ("fill", "image"), ("image", backdrop), ("shape", "1:1"), ("shadow", "none"));

        var info = Inspect.Image(output);
        Assert.Equal((464u, 464u), (info.Width, info.Height));
        Assert.True(Pixel(output, 2, 2).FuzzyEquals(MagickColors.Lime, new Percentage(3)));
    }

    [Fact]
    public async Task A_picture_background_needs_a_picture()
    {
        using var scratch = new Scratch();
        var result = await Run(scratch.Image("photo.png"), ("fill", "image"));

        Assert.Equal("Picture is needed.", Assert.Single(result.Failures).Message);
    }

    [Fact]
    public async Task The_preview_is_the_result_made_small()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.jpg", 1600, 1200);
        var engine = Env.Engine();

        var preview = await engine.PreviewAsync([photo], Env.Offered(engine, "background", [photo]), new PreviewRequest(300), TestContext.Current.CancellationToken);

        using var picture = new MagickImage(preview!.Image!);
        Assert.Equal(300u, picture.Width);
        Assert.Equal([new Fact("Size", "1600 × 1200 → 1856 × 1456")], preview.Facts);
    }
}

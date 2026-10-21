using ImageMagick;

namespace Tanjelo.Core.Tests;

public class ImageConverterTests
{
    [Theory]
    [InlineData("jpg", MagickFormat.Jpeg)]
    [InlineData("webp", MagickFormat.WebP)]
    [InlineData("avif", MagickFormat.Avif)]
    [InlineData("gif", MagickFormat.Gif)]
    [InlineData("bmp", MagickFormat.Bmp)]
    [InlineData("tiff", MagickFormat.Tiff)]
    public async Task Png_converts_to_each_image_format(string key, MagickFormat format)
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne(key, scratch.Image("photo.png", 64, 48));

        Assert.Equal(scratch.PathOf("photo." + key), output);
        var info = Inspect.Image(output);
        Assert.Equal(format, info.Format);
        Assert.Equal((64u, 48u), (info.Width, info.Height));
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.bmp")]
    [InlineData("photo.gif")]
    [InlineData("photo.tiff")]
    public async Task Other_formats_convert_to_png(string name)
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("png", scratch.Image(name));
        Assert.Equal(MagickFormat.Png, Inspect.Image(output).Format);
    }

    [Fact]
    public async Task Jpg_flattens_transparency_onto_white()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("jpg", scratch.TransparentPng("logo.png"));

        using var image = new MagickImage(output);
        var corner = image.GetPixels().GetPixel(0, 0).ToColor()!;
        Assert.True(corner.R > 240 && corner.G > 240 && corner.B > 240, $"corner is {corner}");
    }

    [Fact]
    public async Task Ico_is_square_and_holds_several_sizes()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("ico", scratch.Image("wide.png", 300, 100));

        using var frames = new MagickImageCollection(output);
        Assert.True(frames.Count > 1);
        Assert.All(frames, f => Assert.Equal(f.Width, f.Height));
        Assert.Equal(256u, frames.Max(f => f.Width));
    }

    [Fact]
    public async Task Animated_gif_stays_animated_as_webp()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("webp", scratch.AnimatedGif("spin.gif", frames: 3));

        using var frames = new MagickImageCollection(output);
        Assert.Equal(3, frames.Count);
    }

    [Fact]
    public async Task Svg_is_rasterised()
    {
        using var scratch = new Scratch();
        var svg = scratch.Text("shape.svg", """<svg xmlns="http://www.w3.org/2000/svg" width="40" height="20"><rect width="40" height="20" fill="red"/></svg>""");

        var output = await Env.RunOne("png", svg);

        var info = Inspect.Image(output);
        Assert.Equal(MagickFormat.Png, info.Format);
        Assert.Equal(2.0, (double)info.Width / info.Height, 2);
    }

    [Fact]
    public async Task An_image_becomes_a_one_page_pdf()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("pdf", scratch.Image("scan.jpg", 300, 400));

        Assert.Equal(scratch.PathOf("scan.pdf"), output);
        Assert.Equal(1, Inspect.PdfPages(output));
    }

    [Fact]
    public async Task Several_images_combine_into_one_pdf_in_order()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("combine-pdf", scratch.Image("1.png"), scratch.Image("2.jpg"), scratch.Image("3.bmp"));

        Assert.Equal(scratch.PathOf("Combined.pdf"), output);
        Assert.Equal(3, Inspect.PdfPages(output));
    }

    [Fact]
    public async Task An_invalid_image_fails_with_a_readable_message()
    {
        using var scratch = new Scratch();
        var bad = scratch.Text("broken.png", "not an image");
        var engine = Env.Engine();

        var result = await engine.RunAsync([bad], Env.Offered(engine, "jpg", [bad]), ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("This doesn't look like a valid image", Assert.Single(result.Failures).Message);
    }
}

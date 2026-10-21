using ImageMagick;

namespace Tanjelo.Core.Tests;

public class PdfConverterTests
{
    [Fact]
    public async Task A_one_page_pdf_becomes_one_image_beside_it()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("png", scratch.Pdf("letter.pdf"));

        Assert.Equal(scratch.PathOf("letter.png"), output);
        Assert.Equal(MagickFormat.Png, Inspect.Image(output).Format);
    }

    [Fact]
    public async Task Pages_render_at_300_dpi_by_default()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("png", scratch.Pdf("letter.pdf"));

        var info = Inspect.Image(output);
        Assert.Equal((2550u, 3300u), (info.Width, info.Height)); // US Letter, 8.5 x 11 in
    }

    [Theory]
    [InlineData(200, 1700, 2200)]
    [InlineData(96, 816, 1056)]
    public async Task Pages_render_at_the_configured_resolution(int dpi, uint width, uint height)
    {
        using var scratch = new Scratch();
        var engine = Env.Engine(new ConverterOptions { PdfRenderDpi = dpi });

        var result = await Env.Run(engine, "png", scratch.Pdf("letter.pdf"));

        var info = Inspect.Image(Assert.Single(result.Outputs));
        Assert.Equal((width, height), (info.Width, info.Height));
    }

    [Fact]
    public async Task A_multi_page_pdf_becomes_a_folder_of_numbered_images()
    {
        using var scratch = new Scratch();
        var folder = await Env.RunOne("jpg", scratch.Pdf("report.pdf", pages: 3));

        Assert.Equal(scratch.PathOf("report pages"), folder);
        Assert.Equal(["report-1.jpg", "report-2.jpg", "report-3.jpg"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.All(Directory.GetFiles(folder), f => Assert.Equal(MagickFormat.Jpeg, Inspect.Image(f).Format));
    }

    [Fact]
    public async Task A_damaged_pdf_fails_without_leaving_files()
    {
        using var scratch = new Scratch();
        var bad = scratch.Text("broken.pdf", "%PDF-1.4 nothing else");
        var engine = Env.Engine();

        var result = await engine.RunAsync([bad], Env.Offered(engine, "png", [bad]), ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Failures);
        Assert.Equal(["broken.pdf"], scratch.Files());
    }
}

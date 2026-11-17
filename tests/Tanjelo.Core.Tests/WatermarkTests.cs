using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Content;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Tests;

/// <summary>Watermark…: text or a picture across the pages, as Windows draws them.</summary>
public class WatermarkTests
{
    static async Task<string> Watermark(string pdf, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "watermark", [pdf]).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        var result = await engine.RunAsync([pdf], target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("\n", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    static async Task<MagickImage> Render(string pdf, int page = 0)
    {
        var document = await Converters.PdfConverter.LoadAsync(pdf);
        return new MagickImage((await Converters.PdfConverter.RenderPagePngAsync(document, page, 72, 2000)).Png);
    }

    static IMagickColor<byte> At(MagickImage picture, int x, int y) => picture.GetPixels().GetPixel(x, y).ToColor()!;

    /// <summary>The watermark's letters, which are Arial; the fixtures' own text is Helvetica.</summary>
    static List<Letter> Marks(Page page) => page.Letters.Where(l => l.FontName?.Contains("Arial", StringComparison.Ordinal) == true).ToList();

    [Fact]
    public async Task Straight_text_crosses_the_middle_at_its_share_of_the_width_and_opacity()
    {
        using var scratch = new Scratch();
        var output = await Watermark(scratch.Pdf("report.pdf"), ("layout", "straight"), ("colour", "#000000"), ("opacity", "25"));

        Assert.Equal(scratch.PathOf("report-watermarked.pdf"), output);
        using (var pdf = PigDocument.Open(output))
        {
            var letters = Marks(pdf.GetPage(1));
            Assert.Equal("CONFIDENTIAL", string.Concat(letters.Select(l => l.Value)));
            var (left, right) = (letters[0].BoundingBox.Left, letters[^1].BoundingBox.Right);
            Assert.Equal(0.6 * 612, right - left, 612 * 0.03); // 60% of the width, give or take the letters' own spacing
            Assert.Equal(306, (left + right) / 2, 4.0);
        }
        // Black at a quarter: a light grey over the white page.
        using var picture = await Render(output);
        var darkest = Enumerable.Range(150, 310).Min(x => At(picture, x, 396).R);
        Assert.InRange(darkest, 175, 205);
    }

    [Fact]
    public async Task Diagonal_text_runs_from_the_bottom_left_towards_the_top_right()
    {
        using var scratch = new Scratch();
        var output = await Watermark(scratch.Pdf("report.pdf"));

        using var pdf = PigDocument.Open(output);
        var letters = Marks(pdf.GetPage(1));
        var (first, last) = (letters[0].StartBaseLine, letters[^1].EndBaseLine);
        Assert.Equal(Math.Atan2(792, 612), Math.Atan2(last.Y - first.Y, last.X - first.X), 0.02);
        Assert.Equal((306, 396), ((first.X + last.X) / 2, (first.Y + last.Y) / 2), (a, b) => Math.Abs(a.Item1 - b.Item1) < 15 && Math.Abs(a.Item2 - b.Item2) < 15);
    }

    [Fact]
    public async Task Tiled_text_covers_the_page_with_copies()
    {
        using var scratch = new Scratch();
        var output = await Watermark(scratch.Pdf("report.pdf"), ("layout", "tiled"), ("text", "DRAFT"));

        using var pdf = PigDocument.Open(output);
        var copies = Marks(pdf.GetPage(1)).Count(l => l.Value == "D");
        Assert.True(copies >= 6, $"only {copies} copies");
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Single(document.Pages[0].Resources.Elements.GetDictionary("/XObject")!.Elements.Keys, k => k.StartsWith("/TjForm", StringComparison.Ordinal)); // one form, placed many times
    }

    [Fact]
    public async Task A_picture_keeps_its_transparency()
    {
        using var scratch = new Scratch();
        var logo = scratch.TransparentPng("logo.png", 40); // a navy square in the middle of a transparent one

        var output = await Watermark(scratch.Pdf("report.pdf"), ("mark", "picture"), ("picture", logo), ("layout", "straight"), ("size", "50"), ("opacity", "100"));

        using var picture = await Render(output);
        var middle = At(picture, 306, 396);
        Assert.True(middle.B > 100 && middle.R < 60, $"the middle is {middle}");
        // The square's transparent edge, a quarter of the picture in from its side: the page shows through.
        var edge = At(picture, 306 - 153 + 20, 396);
        Assert.True(edge.R > 240 && edge.G > 240, $"the transparent part is {edge}");
    }

    [Fact]
    public async Task Only_the_listed_pages_get_it_and_a_page_it_hasnt_is_caught()
    {
        using var scratch = new Scratch();
        var pdf = scratch.Pdf("report.pdf", pages: 4);

        var output = await Watermark(pdf, ("pages", "2,4-"));

        using (var marked = PigDocument.Open(output))
            Assert.Equal([0, 12, 0, 12], marked.GetPages().Select(p => Marks(p).Count));
        var engine = Env.Engine();
        var problem = await engine.ValidateAsync([pdf], Env.Offered(engine, "watermark", [pdf]).WithParameters(new Dictionary<string, string> { ["pages"] = "3-6" }),
            TestContext.Current.CancellationToken);
        Assert.Equal(new SettingsProblem("pages", "There's no page 6: report.pdf has 4 pages."), problem);
    }

    [Fact]
    public async Task Under_the_content_a_filled_page_hides_it_and_over_it_shows()
    {
        using var scratch = new Scratch();
        var filled = scratch.PathOf("filled.pdf");
        using (var document = new PdfDocument())
        {
            RawPdf.Content(RawPdf.Page(document, 0, 0, 612, 792), "0 0 0 rg 100 100 412 592 re f");
            document.Save(filled);
        }

        foreach (var under in new[] { true, false })
        {
            var output = await Watermark(filled, ("layout", "straight"), ("colour", "#FF0000"), ("opacity", "100"), ("under", under.ToString()));
            using var picture = await Render(output);
            var reddest = Enumerable.Range(150, 310).Max(x => At(picture, x, 396).R);
            Assert.True(under ? reddest < 30 : reddest > 200, $"under={under}: {reddest}");
            File.Delete(output);
        }
    }

    [Fact]
    public async Task A_turned_page_gets_it_across_the_page_as_seen()
    {
        using var scratch = new Scratch();
        var output = await Watermark(scratch.RotatedPdf("turned.pdf", 90), ("layout", "straight"), ("colour", "#000000"), ("opacity", "100"));

        using var picture = await Render(output);
        Assert.Equal((792u, 612u), (picture.Width, picture.Height));
        var darkest = Enumerable.Range(200, 400).Min(x => At(picture, x, 306).R);
        Assert.True(darkest < 60, $"nothing dark across the middle: {darkest}");
    }

    [Fact]
    public async Task The_preview_shows_the_first_page_it_goes_on()
    {
        using var scratch = new Scratch();
        var pdf = scratch.Pdf("report.pdf", pages: 3);
        var engine = Env.Engine();
        var target = Env.Offered(engine, "watermark", [pdf]).WithParameters(new Dictionary<string, string> { ["pages"] = "2-3" });

        var preview = await engine.PreviewAsync([pdf], target, new PreviewRequest(300), TestContext.Current.CancellationToken);

        Assert.Equal([new Fact("Pages", "2 of 3")], preview!.Facts);
        using var picture = new MagickImage(preview.Image!);
        Assert.Equal(300u, picture.Height);
        Assert.Equal(["report.pdf"], scratch.Files());
    }
}

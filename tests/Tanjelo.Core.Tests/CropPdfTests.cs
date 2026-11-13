using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Tanjelo.Core.Tests;

/// <summary>Crop margins…: the visible box set to the content, or by set amounts, where the page is seen.</summary>
public class CropPdfTests
{
    const double Padding = 5 * 72 / 25.4; // the default 5 mm

    /// <summary>Letter pages, each with a black rectangle at a known place in its own coordinates, turned by <paramref name="rotation"/>.</summary>
    static string Boxed(Scratch scratch, string name, int rotation, params (double X0, double Y0, double X1, double Y1)[] rectangles)
    {
        var path = scratch.PathOf(name);
        using var document = new PdfDocument();
        foreach (var (x0, y0, x1, y1) in rectangles)
        {
            var page = RawPdf.Page(document, 0, 0, 612, 792);
            RawPdf.Content(page, FormattableString.Invariant($"0 0 0 rg {x0} {y0} {x1 - x0} {y1 - y0} re f"));
            if (rotation != 0) page.Elements.SetInteger("/Rotate", rotation);
        }
        document.Save(path);
        return path;
    }

    static async Task<string> Crop(string pdf, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "crop-pdf", [pdf]).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        var result = await engine.RunAsync([pdf], target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("\n", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    static List<(double, double, double, double)> CropBoxes(string pdf)
    {
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return document.Pages.Cast<PdfPage>().Select(p => (p.CropBox.X1, p.CropBox.Y1, p.CropBox.X2, p.CropBox.Y2)).ToList();
    }

    static void Near((double, double, double, double) expected, (double, double, double, double) actual, double within = 2.5)
    {
        var (a, b) = (new[] { expected.Item1, expected.Item2, expected.Item3, expected.Item4 }, new[] { actual.Item1, actual.Item2, actual.Item3, actual.Item4 });
        Assert.True(a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= within), $"expected {expected}, got {actual}");
    }

    static (double, double, double, double) Around((double X0, double Y0, double X1, double Y1) r) => (r.X0 - Padding, r.Y0 - Padding, r.X1 + Padding, r.Y1 + Padding);

    [Fact]
    public async Task Each_page_its_own_box_keeps_its_content_and_a_padding()
    {
        using var scratch = new Scratch();
        (double, double, double, double) first = (100, 200, 400, 600), second = (150, 300, 500, 700);

        var output = await Crop(Boxed(scratch, "report.pdf", 0, first, second), ("boxes", "each"));

        Assert.Equal(scratch.PathOf("report-cropped.pdf"), output);
        var boxes = CropBoxes(output);
        Near(Around(first), boxes[0]);
        Near(Around(second), boxes[1]);
        // Windows shows the page at the kept size.
        var document = await Converters.PdfConverter.LoadAsync(output);
        var (_, width, height) = await Converters.PdfConverter.RenderPagePngAsync(document, 0, 72, 4000);
        Assert.Equal((300 + 2 * Padding, 400 + 2 * Padding), (width, height), (a, b) => Math.Abs(a.Item1 - b.Item1) < 4 && Math.Abs(a.Item2 - b.Item2) < 4);
    }

    [Fact]
    public async Task One_box_for_all_holds_every_pages_content()
    {
        using var scratch = new Scratch();

        var output = await Crop(Boxed(scratch, "report.pdf", 0, (100, 200, 400, 600), (150, 300, 500, 700)));

        var boxes = CropBoxes(output);
        Near(Around((100, 200, 500, 700)), boxes[0]);
        Near(Around((100, 200, 500, 700)), boxes[1]);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Turned_pages_are_cropped_around_their_content_too(int rotation)
    {
        using var scratch = new Scratch();
        (double, double, double, double) content = (100, 200, 400, 600);

        var output = await Crop(Boxed(scratch, "turned.pdf", rotation, content));

        // In the page's own coordinates, the box is around the same rectangle whichever way it's shown.
        Near(Around(content), CropBoxes(output)[0]);
    }

    [Fact]
    public async Task Set_amounts_come_off_each_side_of_the_page_as_seen()
    {
        using var scratch = new Scratch();
        var mm = 72 / 25.4;

        var upright = await Crop(Boxed(scratch, "upright.pdf", 0, (100, 200, 400, 600)), ("mode", "amounts"), ("top", "10"), ("bottom", "20"), ("left", "30"), ("right", "40"));
        var turned = await Crop(Boxed(scratch, "turned.pdf", 90, (100, 200, 400, 600)), ("mode", "amounts"), ("top", "10"), ("bottom", "20"), ("left", "30"), ("right", "40"));

        Near((30 * mm, 20 * mm, 612 - 40 * mm, 792 - 10 * mm), CropBoxes(upright)[0], 0.5);
        // Turned a quarter clockwise, the page's left edge is at the top as it's seen, and its bottom edge on the left.
        Near((10 * mm, 30 * mm, 612 - 20 * mm, 792 - 40 * mm), CropBoxes(turned)[0], 0.5);
    }

    [Fact]
    public async Task A_page_with_no_margins_writes_nothing_and_amounts_that_leave_nothing_are_refused()
    {
        using var scratch = new Scratch();
        var full = Boxed(scratch, "full.pdf", 0, (0, 0, 612, 792));
        var engine = Env.Engine();

        var nothing = await engine.RunAsync([full], Env.Offered(engine, "crop-pdf", [full]), ct: TestContext.Current.CancellationToken);
        var everything = await engine.RunAsync([full], Env.Offered(engine, "crop-pdf", [full])
            .WithParameters(new Dictionary<string, string> { ["mode"] = "amounts", ["left"] = "150", ["right"] = "100" }), ct: TestContext.Current.CancellationToken);

        Assert.Equal("There are no margins to crop.", Assert.Single(nothing.Failures).Message);
        Assert.Equal("The amounts leave nothing of page 1.", Assert.Single(everything.Failures).Message);
        Assert.Equal(["full.pdf"], scratch.Files());
    }

    [Fact]
    public async Task The_preview_outlines_what_is_kept()
    {
        using var scratch = new Scratch();
        var pdf = Boxed(scratch, "report.pdf", 0, (100, 200, 400, 600));
        var engine = Env.Engine();

        var preview = await engine.PreviewAsync([pdf], Env.Offered(engine, "crop-pdf", [pdf]), new PreviewRequest(400), TestContext.Current.CancellationToken);

        Assert.Equal([new Fact("Kept", "116 × 151 mm of 216 × 279 mm")], preview!.Facts);
        using var picture = new ImageMagick.MagickImage(preview.Image!);
        var outside = picture.GetPixels().GetPixel(5, 5).ToColor()!;
        Assert.True(outside.R < 200, $"the margin isn't shaded: {outside}");
    }
}

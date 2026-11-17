using ImageMagick;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Content;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Tests;

/// <summary>Make searchable, and searchable PDFs from pictures: scanned pages get their words as invisible text.</summary>
public class SearchablePdfTests
{
    const string NoOcr = "Windows has no OCR language here";

    // ScannedPdf draws a picture of 1700 × 2200 pixels at 200 an inch, a Letter page; its first line starts 170 pixels in
    // and 200 down, in 42-pixel Arial: 61 points from the left and 72 from the top.
    static readonly string[] Lines = ["Quarterly report", "Sales went up this quarter"];

    static Word Find(Page page, string text) => page.GetWords().First(w => w.Text == text);

    [Fact]
    public async Task A_scan_gets_its_words_as_invisible_text_where_they_show()
    {
        Assert.SkipUnless(Env.Tools.Ocr, NoOcr);
        using var scratch = new Scratch();
        var scan = scratch.ScannedPdf("scan.pdf", Lines);

        var result = await Env.Run("searchable-pdf", scan);

        var output = Assert.Single(result.Outputs);
        Assert.Equal(scratch.PathOf("scan-searchable.pdf"), output);
        Assert.Equal(["1 page read with OCR"], result.Notes);
        using (var pdf = PigDocument.Open(output))
        {
            var page = pdf.GetPage(1);
            var quarterly = Find(page, "Quarterly");
            Assert.Equal(61, quarterly.BoundingBox.Left, 6.0);
            Assert.InRange(quarterly.BoundingBox.Top, 792 - 72 - 18, 792 - 72 + 6);
            Assert.True(Find(page, "Sales").BoundingBox.Top < quarterly.BoundingBox.Bottom, "the second line is below the first");
            Assert.All(page.Letters, l => Assert.Equal(UglyToad.PdfPig.Core.TextRenderingMode.Neither, l.RenderingMode));
            Assert.Contains("went", page.GetWords().Select(w => w.Text));
        }
        // It looks just as it did.
        using var before = await Render(scan);
        using var after = await Render(output);
        Assert.True(before.Compare(after, ErrorMetric.MeanAbsolute) < 0.002);
    }

    static async Task<MagickImage> Render(string pdf)
    {
        var document = await Converters.PdfConverter.LoadAsync(pdf);
        return new MagickImage((await Converters.PdfConverter.RenderPagePngAsync(document, 0, 50, 2000)).Png);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public async Task A_scan_turned_upright_by_its_page_gets_its_words_where_they_show(int rotation)
    {
        Assert.SkipUnless(Env.Tools.Ocr, NoOcr);
        using var scratch = new Scratch();

        var output = await Env.RunOne("searchable-pdf", scratch.TurnedScanPdf("turned.pdf", rotation, Lines));

        // PdfPig gives positions as the page shows, turned: the same as on the upright scan.
        using var pdf = PigDocument.Open(output);
        var quarterly = Find(pdf.GetPage(1), "Quarterly");
        Assert.Equal(61, quarterly.BoundingBox.Left, 6.0);
        Assert.InRange(quarterly.BoundingBox.Top, 792 - 72 - 18, 792 - 72 + 6);
    }

    [Fact]
    public async Task Pages_with_text_are_left_alone_and_a_pdf_with_text_on_every_page_writes_nothing()
    {
        Assert.SkipUnless(Env.Tools.Ocr, NoOcr);
        using var scratch = new Scratch();
        var text = scratch.BookmarkedPdf("text.pdf", ("Introduction", 1));
        var scan = scratch.ScannedPdf("scan.pdf", Lines);
        var mixed = scratch.PathOf("mixed.pdf");
        using (var both = new PdfSharp.Pdf.PdfDocument())
        {
            foreach (var path in new[] { text, scan })
            {
                using var source = PdfReader.Open(path, PdfDocumentOpenMode.Import);
                both.AddPage(source.Pages[0]);
            }
            both.Save(mixed);
        }

        var output = await Env.RunOne("searchable-pdf", mixed);

        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            Assert.DoesNotContain(document.Pages[0].Resources.Elements.GetDictionary("/XObject")?.Elements.Keys ?? [], k => k.StartsWith("/TjForm", StringComparison.Ordinal));
            Assert.Contains(document.Pages[1].Resources.Elements.GetDictionary("/XObject")!.Elements.Keys, k => k.StartsWith("/TjForm", StringComparison.Ordinal));
        }
        var engine = Env.Engine();
        var again = await engine.RunAsync([text], Env.Offered(engine, "searchable-pdf", [text]), ct: TestContext.Current.CancellationToken);
        Assert.Equal("Every page already has text.", Assert.Single(again.Failures).Message);
        Assert.DoesNotContain("text-searchable.pdf", scratch.Files());
    }

    [Fact]
    public void Without_an_OCR_language_the_tool_is_hidden_and_the_hint_says_what_to_add()
    {
        using var scratch = new Scratch();
        var scan = scratch.ScannedPdf("scan.pdf", Lines);
        var photo = scratch.Image("photo.png");
        var engine = new ConverterEngine(new Toolbox { Ocr = false });

        Assert.DoesNotContain("searchable-pdf", engine.GetMenu([scan]).Targets.Select(t => t.Key));
        Assert.DoesNotContain("searchable-pdf", engine.GetMenu([photo]).Targets.Select(t => t.Key));
        Assert.DoesNotContain("combine-searchable-pdf", engine.GetMenu([photo, scratch.Image("second.png")]).Targets.Select(t => t.Key));
        if (!Converters.PdfText.CanOcr) // the hint looks at Windows itself
            Assert.Contains(Converters.PdfText.NoOcrHint, engine.GetMenu([scan]).Hints);
        Assert.Contains("searchable-pdf", new ConverterEngine(new Toolbox { Ocr = true }).GetMenu([scan]).Targets.Select(t => t.Key));
    }

    [Fact]
    public async Task Pictures_become_searchable_pdfs_one_or_several_at_a_time()
    {
        Assert.SkipUnless(Env.Tools.Ocr, NoOcr);
        using var scratch = new Scratch();
        var first = scratch.TextPicture("first.png", 1200, 500, "Quarterly report");
        var second = scratch.TextPicture("second.jpg", 1200, 500, "Sales went up");

        var one = await Env.RunOne("searchable-pdf", first);
        var both = await Env.RunOne("combine-searchable-pdf", first, second);

        Assert.Equal(scratch.PathOf("first.pdf"), one);
        Assert.Equal(scratch.PathOf("Combined.pdf"), both);
        using (var pdf = PigDocument.Open(one))
            Assert.Equal(["Quarterly", "report"], pdf.GetPage(1).GetWords().Select(w => w.Text));
        using (var pdf = PigDocument.Open(both))
        {
            Assert.Equal(2, pdf.NumberOfPages);
            Assert.Contains("Sales", pdf.GetPage(2).GetWords().Select(w => w.Text));
        }
    }

    [Fact]
    public async Task Tanj_can_ask_for_another_installed_language_and_lists_them()
    {
        Assert.SkipUnless(Env.Tools.Ocr, NoOcr);
        using var scratch = new Scratch();
        var scan = scratch.ScannedPdf("scan.pdf", Lines);
        var engine = Env.Engine();
        var target = Env.Offered(engine, "searchable-pdf", [scan]);
        var installed = Toolbox.OcrLanguages();

        var listed = new StringWriter();
        Tanj.OptionList.Write(target, listed);
        Assert.Contains($"--language: an OCR language Windows has ({string.Join(", ", installed)})", listed.ToString());

        var refused = await engine.RunAsync([scan], target.WithParameters(new Dictionary<string, string> { ["language"] = "xx-YY" }), ct: TestContext.Current.CancellationToken);
        Assert.StartsWith("Windows has no OCR language 'xx-YY'. Installed: ", Assert.Single(refused.Failures).Message);
        var read = await engine.RunAsync([scan], target.WithParameters(new Dictionary<string, string> { ["language"] = installed[0] }), ct: TestContext.Current.CancellationToken);
        Assert.Single(read.Outputs);
    }
}

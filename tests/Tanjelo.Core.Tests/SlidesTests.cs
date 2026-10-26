using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using PdfSharp.Drawing;
using Tanjelo.Core.Converters;
using A = DocumentFormat.OpenXml.Drawing;

namespace Tanjelo.Core.Tests;

public class SlidesTests
{
    /// <summary>The slides of a deck in order: the picture's size and place, and the speaker notes.</summary>
    static List<(long X, long Y, long Width, long Height, string Notes)> Slides(string pptx, out long slideWidth, out long slideHeight)
    {
        using var deck = PresentationDocument.Open(pptx, false);
        var presentation = deck.PresentationPart!;
        var size = presentation.Presentation!.SlideSize!;
        (slideWidth, slideHeight) = (size.Cx!.Value, size.Cy!.Value);
        return presentation.Presentation.SlideIdList!.Elements<DocumentFormat.OpenXml.Presentation.SlideId>()
            .Select(id => (SlidePart)presentation.GetPartById(id.RelationshipId!))
            .Select(slide =>
            {
                var picture = slide.Slide!.Descendants<DocumentFormat.OpenXml.Presentation.Picture>().Single();
                var xfrm = picture.Descendants<A.Transform2D>().Single();
                return (xfrm.Offset!.X!.Value, xfrm.Offset.Y!.Value, xfrm.Extents!.Cx!.Value, xfrm.Extents.Cy!.Value,
                    string.Join("\n", slide.NotesSlidePart?.NotesSlide?.Descendants<A.Paragraph>().Select(p => p.InnerText) ?? []));
            })
            .ToList();
    }

    static void AssertValid(string pptx)
    {
        using var deck = PresentationDocument.Open(pptx, false);
        var errors = new OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Microsoft365).Validate(deck)
            .Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    static string ThreePages(Scratch scratch) => scratch.TextPdf("talk.pdf",
        g => g.DrawString("Welcome to the talk", new XFont("Arial", 20), XBrushes.Black, 60, 80),
        g => g.DrawString("The middle part", new XFont("Arial", 20), XBrushes.Black, 60, 80),
        g => g.DrawString("Thank you", new XFont("Arial", 20), XBrushes.Black, 60, 80));

    [Fact]
    public async Task A_pdf_becomes_one_slide_per_page_the_size_of_its_pages()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("pptx", ThreePages(scratch));

        Assert.Equal(scratch.PathOf("talk.pptx"), output);
        var slides = Slides(output, out var width, out var height);
        Assert.Equal(3, slides.Count);
        // A4 is 210 x 297 mm.
        Assert.Equal(210.0, width / 36000.0, 0);
        Assert.Equal(297.0, height / 36000.0, 0);
        Assert.All(slides, s => Assert.Equal((0L, 0L, width, height), (s.X, s.Y, s.Width, s.Height)));
        AssertValid(output);
    }

    [Fact]
    public async Task Each_page_s_text_is_in_its_speaker_notes()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("pptx", ThreePages(scratch));

        Assert.Equal(["Welcome to the talk", "The middle part", "Thank you"], Slides(output, out _, out _).Select(s => s.Notes));
    }

    [Fact]
    public async Task A_scanned_pdf_still_makes_slides()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("pptx", scratch.ScannedPdf("scan.pdf", "Only a picture of text"));

        var slide = Assert.Single(Slides(output, out _, out _));
        Assert.Equal("", slide.Notes);
        AssertValid(output);
    }

    [Fact]
    public async Task A_password_protected_pdf_says_so_and_leaves_nothing()
    {
        using var scratch = new Scratch();
        var pdf = scratch.TextPdf("secret.pdf", "open sesame", g => g.DrawString("Hidden", new XFont("Arial", 12), XBrushes.Black, 60, 80));
        var engine = Env.Engine();

        var result = await engine.RunAsync([pdf], Env.Offered(engine, "pptx", [pdf]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("This PDF is password-protected.", Assert.Single(result.Failures).Message);
        Assert.Equal(["secret.pdf"], scratch.Files());
    }

    [Fact]
    public async Task Pictures_become_one_deck_shaped_like_the_first()
    {
        using var scratch = new Scratch();
        var wide = scratch.Image("wide.png", 400, 300);
        var tall = scratch.Image("tall.jpg", 300, 600);
        var photo = scratch.Photo("photo.webp", 320, 240);

        var output = await Env.RunOne("combine-pptx", wide, tall, photo);

        Assert.Equal(scratch.PathOf("Combined.pptx"), output);
        var slides = Slides(output, out var width, out var height);
        Assert.Equal((12192000L, 9144000L), (width, height)); // 13.33 x 10 inches, 4:3 like wide.png
        Assert.Equal(3, slides.Count);
        Assert.Equal((0L, 0L, width, height), (slides[0].X, slides[0].Y, slides[0].Width, slides[0].Height));
        // The tall picture keeps its shape, centred.
        Assert.Equal((height, 4572000L), (slides[1].Height, slides[1].Width));
        Assert.Equal((width - 4572000L) / 2, slides[1].X);
        AssertValid(output);
    }

    [Fact]
    public void Upright_jpegs_go_in_untouched_and_sideways_ones_are_turned()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.jpg", 320, 240);
        var sideways = scratch.Sideways("portrait.jpg", 120, 80);

        var (bytes, jpeg, _, _) = Pictures.ForSlide(photo);
        var (_, turnedJpeg, width, height) = Pictures.ForSlide(sideways);

        Assert.True(jpeg);
        Assert.Equal(File.ReadAllBytes(photo), bytes);
        Assert.True(turnedJpeg);
        Assert.Equal((80, 120), (width, height));
    }

    [Fact]
    public void Images_offer_one_pptx_only_for_two_or_more()
    {
        using var scratch = new Scratch();
        var a = scratch.Image("a.png");
        var b = scratch.Image("b.jpg");

        Assert.Contains("combine-pptx", Env.Keys(a, b));
        Assert.DoesNotContain("combine-pptx", Env.Keys(a));
    }

    [Theory]
    [InlineData(7772400, 10058400, 7772400, 10058400)]    // US Letter fits as it is
    [InlineData(457200, 457200, 914400, 914400)]          // half an inch grows to the 1 inch minimum
    [InlineData(102412800, 51206400, 51206400, 25603200)] // 112 inches wide shrinks to the 56 inch maximum
    public void Slides_stay_within_powerpoint_s_limits(long width, long height, long fitWidth, long fitHeight) =>
        Assert.Equal((fitWidth, fitHeight), SlideDeck.Fit(width, height));
}

[Collection("Office")]
public class SlidesInPowerPointTests
{
    [Fact]
    public async Task PowerPoint_opens_the_deck_without_repairing_it()
    {
        Assert.SkipUnless(Env.Tools.PowerPoint, Env.NoPowerPoint);
        using var scratch = new Scratch();
        var pdf = scratch.TextPdf("deck.pdf",
            g => g.DrawString("One", new XFont("Arial", 20), XBrushes.Black, 60, 80),
            g => g.DrawString("Two", new XFont("Arial", 20), XBrushes.Black, 60, 80));
        var pptx = await Env.RunOne("pptx", pdf);
        File.Delete(pdf);

        // PowerPoint opens the file read-only (from a temp copy, under the watchdog) and exports it.
        var exported = await Env.RunOne("pdf", pptx);

        Assert.Equal(2, Inspect.PdfPages(exported));
    }
}

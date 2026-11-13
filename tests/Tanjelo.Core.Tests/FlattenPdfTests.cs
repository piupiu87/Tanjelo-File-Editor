using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Tests;

/// <summary>Flatten: forms and comments drawn into the page, as Windows draws them before.</summary>
public class FlattenPdfTests
{
    static async Task<BatchResult> Flatten(string pdf)
    {
        var engine = Env.Engine();
        return await engine.RunAsync([pdf], Env.Offered(engine, "flatten-pdf", [pdf]), ct: TestContext.Current.CancellationToken);
    }

    static async Task<MagickImage> Render(string pdf)
    {
        var document = await Converters.PdfConverter.LoadAsync(pdf);
        return new MagickImage((await Converters.PdfConverter.RenderPagePngAsync(document, 0, 72, 2000)).Png);
    }

    static List<PdfDictionary> Annotations(PdfDocument document) =>
        document.Pages[0].Elements.GetArray("/Annots")?.Elements.Select(a => (PdfDictionary)((PdfReference)a).Value).ToList() ?? [];

    [Fact]
    public async Task Forms_and_comments_are_drawn_into_the_page_and_look_the_same()
    {
        using var scratch = new Scratch();
        var pdf = scratch.FormPdf("form.pdf");

        var result = await Flatten(pdf);

        var output = Assert.Single(result.Outputs);
        Assert.Equal(scratch.PathOf("form-flattened.pdf"), output);
        Assert.Empty(result.Notes);
        using (var document = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            // Only the link is left, and the form has gone with its fields.
            Assert.Equal(["/Link"], Annotations(document).Select(a => a.Elements.GetName("/Subtype")));
            Assert.Equal("https://example.com/", Annotations(document)[0].Elements.GetDictionary("/A")!.Elements.GetString("/URI"));
            Assert.False(document.Internals.Catalog.Elements.ContainsKey("/AcroForm"));
        }
        using (var flattened = PigDocument.Open(output))
            Assert.Contains("Filled in", flattened.GetPage(1).Text);

        // Windows draws the form, the tick and the comments the same before and after; the hidden comment neither time.
        using var before = await Render(pdf);
        using var after = await Render(output);
        Assert.True(before.Compare(after, ErrorMetric.MeanAbsolute) < 0.003, $"differs by {before.Compare(after, ErrorMetric.MeanAbsolute):P2}");
        var tick = after.GetPixels().GetPixel(108, 792 - 608).ToColor()!;
        Assert.True(tick.R < 80, $"the tick isn't there: {tick}");
        var square = after.GetPixels().GetPixel(302, 792 - 530).ToColor()!;
        Assert.True(square.R > 200 && square.G < 80, $"the red comment isn't there: {square}");
        var hidden = after.GetPixels().GetPixel(350, 792 - 430).ToColor()!;
        Assert.True(hidden.B > 240 && hidden.R > 240, $"the hidden comment shows: {hidden}");
    }

    [Fact]
    public async Task A_text_field_without_an_appearance_gets_one_from_its_value()
    {
        using var scratch = new Scratch();

        var output = Assert.Single((await Flatten(scratch.FormPdf("form.pdf", needAppearances: true))).Outputs);

        using (var flattened = PigDocument.Open(output))
        {
            var words = flattened.GetPage(1).GetWords().ToList();
            var filled = words.First(w => w.Text == "Filled");
            Assert.InRange(filled.BoundingBox.Left, 100, 106); // in the field, at its left
            Assert.InRange(filled.BoundingBox.Bottom, 650, 668);
            Assert.Equal(12, filled.Letters[0].PointSize, 0.5); // the size its /DA gives
        }
        using var document = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.False(document.Internals.Catalog.Elements.ContainsKey("/AcroForm"));
    }

    [Fact]
    public async Task A_field_with_nothing_to_show_it_by_is_left_and_counted()
    {
        using var scratch = new Scratch();
        var pdf = scratch.FormPdf("form.pdf");
        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            Annotations(document)[1].Elements.Remove("/AP"); // the check box
            document.Save(pdf);
        }

        var result = await Flatten(pdf);

        Assert.Equal("1 form field had nothing to show it by, so it was left as it is.", Assert.Single(result.Notes));
        using var flattened = PdfReader.Open(Assert.Single(result.Outputs), PdfDocumentOpenMode.Import);
        Assert.Equal(["/Widget", "/Link"], Annotations(flattened).Select(a => a.Elements.GetName("/Subtype")));
        var fields = flattened.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!;
        Assert.Equal("agree", ((PdfDictionary)((PdfReference)Assert.Single(fields.Elements)).Value).Elements.GetString("/T"));
    }

    [Fact]
    public async Task XFA_forms_are_refused_and_a_pdf_without_forms_or_comments_writes_nothing()
    {
        using var scratch = new Scratch();
        var xfa = scratch.FormPdf("xfa.pdf", xfa: true);
        var plain = scratch.BookmarkedPdf("plain.pdf", ("Introduction", 1));

        Assert.Equal("This is an XFA form, which Tanjelo can't flatten. Open it in a PDF reader and print it to PDF instead.",
            Assert.Single((await Flatten(xfa)).Failures).Message);
        Assert.Equal("There's nothing to flatten: no form fields or comments.", Assert.Single((await Flatten(plain)).Failures).Message);
        Assert.Equal(["plain.pdf", "xfa.pdf"], scratch.Files());
    }

    [Fact]
    public async Task A_signed_pdf_keeps_its_invisible_signature_and_says_it_wont_hold()
    {
        using var scratch = new Scratch();
        var signed = scratch.SignedPdf("signed.pdf");
        Assert.Equal("There's nothing to flatten: no form fields or comments.", Assert.Single((await Flatten(signed)).Failures).Message);
        using (var document = PdfReader.Open(signed, PdfDocumentOpenMode.Modify))
        {
            var comment = RawPdf.Annotation(document, document.Pages[0], "/Square", 300, 500, 400, 560);
            RawPdf.Appearance(document, comment, 100, 60, "1 0 0 RG 4 w 2 2 96 56 re S");
            document.Save(signed);
        }

        var result = await Flatten(signed);

        Assert.Equal("This PDF was digitally signed, and the signature isn't valid in the changed copy.", Assert.Single(result.Notes));
        using var flattened = PdfReader.Open(Assert.Single(result.Outputs), PdfDocumentOpenMode.Import);
        Assert.Equal(["/Widget"], Annotations(flattened).Select(a => a.Elements.GetName("/Subtype")));
        Assert.True(flattened.Internals.Catalog.Elements.ContainsKey("/AcroForm"));
    }
}

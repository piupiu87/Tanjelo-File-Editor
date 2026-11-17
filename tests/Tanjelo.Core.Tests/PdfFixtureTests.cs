using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Outline;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Tests;

/// <summary>The PDF fixtures the PDF tools are tested on are what they say they are, read back by PdfPig, PDFsharp and Windows.</summary>
public class PdfFixtureTests
{
    [Fact]
    public void Rotated_pages_keep_their_text_in_the_pages_own_coordinates()
    {
        using var scratch = new Scratch();
        using var pdf = PigDocument.Open(scratch.RotatedPdf("turned.pdf", 0, 90, 180, 270));

        Assert.Equal([0, 90, 180, 270], Enumerable.Range(1, 4).Select(n => pdf.GetPage(n).Rotation.Value));
        var letters = pdf.GetPage(2).Letters;
        Assert.Equal("Rotated 90", string.Concat(letters.Select(l => l.Value)));
        // PdfPig gives positions as the page shows, turned: 700 from the left, 72 from the top of a page 612 high.
        Assert.Equal(700, letters[0].StartBaseLine.X, 0.5);
        Assert.Equal(540, letters[0].StartBaseLine.Y, 0.5);
    }

    [Fact]
    public void Boxes_put_the_visible_part_away_from_the_origin()
    {
        using var scratch = new Scratch();
        var path = scratch.BoxesPdf("boxes.pdf");
        using var pdf = PigDocument.Open(path);

        Assert.Equal((100, 150, 500, 700), Box(pdf.GetPage(1).CropBox.Bounds));
        Assert.Equal((200, 300, 812, 1092), Box(pdf.GetPage(2).MediaBox.Bounds));
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.Equal(2, document.Pages[2].Elements.GetReal("/UserUnit"));
        Assert.Equal(3, Inspect.PdfPages(path));

        static (double, double, double, double) Box(UglyToad.PdfPig.Core.PdfRectangle r) => (r.Left, r.Bottom, r.Right, r.Top);
    }

    [Fact]
    public void The_changed_state_pages_leave_their_graphics_state_unbalanced()
    {
        using var scratch = new Scratch();
        using var document = PdfReader.Open(scratch.ChangedStatePdf("state.pdf"), PdfDocumentOpenMode.Import);

        Assert.Equal(1, Balance(document.Pages[0]));
        Assert.Equal(-1, Balance(document.Pages[1]));

        static int Balance(PdfPage page) => ContentReader.ReadContent(page).OfType<COperator>().Sum(o => o.Name == "q" ? 1 : o.Name == "Q" ? -1 : 0);
    }

    [Fact]
    public void The_form_has_fields_comments_and_a_link()
    {
        using var scratch = new Scratch();
        foreach (var needAppearances in new[] { false, true })
        {
            using var document = PdfReader.Open(scratch.FormPdf($"form-{needAppearances}.pdf", needAppearances), PdfDocumentOpenMode.Import);
            var annotations = document.Pages[0].Elements.GetArray("/Annots")!.Elements.Select(a => (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)a).Value).ToList();
            Assert.Equal(["/Widget", "/Widget", "/Text", "/Popup", "/Square", "/Square", "/Link"], annotations.Select(a => a.Elements.GetName("/Subtype")));
            Assert.Equal(!needAppearances, annotations[0].Elements.ContainsKey("/AP"));
            var form = document.Internals.Catalog.Elements.GetDictionary("/AcroForm")!;
            Assert.Equal(2, form.Elements.GetArray("/Fields")!.Elements.Count);
            Assert.Equal(needAppearances, form.Elements.GetBoolean("/NeedAppearances"));
        }
        using var xfa = PdfReader.Open(scratch.FormPdf("xfa.pdf", xfa: true), PdfDocumentOpenMode.Import);
        Assert.True(xfa.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.ContainsKey("/XFA"));
    }

    [Theory]
    [InlineData("aes256", "%PDF-2.0")]
    [InlineData("aes128", "%PDF-1.")]
    [InlineData("rc4", "%PDF-1.")]
    public void Encrypted_pdfs_open_only_with_their_password(string encryption, string header)
    {
        using var scratch = new Scratch();
        var locked = scratch.EncryptedPdf("locked.pdf", encryption, "open-1234");
        var limited = scratch.EncryptedPdf("limited.pdf", encryption, null, "perm-5678", limits: true);

        Assert.StartsWith(header, File.ReadAllText(locked)[..9]);
        Assert.Throws<PdfReaderException>(() => PdfReader.Open(locked, PdfDocumentOpenMode.Import));
        using (PdfReader.Open(locked, "open-1234", PdfDocumentOpenMode.Modify)) { }
        using (var pdf = PigDocument.Open(locked, new UglyToad.PdfPig.ParsingOptions { Password = "open-1234" }))
            Assert.Equal("Locked text", pdf.GetPage(1).Text);
        using (PdfReader.Open(limited, PdfDocumentOpenMode.Import)) { }
        Assert.Contains("owner password", Assert.Throws<PdfReaderException>(() => PdfReader.Open(limited, PdfDocumentOpenMode.Modify)).Message);
        using (PdfReader.Open(limited, "perm-5678", PdfDocumentOpenMode.Modify)) { }
    }

    [Fact]
    public void The_pictures_pdf_has_every_kind_of_picture()
    {
        using var scratch = new Scratch();
        var path = scratch.PicturesPdf("pictures.pdf");
        using var pdf = PigDocument.Open(path);

        var first = pdf.GetPage(1).GetImages().Select(i => i.WidthInSamples).Order().ToList();
        Assert.Equal([10, 400, 560, 640, 1200, 1200], first);
        var second = pdf.GetPage(2).GetImages().ToList();
        Assert.Contains(second, i => i.WidthInSamples == 200 && i.MaskImage is not null);
        Assert.Contains(second, i => i.WidthInSamples == 96 && i.IsImageMask);
        Assert.Contains(second, i => i.WidthInSamples == 408 && i.BitsPerComponent == 1);
        Assert.Contains("Pictures page two", pdf.GetPage(2).Text);
        Assert.Single(pdf.TryGetBookmarks(out var bookmarks) ? bookmarks.Roots : []);
        Assert.Equal(2, Inspect.PdfPages(path));
    }

    [Fact]
    public void Bookmarks_mark_each_chapter_with_one_beneath()
    {
        using var scratch = new Scratch();
        using var pdf = PigDocument.Open(scratch.BookmarkedPdf("book.pdf", ("Introduction", 2), ("Methods", 3), ("Results", 1)));

        Assert.True(pdf.TryGetBookmarks(out var bookmarks));
        Assert.Equal(["Introduction", "Methods", "Results"], bookmarks.Roots.Select(b => b.Title));
        Assert.Equal(["Introduction details"], bookmarks.Roots[0].Children.Select(b => b.Title));
        Assert.Equal(6, pdf.NumberOfPages);
        Assert.Equal(3, ((DocumentBookmarkNode)bookmarks.Roots[1]).PageNumber);
    }

    [Fact]
    public void The_signed_pdf_has_a_signature_field_with_a_value()
    {
        using var scratch = new Scratch();
        using var document = PdfReader.Open(scratch.SignedPdf("signed.pdf"), PdfDocumentOpenMode.Import);

        var field = (PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)document.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!.Elements[0]).Value;
        Assert.Equal("/Sig", field.Elements.GetName("/FT"));
        Assert.Equal("/Sig", field.Elements.GetDictionary("/V")!.Elements.GetName("/Type"));
    }
}

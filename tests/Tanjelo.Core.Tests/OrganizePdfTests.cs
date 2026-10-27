using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Tanjelo.Core.Converters;
using static Tanjelo.Core.Converters.PageList;

namespace Tanjelo.Core.Tests;

public class OrganizePdfTests
{
    static readonly string[] Names = ["doc.pdf", "more.pdf"];

    [Fact]
    public void A_page_list_has_ranges_both_ways_turns_copies_and_other_pdfs()
    {
        Assert.Equal(
            [new Page(0, 2), new Page(0, 0), new Page(0, 1), new Page(0, 4, 90), new Page(0, 0)],
            Parse("3,1-2,5r90,1", [5], Names));
        Assert.Equal([new Page(0, 2), new Page(0, 1), new Page(0, 0)], Parse("3-1", [5], Names));
        Assert.Equal([new Page(0, 0, 180), new Page(0, 1, 180), new Page(1, 1), new Page(1, 2, 270)], Parse(" 1-2r180 ; B2  b3R270", [5, 3], Names));
    }

    [Theory]
    [InlineData("1,x", "Couldn't read 'x' in the page list. Use page numbers and ranges such as 3,1-2,5r90.")]
    [InlineData("2r45", "Couldn't read '2r45' in the page list. Use page numbers and ranges such as 3,1-2,5r90.")]
    [InlineData("1,6", "There's no page 6: doc.pdf has 5 pages.")]
    [InlineData("0", "There's no page 0: doc.pdf has 5 pages.")]
    [InlineData("b1", "'b1' is a page of another PDF, but no other PDF was given.")]
    [InlineData(" , ", "The page list leaves no pages.")]
    public void A_bad_page_list_says_what_is_wrong(string list, string message)
    {
        var error = Assert.Throws<ConversionException>(() => Parse(list, [5], Names));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void A_page_list_is_written_with_ranges_and_reads_back_the_same()
    {
        Page[] pages = [new(0, 2), new(0, 0), new(0, 1), new(0, 1, 90), new(0, 6), new(0, 5), new(0, 4), new(1, 0), new(1, 1), new(0, 7, 90), new(0, 8, 90)];
        var text = Format(pages);
        Assert.Equal("3,1-2,2r90,7-5,b1-2,8-9r90", text);
        Assert.Equal(pages, Parse(text, [9, 2], Names));
    }

    /// <summary>A PDF whose pages say "Page 1"…, with bookmarks: One (page 1), Three (page 3) holding Four (page 4), and Five.</summary>
    internal static string Numbered(Scratch scratch, string name = "doc.pdf", int pages = 5, bool bookmarks = true, string prefix = "Page")
    {
        PdfFonts.Ready();
        var path = scratch.PathOf(name);
        using var document = new PdfDocument();
        var font = new XFont("Arial", 40);
        for (var i = 1; i <= pages; i++)
        {
            var page = document.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString($"{prefix} {i}", font, XBrushes.Black, new XPoint(100, 200));
        }
        if (bookmarks && pages >= 5)
        {
            document.Outlines.Add("One", document.Pages[0], true);
            var three = document.Outlines.Add("Three", document.Pages[2], true);
            three.Outlines.Add("Four", document.Pages[3]);
            document.Outlines.Add("Five", document.Pages[4]);
        }
        document.Save(path);
        return path;
    }

    static string[] Texts(string pdf)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return document.GetPages().Select(p => p.Text.Trim()).ToArray();
    }

    static int[] Rotations(string pdf)
    {
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return document.Pages.Cast<PdfPage>().Select(p => p.Elements.GetInteger("/Rotate")).ToArray();
    }

    static string Outline(string pdf)
    {
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        static string Write(IReadOnlyList<OrganizePdfTool.Bookmark> marks) =>
            string.Join(" ", marks.Select(m => $"{m.Title}:{(m.Page is { } p ? p + 1 : 0)}" + (m.Children.Count > 0 ? $"[{Write(m.Children)}]" : "")));
        return Write(OrganizePdfTool.Bookmarks.Read(document));
    }

    static async Task<BatchResult> Run(string pdf, string pages, string with = "")
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "organize-pdf", [pdf]).WithParameters(new Dictionary<string, string> { ["pages"] = pages, ["with"] = with });
        return await engine.RunAsync([pdf], target, ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pages_come_out_in_the_new_order_turned_and_copied_with_their_bookmarks()
    {
        using var scratch = new Scratch();
        var pdf = Numbered(scratch);
        Assert.Equal("One:1 Three:3[Four:4] Five:5", Outline(pdf));

        var result = await Run(pdf, "3,1-2,2r90");
        var output = Assert.Single(result.Outputs);
        Assert.Empty(result.Failures);
        Assert.Equal("doc-organized.pdf", Path.GetFileName(output));
        Assert.Equal(["Page 3", "Page 1", "Page 2", "Page 2"], Texts(output));
        Assert.Equal([0, 0, 0, 90], Rotations(output)); // the copy turned, not the page it was copied from
        // Four and Five were left out; Three keeps its place, now first.
        Assert.Equal("One:2 Three:1", Outline(output));
        Assert.Equal(["Page 1", "Page 2", "Page 3", "Page 4", "Page 5"], Texts(pdf)); // the original is untouched
    }

    [Fact]
    public async Task Turning_adds_to_a_page_that_was_already_turned_and_a_deleted_bookmark_lets_its_children_up()
    {
        using var scratch = new Scratch();
        var pdf = Numbered(scratch);
        var turned = Assert.Single((await Run(pdf, "1-5r270")).Outputs);
        var again = Assert.Single((await Run(turned, "4r180,5r90")).Outputs);
        Assert.Equal([90, 0], Rotations(again));
        Assert.Equal("Four:1 Five:2", Outline(again)); // Three was deleted, so Four moved up a level
    }

    [Fact]
    public async Task Pages_can_come_from_other_pdfs()
    {
        using var scratch = new Scratch();
        var pdf = Numbered(scratch);
        var more = Numbered(scratch, "more.pdf", pages: 2, bookmarks: false, prefix: "More");
        var output = Assert.Single((await Run(pdf, "1,b2,b1r90,2", "more.pdf")).Outputs); // found beside the PDF
        Assert.Equal(["Page 1", "More 2", "More 1", "Page 2"], Texts(output));
        Assert.Equal([0, 0, 90, 0], Rotations(output));
        Assert.Equal("One:1", Outline(output));
        Assert.True(File.Exists(more));
    }

    [Fact]
    public async Task Bad_lists_and_locked_pdfs_say_what_is_wrong()
    {
        using var scratch = new Scratch();
        var pdf = Numbered(scratch);
        Assert.Equal("There's no page 9: doc.pdf has 5 pages.", Assert.Single((await Run(pdf, "1,9")).Failures).Message);
        Assert.Equal($"Couldn't find {scratch.PathOf("gone.pdf")} to take pages from.", Assert.Single((await Run(pdf, "1", "gone.pdf")).Failures).Message);
        var locked = scratch.TextPdf("locked.pdf", "secret", g => g.DrawString("Hidden", new XFont("Arial", 20), XBrushes.Black, 50, 50));
        Assert.Equal("This PDF is password-protected.", Assert.Single((await Run(locked, "1")).Failures).Message);
        Assert.Equal(["doc.pdf", "locked.pdf"], scratch.Files()); // nothing half-written
    }

    [Fact]
    public async Task Links_between_pages_are_mentioned_before_and_after_saving()
    {
        using var scratch = new Scratch();
        PdfFonts.Ready();
        var path = scratch.PathOf("linked.pdf");
        using (var document = new PdfDocument())
        {
            var first = document.AddPage();
            document.AddPage();
            first.AddDocumentLink(new PdfSharp.Pdf.PdfRectangle(new XRect(50, 50, 100, 20)), 2);
            document.Save(path);
        }
        Assert.Equal("This PDF's links between pages won't work in the saved copy.", OrganizePdfTool.Losses(path));
        Assert.Null(OrganizePdfTool.Losses(Numbered(scratch)));
        var result = await Run(path, "2,1");
        Assert.Equal(["This PDF's links between pages won't work in the saved copy."], result.Notes);
    }

    [Fact]
    public async Task Thumbnails_show_the_pages_as_they_are_turned()
    {
        using var scratch = new Scratch();
        var turned = Assert.Single((await Run(Numbered(scratch), "1r90,2")).Outputs);
        var thumbnails = await PdfThumbnails.OpenAsync(turned);
        Assert.Equal(2, thumbnails.Count);
        var (width, height) = thumbnails.Size(0);
        Assert.True(width > height, "a turned portrait page shows as landscape");
        var png = await thumbnails.RenderAsync(1, 120, TestContext.Current.CancellationToken);
        using var image = new ImageMagick.MagickImage(png);
        Assert.Equal(120u, Math.Max(image.Width, image.Height));
    }
}

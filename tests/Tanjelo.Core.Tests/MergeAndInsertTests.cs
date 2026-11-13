using System.Windows;
using System.Windows.Input;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Tanjelo.Core.Converters;
using Tanjelo.Editors;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using Point = System.Windows.Point;

namespace Tanjelo.Core.Tests;

/// <summary>Merging pictures and documents with PDFs, a bookmark each; blank pages and pictures in Organize pages.</summary>
public class MergeAndInsertTests
{
    static string Outline(string pdf)
    {
        using var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        static string Write(IReadOnlyList<OrganizePdfTool.Bookmark> marks) =>
            string.Join(" ", marks.Select(m => $"{m.Title}:{(m.Page is { } p ? p + 1 : 0)}" + (m.Children.Count > 0 ? $"[{Write(m.Children)}]" : "")));
        return Write(OrganizePdfTool.Bookmarks.Read(document));
    }

    [Fact]
    public async Task A_pdf_a_picture_and_a_markdown_file_merge_in_order_with_a_bookmark_each()
    {
        using var scratch = new Scratch();
        var report = scratch.BookmarkedPdf("report.pdf", ("Introduction", 2));
        var photo = scratch.Image("photo.png", 300, 200);
        var notes = scratch.Text("notes.md", "# Notes\n\nWritten for the merge.\n");
        var withNotes = Env.Tools.WebView2 is not null; // Markdown is printed to PDF by WebView2
        string[] inputs = withNotes ? [notes, report, photo] : [report, photo];

        var output = await Env.RunOne("merge-pdf", inputs);

        Assert.Equal(scratch.PathOf("Merged.pdf"), output);
        // Nothing but the merged PDF is left beside them; the PDFs made on the way went into a scratch folder.
        Assert.Equal([.. inputs.Select(Path.GetFileName).Append("Merged.pdf").Order(StringComparer.Ordinal)], scratch.Files());
        using (var merged = PigDocument.Open(output))
        {
            var texts = merged.GetPages().Select(p => p.Text).ToList();
            var first = withNotes ? 1 : 0;
            Assert.Equal(first + 3, texts.Count);
            if (withNotes) Assert.Contains("Written for the merge", texts[0]);
            Assert.Equal("Introduction page 1", texts[first]);
            Assert.Single(merged.GetPage(first + 3).GetImages());
        }
        Assert.Equal(withNotes ? "notes:1 report:2[Introduction:2[Introduction details:3]] photo:4" : "report:1[Introduction:1[Introduction details:2]] photo:3",
            Outline(output));
    }

    [Fact]
    public void Office_documents_merge_only_when_something_makes_PDFs_of_them()
    {
        using var scratch = new Scratch();
        var pdf = scratch.Pdf("report.pdf");
        var letter = scratch.Text("letter.docx", "made up, never opened here");

        var offered = Env.Keys(pdf, letter).Contains("merge-pdf");

        Assert.Equal(Env.Tools.Word || Env.Tools.LibreOffice is not null, offered);
        Assert.DoesNotContain("merge-pdf", new ConverterEngine(new Toolbox()).GetMenu([pdf, letter]).Targets.Select(t => t.Key));
    }

    [Fact]
    public void Blank_pages_go_in_page_lists_and_come_back_out()
    {
        var pages = PageList.Parse("blank, 2, BLANK, b1, 1", [3, 1], ["doc.pdf", "picture.png"]);

        Assert.Equal([PageList.Page.Blank, new(0, 1), PageList.Page.Blank, new(1, 0), new(0, 0)], pages);
        Assert.Equal("blank,2,blank,b1,1", PageList.Format(pages));
    }

    [Fact]
    public async Task Tanj_puts_blank_pages_and_pictures_among_the_pages()
    {
        using var scratch = new Scratch();
        var pdf = OrganizePdfTests.Numbered(scratch, pages: 2);
        var turned = scratch.RotatedPdf("turned.pdf", 90);
        var photo = scratch.Image("photo.jpg", 400, 300);
        var engine = Env.Engine();
        var target = Env.Offered(engine, "organize-pdf", [pdf]).WithParameters(new Dictionary<string, string>
        {
            ["pages"] = "blank,1,blank,c1,b1,blank,2",
            ["with"] = $"{turned}|{photo}",
        });

        var result = await engine.RunAsync([pdf], target, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Failures);
        using var document = PdfReader.Open(Assert.Single(result.Outputs), PdfDocumentOpenMode.Import);
        var sizes = document.Pages.Cast<PdfPage>().Select(p => new PageGeometry(p)).Select(g => ((int)Math.Round(g.Width), (int)Math.Round(g.Height))).ToList();
        // A blank page is the size the page before it is seen at, or the page after it when it comes first.
        Assert.Equal((612, 792), sizes[0]);
        Assert.Equal((612, 792), sizes[2]);
        Assert.Equal((792, 612), sizes[5]); // after the page turned a quarter
        using var pig = PigDocument.Open(Assert.Single(result.Outputs));
        Assert.Equal(["", "Page 1", "", "", "Rotated 90", "", "Page 2"], pig.GetPages().Select(p => p.Text.Replace(" ", "") == "" ? "" : p.Text));
        Assert.Single(pig.GetPage(4).GetImages()); // the picture, a page of its own
        Assert.Empty(pig.GetPage(1).GetImages());
    }

    [Fact]
    public async Task The_page_editor_inserts_blank_pages_and_pictures_after_the_selection()
    {
        using var scratch = new Scratch();
        var pdf = OrganizePdfTests.Numbered(scratch, pages: 3);
        var photo = scratch.Image("photo.png", 300, 200);
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "organize-pdf", [pdf]);
            var editor = new PageEditor(pdf, target);
            var window = new EditorWindow(pdf, target, editor);
            var host = Ui.Host(window, 1100, 760);
            await window.LoadAsync();
            await Ui.Settle();
            Ui.Layout(host);

            var tiles = editor.TileRects();
            Point Middle(Rect tile) => new(tile.Left + tile.Width / 2, tile.Top + tile.Height / 2);
            editor.PointerDown(Middle(tiles[0]), ModifierKeys.None);
            editor.PointerUp(Middle(tiles[0]));
            editor.InsertBlank();
            Assert.True(editor.Pages[1].IsBlank);
            Assert.Equal([editor.Pages[1].Id], editor.Selected); // the new page, so another goes after it
            await editor.InsertPicturesAsync([photo]);
            Assert.Equal(1, editor.Pages[2].Source);
            editor.Undo();
            Assert.Equal(4, editor.Pages.Count);
            editor.Redo();

            var saved = editor.Result();
            Assert.Equal("1,blank,b1,2-3", saved.Parameters["pages"]);
            Assert.Equal(photo, saved.Parameters["with"]);
            Ui.Layout(host);
            await editor.SettleThumbnailsAsync();
            Assert.True(editor.PictureShown(2));
            Ui.Render(host, "pages-inserted-light");

            var result = await Env.Engine().RunAsync([pdf], saved, ct: TestContext.Current.CancellationToken);
            Assert.Empty(result.Failures);
            using var output = PigDocument.Open(Assert.Single(result.Outputs));
            Assert.Equal(5, output.NumberOfPages);
            Assert.Single(output.GetPage(3).GetImages());
            window.Close();
        });
    }
}

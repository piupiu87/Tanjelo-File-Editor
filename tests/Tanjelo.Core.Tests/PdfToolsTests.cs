using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Tanjelo.Core.Tests;

public class PdfToolsTests
{
    [Fact]
    public async Task A_pdf_splits_into_a_folder_with_one_pdf_per_page()
    {
        using var scratch = new Scratch();
        var folder = await Env.RunOne("split-pdf", scratch.Pdf("report.pdf", pages: 3));

        Assert.Equal(scratch.PathOf("report pages"), folder);
        Assert.Equal(["report-1.pdf", "report-2.pdf", "report-3.pdf"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.All(Directory.GetFiles(folder), page => Assert.Equal(1, Inspect.PdfPages(page)));
    }

    [Fact]
    public async Task A_one_page_pdf_isnt_split()
    {
        using var scratch = new Scratch();
        var single = scratch.Pdf("single.pdf");
        var engine = Env.Engine();

        var result = await engine.RunAsync([single], Env.Offered(engine, "split-pdf", [single]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("This PDF has only one page.", Assert.Single(result.Failures).Message);
        Assert.Equal(["single.pdf"], scratch.Files());
    }

    [Fact]
    public async Task Pdfs_merge_in_the_order_they_were_selected()
    {
        using var scratch = new Scratch();
        // Page widths tell the files apart: 300, 400 and 500 points.
        var c = scratch.Pdf("c.pdf", pages: 1, width: 500);
        var a = scratch.Pdf("a.pdf", pages: 2, width: 300);
        var b = scratch.Pdf("b.pdf", pages: 1, width: 400);

        var output = await Env.RunOne("merge-pdf", c, a, b);

        Assert.Equal(scratch.PathOf("Merged.pdf"), output);
        using var merged = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal([500, 300, 300, 400], merged.Pages.Cast<PdfPage>().Select(p => (int)Math.Round(p.Width.Point)));
    }

    [Fact]
    public void Merge_is_offered_only_for_two_or_more_pdfs()
    {
        using var scratch = new Scratch();
        var pdf = scratch.Pdf("a.pdf");
        Assert.Contains("merge-pdf", Env.Keys(pdf, scratch.Pdf("b.pdf")));
        Assert.DoesNotContain("merge-pdf", Env.Keys(pdf));
        Assert.DoesNotContain("merge-pdf", Env.Keys(pdf, scratch.Image("c.png")));
    }

    [Fact]
    public async Task A_locked_pdf_in_a_merge_is_named_in_the_error()
    {
        using var scratch = new Scratch();
        var locked = scratch.PathOf("locked.pdf");
        using (var document = new PdfDocument())
        {
            document.AddPage();
            document.SecuritySettings.UserPassword = "secret";
            document.Save(locked);
        }
        var inputs = new[] { scratch.Pdf("open.pdf"), locked };
        var engine = Env.Engine();

        var result = await engine.RunAsync(inputs, Env.Offered(engine, "merge-pdf", inputs), ct: TestContext.Current.CancellationToken);

        Assert.Equal("locked.pdf: This PDF is password-protected.", Assert.Single(result.Failures).Message);
        Assert.DoesNotContain("Merged.pdf", scratch.Files());
    }
}

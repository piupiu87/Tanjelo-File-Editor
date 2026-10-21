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
}

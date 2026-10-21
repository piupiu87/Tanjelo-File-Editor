using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tanjelo.Core.Tests;

/// <summary>Office apps share global state (running instances, the PDF import helper), so these run one at a time.</summary>
[CollectionDefinition("Office", DisableParallelization = true)]
public class OfficeCollection;

[Collection("Office")]
public class WordTests
{
    const string Text = "Hello from Tanjelo.\r\nA second paragraph.";

    [Theory]
    [InlineData("pdf")]
    [InlineData("rtf")]
    [InlineData("odt")]
    [InlineData("doc")]
    public async Task A_word_document_converts_to(string key)
    {
        Assert.SkipUnless(Env.Tools.Word, Env.NoWord);
        using var scratch = new Scratch();
        var docx = await Env.RunOne("docx", scratch.Text("letter.txt", Text));
        File.Delete(scratch.PathOf("letter.txt"));

        var output = await Env.RunOne(key, docx);

        Assert.Equal(scratch.PathOf("letter." + key), output);
        Assert.True(new FileInfo(output).Length > 0);
        if (key == "pdf") Assert.Equal(1, Inspect.PdfPages(output));
    }

    [Fact]
    public async Task Text_survives_a_round_trip_through_docx()
    {
        Assert.SkipUnless(Env.Tools.Word, Env.NoWord);
        using var scratch = new Scratch();
        var docx = await Env.RunOne("docx", scratch.Text("notes.txt", Text));

        var txt = await Env.RunOne("txt", docx);

        Assert.Equal(scratch.PathOf("notes (2).txt"), txt);
        Assert.Contains("A second paragraph.", File.ReadAllText(txt));
    }

    [Theory]
    [InlineData("letter.txt")]
    [InlineData("letter.docx")]
    public async Task Documents_become_web_pages(string name)
    {
        Assert.SkipUnless(Env.Tools.Word, Env.NoWord);
        using var scratch = new Scratch();
        var input = scratch.Text("letter.txt", Text);
        if (name.EndsWith(".docx"))
        {
            input = await Env.RunOne("docx", input);
            File.Delete(scratch.PathOf("letter.txt"));
        }

        var html = await Env.RunOne("html", input);

        Assert.Equal(scratch.PathOf("letter.html"), html);
        var content = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(html), @"\s+", " "); // Word wraps its HTML source
        Assert.Contains("<html", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A second paragraph.", content);
    }

    [Theory]
    [InlineData("png", ImageMagick.MagickFormat.Png)]
    [InlineData("jpg", ImageMagick.MagickFormat.Jpeg)]
    public async Task A_one_page_document_becomes_one_image(string key, ImageMagick.MagickFormat format)
    {
        Assert.SkipUnless(Env.Tools.Word, Env.NoWord);
        using var scratch = new Scratch();
        var output = await Env.RunOne(key, scratch.Text("note.txt", Text));

        Assert.Equal(scratch.PathOf("note." + key), output);
        var info = Inspect.Image(output);
        Assert.Equal(format, info.Format);
        Assert.True(info.Width >= 2400, $"rendered {info.Width} px wide; 300 DPI expected"); // Letter or A4 at 300 DPI
    }

    [Fact]
    public async Task A_longer_document_becomes_a_folder_with_an_image_per_page()
    {
        Assert.SkipUnless(Env.Tools.Word, Env.NoWord);
        using var scratch = new Scratch();
        var lines = string.Join("\r\n", Enumerable.Range(1, 150).Select(i => $"Line {i} of a document long enough for several pages."));
        var docx = await Env.RunOne("docx", scratch.Text("report.txt", lines));

        var folder = await Env.RunOne("png", docx);

        Assert.Equal(scratch.PathOf("report pages"), folder);
        var pages = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
        Assert.True(pages.Count >= 2, $"{pages.Count} pages");
        Assert.Equal("report-1.png", pages[0]);
    }

    [Fact]
    public async Task A_pdf_becomes_an_editable_docx()
    {
        Assert.SkipUnless(Env.Tools.Word, Env.NoWord);
        using var scratch = new Scratch();
        var pdf = await Env.RunOne("pdf", scratch.Text("memo.txt", Text));
        File.Delete(scratch.PathOf("memo.txt"));

        var docx = await Env.RunOne("docx", pdf);
        var txt = await Env.RunOne("txt", docx);

        Assert.Contains("Hello from Tanjelo", File.ReadAllText(txt));
    }
}

[Collection("Office")]
public class ExcelTests
{
    [Fact]
    public async Task Csv_survives_a_round_trip_through_xlsx()
    {
        Assert.SkipUnless(Env.Tools.Excel, Env.NoExcel);
        using var scratch = new Scratch();
        var xlsx = await Env.RunOne("xlsx", scratch.Text("stock.csv", "fruit,count\r\napple,3\r\npear,5\r\n"));
        File.Delete(scratch.PathOf("stock.csv"));

        var csv = await Env.RunOne("csv", xlsx);

        Assert.Equal(["fruit,count", "apple,3", "pear,5"], File.ReadAllLines(csv));
    }

    [Fact]
    public async Task Tsv_survives_a_round_trip_through_xlsx()
    {
        Assert.SkipUnless(Env.Tools.Excel, Env.NoExcel);
        using var scratch = new Scratch();
        var xlsx = await Env.RunOne("xlsx", scratch.Text("stock.tsv", "fruit\tcount\tnote\r\napple\t3\tcrunchy, sweet\r\npear\t5\tnaïve café\r\n"));
        File.Delete(scratch.PathOf("stock.tsv"));

        var tsv = await Env.RunOne("tsv", xlsx);

        Assert.Equal(["fruit\tcount\tnote", "apple\t3\tcrunchy, sweet", "pear\t5\tnaïve café"], File.ReadAllLines(tsv));
        Assert.Equal([0xEF, 0xBB, 0xBF], File.ReadAllBytes(tsv)[..3]);
    }

    [Fact]
    public async Task Csv_and_tsv_convert_into_each_other()
    {
        Assert.SkipUnless(Env.Tools.Excel, Env.NoExcel);
        using var scratch = new Scratch();

        var tsv = await Env.RunOne("tsv", scratch.Text("a.csv", "x,y\r\n1,\"two, three\"\r\n"));
        Assert.Equal(["x\ty", "1\ttwo, three"], File.ReadAllLines(tsv));

        var csv = await Env.RunOne("csv", scratch.Text("b.tsv", "x\ty\r\n1\ttwo, three\r\n"));
        Assert.Equal(["x,y", "1,\"two, three\""], File.ReadAllLines(csv));
    }

    [Fact]
    public void Text_tables_offer_the_other_text_format()
    {
        Assert.SkipUnless(Env.Tools.Excel, Env.NoExcel);
        using var scratch = new Scratch();
        Assert.Equal(["pdf", "xlsx", "tsv"], Env.Keys(Section.Document, scratch.Text("a.csv", "x")));
        Assert.Equal(["pdf", "xlsx", "csv"], Env.Keys(Section.Document, scratch.Text("a.tsv", "x")));
    }

    [Theory]
    [InlineData("a\t\"b, c\"\r\n", "a\tb, c\r\n")]                          // Excel's needless quotes go
    [InlineData("\"say \"\"hi\"\"\"\tx\r\n", "\"say \"\"hi\"\"\"\tx\r\n")] // quotes inside a cell stay escaped
    [InlineData("\"two\r\nlines\"\ty", "\"two\r\nlines\"\ty")]               // so do line breaks
    [InlineData("plain\tcells\r\n\r\n", "plain\tcells\r\n\r\n")]
    [InlineData("\"unterminated", "unterminated")]
    public void Tab_separated_text_is_only_quoted_where_needed(string excel, string expected) =>
        Assert.Equal(expected, Converters.OfficeConverter.Requote(excel));

    [Theory]
    [InlineData("pdf")]
    [InlineData("xls")]
    [InlineData("ods")]
    [InlineData("tsv")]
    public async Task A_workbook_converts_to(string key)
    {
        Assert.SkipUnless(Env.Tools.Excel, Env.NoExcel);
        using var scratch = new Scratch();
        var xlsx = await Env.RunOne("xlsx", scratch.Text("stock.csv", "fruit,count\r\napple,3\r\n"));

        var output = await Env.RunOne(key, xlsx);

        Assert.Equal(scratch.PathOf("stock." + key), output);
        Assert.True(new FileInfo(output).Length > 0);
    }
}

[Collection("Office")]
public class PowerPointTests
{
    [Fact]
    public async Task A_presentation_becomes_a_pdf_with_a_page_per_slide()
    {
        Assert.SkipUnless(Env.Tools.PowerPoint, Env.NoPowerPoint);
        using var scratch = new Scratch();
        var deck = await Presentation(scratch.PathOf("deck.pptx"), slides: 2);

        var pdf = await Env.RunOne("pdf", deck);

        Assert.Equal(2, Inspect.PdfPages(pdf));
    }

    [Fact]
    public async Task Each_slide_becomes_an_image()
    {
        Assert.SkipUnless(Env.Tools.PowerPoint, Env.NoPowerPoint);
        using var scratch = new Scratch();
        var deck = await Presentation(scratch.PathOf("deck.pptx"), slides: 2);

        var folder = await Env.RunOne("png", deck);

        Assert.Equal(scratch.PathOf("deck slides"), folder);
        Assert.Equal(["deck-1.png", "deck-2.png"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
        Assert.Equal(1920u, Inspect.Image(Path.Combine(folder, "deck-1.png")).Width);
    }

    [Fact]
    public async Task A_presentation_saves_as_odp()
    {
        Assert.SkipUnless(Env.Tools.PowerPoint, Env.NoPowerPoint);
        using var scratch = new Scratch();
        var output = await Env.RunOne("odp", await Presentation(scratch.PathOf("deck.pptx"), slides: 1));
        Assert.True(new FileInfo(output).Length > 0);
    }

    /// <summary>
    /// Builds a deck through PowerPoint itself. PowerPoint runs one instance per user, so it's only quit
    /// afterwards if it wasn't already running.
    /// </summary>
    static Task<string> Presentation(string path, int slides) => Task.Run(() =>
    {
        var wasRunning = Process.GetProcessesByName("POWERPNT").Length > 0;
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
        try
        {
            dynamic deck = app.Presentations.Add(0 /* msoFalse: no window */);
            try
            {
                for (var i = 1; i <= slides; i++)
                {
                    dynamic slide = deck.Slides.Add(i, 1 /* ppLayoutTitle */);
                    slide.Shapes.Item(1).TextFrame.TextRange.Text = $"Slide {i}";
                    Marshal.FinalReleaseComObject(slide);
                }
                deck.SaveAs(path, 24 /* ppSaveAsOpenXMLPresentation */);
            }
            finally
            {
                deck.Close();
                Marshal.FinalReleaseComObject(deck);
            }
        }
        finally
        {
            if (!wasRunning) app.Quit();
            Marshal.FinalReleaseComObject(app);
        }
        return path;
    }).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
}

public class LibreOfficeTests
{
    [Fact]
    public async Task Without_office_libreoffice_converts_text_to_pdf()
    {
        Assert.SkipWhen(Env.Tools.LibreOffice is null, "LibreOffice isn't installed");
        using var scratch = new Scratch();
        var engine = new ConverterEngine(new Toolbox { LibreOffice = Env.Tools.LibreOffice });

        var result = await Env.Run(engine, "pdf", scratch.Text("notes.txt", "Hello"));

        Assert.Equal(1, Inspect.PdfPages(Assert.Single(result.Outputs)));
    }
}

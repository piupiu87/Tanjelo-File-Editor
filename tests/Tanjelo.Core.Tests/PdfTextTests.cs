using PdfSharp.Drawing;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class PdfTextTests
{
    static readonly XFont Title = new("Arial", 24, XFontStyleEx.Bold);
    static readonly XFont Heading = new("Arial", 16, XFontStyleEx.Bold);
    static readonly XFont Body = new("Arial", 11);
    static readonly XFont Bold = new("Arial", 11, XFontStyleEx.Bold);

    /// <summary>Draws lines of text from the top-left corner down, each line made of runs in their own fonts.</summary>
    static void Lines(XGraphics g, double x, double y, double leading, params (string Text, XFont Font)[][] lines)
    {
        foreach (var runs in lines)
        {
            var left = x;
            foreach (var (text, font) in runs)
            {
                g.DrawString(text, font, XBrushes.Black, left, y);
                left += g.MeasureString(text, font).Width;
            }
            y += leading;
        }
    }

    static (string, XFont)[] L(string text, XFont? font = null) => [(text, font ?? Body)];

    static void Article(XGraphics g)
    {
        Lines(g, 60, 80, 30, L("Annual Report", Title));
        Lines(g, 60, 130, 22, L("Getting started", Heading));
        Lines(g, 60, 160, 15,
            [("This report explains how the ", Body), ("important", Bold), (" numbers were found, and it continues", Body)],
            L("on a second line so the para-"),
            L("graph has to be put back together."));
        Lines(g, 60, 230, 22, L("What comes next", Heading));
        Lines(g, 60, 260, 15, L("A short closing paragraph."));
    }

    [Fact]
    public void A_pdf_offers_text_and_markdown()
    {
        using var scratch = new Scratch();
        var keys = Env.Keys(Section.Document, scratch.TextPdf("report.pdf", Article));

        Assert.Contains("txt", keys);
        Assert.Contains("md", keys);
    }

    [Fact]
    public async Task Markdown_has_headings_by_size_bold_words_and_whole_paragraphs()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("md", scratch.TextPdf("report.pdf", Article));

        Assert.Equal(scratch.PathOf("report.md"), output);
        var lines = File.ReadAllLines(output);
        Assert.Equal("# Annual Report", lines[0]);
        Assert.Contains("## Getting started", lines);
        Assert.Contains("## What comes next", lines);
        Assert.Contains("This report explains how the **important** numbers were found, and it continues on a second line so the paragraph has to be put back together.", lines);
        Assert.Contains("A short closing paragraph.", lines);
    }

    [Fact]
    public async Task Text_is_plain_and_in_reading_order()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("txt", scratch.TextPdf("report.pdf", Article));

        var text = File.ReadAllText(output);
        Assert.DoesNotContain("#", text);
        Assert.DoesNotContain("**", text);
        Assert.StartsWith("Annual Report\r\n\r\nGetting started\r\n\r\nThis report explains how the important numbers", text);
    }

    [Fact]
    public async Task Two_columns_are_read_one_after_the_other()
    {
        using var scratch = new Scratch();
        var pdf = scratch.TextPdf("columns.pdf", g =>
        {
            Lines(g, 50, 80, 15, L("Left column starts here and"), L("keeps going down the page"), L("until the left side ends."));
            Lines(g, 320, 80, 15, L("Right column follows after"), L("the left one is finished"), L("and closes the page."));
        });

        var text = File.ReadAllText(await Env.RunOne("txt", pdf));

        Assert.Contains("Left column starts here and keeps going down the page until the left side ends.", text);
        Assert.Contains("Right column follows after the left one is finished and closes the page.", text);
        Assert.True(text.IndexOf("Left column", StringComparison.Ordinal) < text.IndexOf("Right column", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bullets_and_numbers_become_list_items()
    {
        using var scratch = new Scratch();
        var pdf = scratch.TextPdf("list.pdf", g =>
        {
            Lines(g, 60, 80, 15, L("Things to pack:"));
            Lines(g, 70, 100, 15, L("•  Passport"), L("•  A charger that is long enough"));
            Lines(g, 82, 130, 15, L("for the hotel"));
            Lines(g, 70, 145, 15, L("•  Snacks"));
            Lines(g, 70, 175, 15, L("1.  Lock the door"), L("2.  Leave"));
            Lines(g, 60, 220, 15, L("Have a good trip."));
        });

        var markdown = File.ReadAllLines(await Env.RunOne("md", pdf));
        var text = File.ReadAllText(await Env.RunOne("txt", pdf));

        Assert.Equal(["Things to pack:", "", "- Passport", "- A charger that is long enough for the hotel", "- Snacks", "", "1. Lock the door", "2. Leave", "", "Have a good trip."],
            markdown);
        Assert.Contains("• Passport\r\n• A charger that is long enough for the hotel\r\n• Snacks", text);
    }

    [Fact]
    public async Task Bullets_set_apart_by_a_tab_stay_with_their_text()
    {
        using var scratch = new Scratch();
        // Word's lists: the bullet, then a tab stop well to the right, then the text.
        var pdf = scratch.TextPdf("tabbed.pdf", g =>
        {
            Lines(g, 60, 80, 15, L("Before the list."));
            foreach (var (y, text) in new[] { (110.0, "First thing"), (128.0, "Second thing"), (146.0, "Third thing") })
            {
                Lines(g, 78, y, 15, L("•"));
                Lines(g, 105, y, 15, L(text));
            }
        });

        var markdown = File.ReadAllLines(await Env.RunOne("md", pdf));

        Assert.Equal(["Before the list.", "", "- First thing", "- Second thing", "- Third thing"], markdown);
    }

    [Fact]
    public async Task Pages_flow_together_unless_page_breaks_are_asked_for()
    {
        using var scratch = new Scratch();
        var pdf = scratch.TextPdf("two pages.pdf",
            g => Lines(g, 60, 80, 15, L("The first page ends in the middle of a"), L("sentence that")),
            g =>
            {
                Lines(g, 60, 80, 15, L("carries on over the page."));
                Lines(g, 290, 800, 15, L("3"));
            });
        var engine = Env.Engine();
        var target = Env.Offered(engine, "md", [pdf]);

        var flowing = File.ReadAllText(Assert.Single((await engine.RunAsync([pdf], target, ct: TestContext.Current.CancellationToken)).Outputs));
        var broken = File.ReadAllText(Assert.Single((await engine.RunAsync([pdf], target.WithParameters(new Dictionary<string, string> { ["page-breaks"] = "true" }),
            ct: TestContext.Current.CancellationToken)).Outputs));

        Assert.Equal("The first page ends in the middle of a sentence that carries on over the page.\n", flowing);
        Assert.Equal("The first page ends in the middle of a sentence that\n\n---\n\ncarries on over the page.\n", broken);
    }

    [Fact]
    public async Task Scanned_pages_are_read_with_ocr()
    {
        Assert.SkipUnless(PdfText.CanOcr, "No OCR language is installed in Windows");
        using var scratch = new Scratch();
        var pdf = scratch.ScannedPdf("scan.pdf", "Tanjelo reads scanned pages", "with the OCR built into Windows.");

        var result = await Env.Run("txt", pdf);

        var text = File.ReadAllText(Assert.Single(result.Outputs));
        Assert.Contains("scanned pages", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("built into Windows", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["1 page read with OCR"], result.Notes);
    }

    [Fact]
    public async Task A_password_protected_pdf_says_so()
    {
        using var scratch = new Scratch();
        var pdf = scratch.TextPdf("secret.pdf", "open sesame", Article);
        var engine = Env.Engine();

        var result = await engine.RunAsync([pdf], Env.Offered(engine, "md", [pdf]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("This PDF is password-protected.", Assert.Single(result.Failures).Message);
        Assert.Equal(["secret.pdf"], scratch.Files());
    }

    [Fact]
    public async Task A_damaged_pdf_fails_without_leaving_files()
    {
        using var scratch = new Scratch();
        var bad = scratch.Text("broken.pdf", "%PDF-1.4 nothing else");
        var engine = Env.Engine();

        var result = await engine.RunAsync([bad], Env.Offered(engine, "txt", [bad]), ct: TestContext.Current.CancellationToken);

        Assert.Single(result.Failures);
        Assert.Equal(["broken.pdf"], scratch.Files());
    }

    [Theory]
    [InlineData("2*3", @"2\*3")]
    [InlineData("snake_case", "snake_case")]
    [InlineData("_emphasis_", @"\_emphasis\_")]
    [InlineData("[1]", @"\[1]")]
    [InlineData("<b>", @"\<b>")]
    [InlineData("a<b", @"a\<b")]
    [InlineData("1<2", "1<2")]
    [InlineData(@"C:\Temp", @"C:\\Temp")]
    public void Markdown_characters_in_words_are_escaped(string word, string escaped) => Assert.Equal(escaped, PdfText.Escape(word));

    [Theory]
    [InlineData("1. Not a list", @"1\. Not a list")]
    [InlineData("# Not a heading", @"\# Not a heading")]
    [InlineData("- Not a bullet", @"\- Not a bullet")]
    [InlineData("2024 was a year", "2024 was a year")]
    [InlineData("3.5 percent", "3.5 percent")]
    public void Paragraphs_that_look_like_markdown_blocks_are_escaped(string paragraph, string escaped) =>
        Assert.Equal(escaped, PdfText.EscapeStart(paragraph));
}

using System.IO.Compression;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using PdfSharp.Drawing;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class PandocTests
{
    const string NoPandoc = "pandoc isn't installed";

    const string Guide = """
        # Garden guide

        Water the **tomatoes** every morning.

        ![A sketch](sketch.png)

        - [x] Plant seeds
        - [ ] Build a fence

        | Plant  | Water |
        |--------|-------|
        | Tomato | Daily |
        """;

    static string Text(string docx)
    {
        using var document = WordprocessingDocument.Open(docx, false);
        return document.MainDocumentPart!.Document!.Body!.InnerText;
    }

    static string EpubTitle(string epub)
    {
        using var zip = ZipFile.OpenRead(epub);
        var opf = zip.Entries.Single(e => e.FullName.EndsWith(".opf", StringComparison.Ordinal));
        using var reader = new StreamReader(opf.Open());
        return Regex.Match(reader.ReadToEnd(), "<dc:title[^>]*>([^<]*)</dc:title>").Groups[1].Value;
    }

    /// <summary>The guide with its picture, in a folder of its own.</summary>
    static string GuideMarkdown(Scratch scratch)
    {
        scratch.Image("sketch.png", 40, 30);
        return scratch.Text("guide.md", Guide);
    }

    [Fact]
    public void Without_pandoc_the_menu_says_what_to_install()
    {
        using var scratch = new Scratch();
        var md = scratch.Text("notes.md", "# Notes");
        var epub = scratch.Text("book.epub", "not really a book");
        var engine = new ConverterEngine(new Toolbox());

        var menu = engine.GetMenu([md]);

        Assert.DoesNotContain(menu.Targets, t => t.Key is "docx" or "epub");
        Assert.Contains(PandocConverter.NoPandoc, menu.Hints);
        Assert.Contains(PandocConverter.NoPandoc, engine.GetMenu([epub]).Hints);
    }

    [Fact]
    public void Documents_offer_what_pandoc_can_make()
    {
        Assert.SkipWhen(Env.Tools.Pandoc is null, NoPandoc);
        using var scratch = new Scratch();

        Assert.Contains("docx", Env.Keys(scratch.Text("a.md", "# A")));
        Assert.Contains("epub", Env.Keys(scratch.Text("b.md", "# B")));
        Assert.Contains("epub", Env.Keys(scratch.Text("c.txt", "C")));
        Assert.Contains("epub", Env.Keys(scratch.Text("d.html", "<p>D</p>")));
        Assert.Contains("epub", Env.Keys(scratch.Pdf("e.pdf")));
        Assert.Equal(FileKind.Ebook, Formats.KindOf("f.epub"));
        Assert.Equal(FileKind.WebPage, Formats.KindOf("g.htm"));
        Assert.Equal("html", Formats.Key("g.htm"));
    }

    [Fact]
    public async Task Markdown_becomes_a_word_document_with_its_picture()
    {
        Assert.SkipWhen(Env.Tools.Pandoc is null, NoPandoc);
        using var scratch = new Scratch();
        var output = await Env.RunOne("docx", GuideMarkdown(scratch));

        Assert.Equal(scratch.PathOf("guide.docx"), output);
        Assert.Contains("Water the tomatoes every morning.", Text(output));
        using var document = WordprocessingDocument.Open(output, false);
        Assert.Single(document.MainDocumentPart!.ImageParts);
    }

    [Theory]
    [InlineData("# Garden guide\n\nText.", "Garden guide")]
    [InlineData("---\ntitle: The real title\n---\n\n# A heading\n\nText.", "The real title")]
    [InlineData("Just text, no heading.", "notes")]
    public async Task An_e_book_is_titled_by_its_front_matter_its_first_heading_or_its_name(string markdown, string title)
    {
        Assert.SkipWhen(Env.Tools.Pandoc is null, NoPandoc);
        using var scratch = new Scratch();
        var output = await Env.RunOne("epub", scratch.Text("notes.md", markdown));

        Assert.Equal(title, EpubTitle(output));
    }

    [Fact]
    public async Task A_word_document_becomes_markdown_with_its_pictures_in_a_folder()
    {
        Assert.SkipWhen(Env.Tools.Pandoc is null, NoPandoc);
        using var scratch = new Scratch();
        var docx = await Env.RunOne("docx", GuideMarkdown(scratch));
        File.Delete(scratch.PathOf("guide.md"));

        var output = await Env.RunOne("md", docx);

        Assert.Equal(scratch.PathOf("guide.md"), output);
        var markdown = File.ReadAllText(output);
        Assert.StartsWith("# Garden guide", markdown);
        Assert.Contains("Water the **tomatoes** every morning.", markdown);
        Assert.Contains("| Tomato | Daily |", markdown);
        Assert.DoesNotContain("width=", markdown);
        var picture = Assert.Single(Directory.GetFiles(scratch.PathOf("guide media")));
        Assert.Contains($"(<guide media/{Path.GetFileName(picture)}>)", markdown);
    }

    [Fact]
    public async Task E_books_become_markdown_word_text_and_a_web_page()
    {
        Assert.SkipWhen(Env.Tools.Pandoc is null, NoPandoc);
        using var scratch = new Scratch();
        var epub = await Env.RunOne("epub", GuideMarkdown(scratch));
        File.Delete(scratch.PathOf("guide.md"));

        var markdown = File.ReadAllText(await Env.RunOne("md", epub));
        var docx = await Env.RunOne("docx", epub);
        var text = File.ReadAllText(await Env.RunOne("txt", epub));
        var html = File.ReadAllText(await Env.RunOne("html", epub));

        Assert.Contains("# Garden guide", markdown);
        Assert.Contains("- [x] Plant seeds", markdown);
        Assert.DoesNotContain("<div", markdown);
        Assert.Contains("Water the tomatoes every morning.", Text(docx));
        Assert.Contains("Water the tomatoes every morning.", text);
        Assert.DoesNotContain("**", text);
        Assert.Contains("src=\"data:image/png;base64,", html); // one page, nothing beside it
        Assert.Contains("Segoe UI", html);
    }

    [Fact]
    public async Task An_e_book_is_printed_to_pdf()
    {
        Assert.SkipWhen(Env.Tools.Pandoc is null, NoPandoc);
        Assert.SkipWhen(Env.Tools.WebView2 is null, "The WebView2 runtime isn't installed");
        using var scratch = new Scratch();
        var epub = await Env.RunOne("epub", GuideMarkdown(scratch));
        File.Delete(scratch.PathOf("guide.md"));

        var pdf = await Env.RunOne("pdf", epub);

        Assert.Equal(scratch.PathOf("guide.pdf"), pdf);
        Assert.Contains("Water the tomatoes every morning.", File.ReadAllText(await Env.RunOne("txt", pdf)));
    }

    [Fact]
    public async Task Plain_text_becomes_an_e_book_without_reading_markup_into_it()
    {
        Assert.SkipWhen(Env.Tools.Pandoc is null, NoPandoc);
        using var scratch = new Scratch();
        var epub = await Env.RunOne("epub", scratch.Text("story.txt", "Once upon a time\nthere was a *star*.\n\n# Not a heading"));

        Assert.Equal("story", EpubTitle(epub));
        var text = File.ReadAllText(await Env.RunOne("txt", epub));
        Assert.Contains("Once upon a time there was a *star*.", text);
        Assert.Contains("# Not a heading", text);
    }

    [Theory]
    [InlineData("<html><head><title>From the page</title></head><body><p>Hi</p></body></html>", "From the page")]
    [InlineData("<p>No title here</p>", "page")]
    public async Task A_web_page_becomes_an_e_book(string html, string title)
    {
        Assert.SkipWhen(Env.Tools.Pandoc is null, NoPandoc);
        using var scratch = new Scratch();
        var epub = await Env.RunOne("epub", scratch.Text("page.html", html));

        Assert.Equal(title, EpubTitle(epub));
    }

    [Fact]
    public async Task A_pdf_becomes_an_e_book_of_its_text_and_headings()
    {
        Assert.SkipWhen(Env.Tools.Pandoc is null, NoPandoc);
        using var scratch = new Scratch();
        var pdf = scratch.TextPdf("report.pdf", g =>
        {
            g.DrawString("Quarterly Report", new XFont("Arial", 24, XFontStyleEx.Bold), XBrushes.Black, 60, 80);
            g.DrawString("Sales went up this quarter.", new XFont("Arial", 11), XBrushes.Black, 60, 120);
        });

        var epub = await Env.RunOne("epub", pdf);

        Assert.Equal("Quarterly Report", EpubTitle(epub));
        Assert.Contains("Sales went up this quarter.", File.ReadAllText(await Env.RunOne("txt", epub)));
    }

    [Fact]
    public void A_folder_inside_the_pictures_folder_is_flattened()
    {
        using var scratch = new Scratch();
        var folder = scratch.PathOf("doc media");
        Directory.CreateDirectory(Path.Combine(folder, "media"));
        File.WriteAllText(Path.Combine(folder, "media", "image1.png"), "png");

        var markdown = PandocConverter.Flatten(folder, "doc media", "![a](<doc media/media/image1.png>) ![b](doc%20media/media/image1.png)");

        Assert.Equal("![a](<doc media/image1.png>) ![b](doc%20media/image1.png)", markdown);
        Assert.Equal(["image1.png"], Directory.GetFileSystemEntries(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void Text_becomes_paragraphs_where_it_has_blank_lines()
    {
        var html = PandocConverter.TextAsHtml("One line\nwrapped.\n\n  \nTwo <b>&</b>\n", "Story");

        Assert.Contains("<title>Story</title>", html);
        Assert.Contains("<p>One line wrapped.</p>\n<p>Two &lt;b&gt;&amp;&lt;/b&gt;</p>", html);
    }
}

[Collection("Office")]
public class PandocWithWordTests
{
    [Theory]
    [InlineData("letter.rtf", @"{\rtf1\ansi{\fonttbl{\f0 Calibri;}}\pard\b\fs40 Dear reader\b0\par\pard\fs22 This came from \b RTF\b0 .\par}")]
    public async Task Rtf_goes_through_word_to_become_markdown(string name, string content)
    {
        Assert.SkipWhen(Env.Tools.Pandoc is null, "pandoc isn't installed");
        Assert.SkipUnless(Env.Tools.Word, Env.NoWord);
        using var scratch = new Scratch();

        var markdown = File.ReadAllText(await Env.RunOne("md", scratch.Text(name, content)));

        Assert.Contains("Dear reader", markdown);
        Assert.Contains("This came from **RTF**.", markdown);
        Assert.Equal(["letter.md", name], scratch.Files());
    }
}

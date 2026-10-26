using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class MarkdownTests
{
    const string NoWebView2 = "The WebView2 runtime isn't installed";

    const string Notes = """
        ---
        title: Trip notes
        ---

        # Packing for the trip

        Bring **sturdy shoes** and a [map](https://example.com/map).

        - [x] Passport
        - [ ] Charger

        | Day | Place  |
        |-----|--------|
        | 1   | Lisbon |
        | 2   | Porto  |

        Remember the ferry.[^1]

        [^1]: It leaves at nine.
        """;

    [Theory]
    [InlineData("notes.md")]
    [InlineData("notes.markdown")]
    public void Markdown_files_offer_html_pdf_and_text(string name)
    {
        using var scratch = new Scratch();
        var keys = Env.Keys(scratch.Text(name, Notes));

        Assert.Equal(FileKind.Markdown, Formats.KindOf(name));
        Assert.Equal("md", Formats.Key(name));
        Assert.Contains("html", keys);
        Assert.Contains("txt", keys);
        Assert.Equal(Env.Tools.WebView2 is not null, keys.Contains("pdf"));
    }

    [Fact]
    public async Task The_web_page_is_styled_and_has_tables_task_lists_and_footnotes()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("html", scratch.Text("notes.md", Notes));

        Assert.Equal(scratch.PathOf("notes.html"), output);
        var html = File.ReadAllText(output);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<title>Trip notes</title>", html);
        Assert.Contains("<style>", html);
        Assert.Contains("<h1 id=\"packing-for-the-trip\">Packing for the trip</h1>", html);
        Assert.Contains("<strong>sturdy shoes</strong>", html);
        Assert.Contains("<td>Lisbon</td>", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("class=\"footnotes\"", html);
        Assert.DoesNotContain("title: Trip notes", html);
    }

    [Fact]
    public async Task Pictures_from_the_computer_are_embedded_and_those_from_the_web_are_left_alone()
    {
        using var scratch = new Scratch();
        scratch.Image("my photo.png", 20, 10);
        Directory.CreateDirectory(scratch.PathOf("art"));
        File.Copy(scratch.Image("logo.gif", 8, 8), scratch.PathOf(@"art\logo.gif"));
        var md = scratch.Text("page.md", "![A photo](my%20photo.png)\n\n![Logo](art/logo.gif)\n\n![Far away](https://example.com/far.png)\n\n![Missing](nowhere.png)\n");

        var html = File.ReadAllText(await Env.RunOne("html", md));

        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.Contains("src=\"data:image/gif;base64,", html);
        Assert.DoesNotContain("my%20photo.png", html);
        Assert.Contains("src=\"https://example.com/far.png\"", html);
        Assert.Contains("src=\"nowhere.png\"", html);
    }

    [Fact]
    public async Task Text_has_no_markup_but_keeps_lists_tables_links_and_footnotes()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("txt", scratch.Text("notes.md", Notes));

        Assert.Equal(
            "Packing for the trip\r\n\r\n" +
            "Bring sturdy shoes and a map (https://example.com/map).\r\n\r\n" +
            "• [x] Passport\r\n• [ ] Charger\r\n\r\n" +
            "Day\tPlace\r\n1\tLisbon\r\n2\tPorto\r\n\r\n" +
            "Remember the ferry.[1]\r\n\r\n" +
            "[1] It leaves at nine.\r\n",
            File.ReadAllText(output));
    }

    [Fact]
    public async Task Code_quotes_and_nested_lists_are_indented_in_text()
    {
        using var scratch = new Scratch();
        var md = scratch.Text("more.md", "1. First\n   - inner\n2. Second\n\n> Quoted\n\n```\nlet x = 1;\n```\n");

        var text = File.ReadAllText(await Env.RunOne("txt", md));

        Assert.Equal("1. First\r\n   • inner\r\n2. Second\r\n\r\n    Quoted\r\n\r\n    let x = 1;\r\n", text);
    }

    [Fact]
    public async Task The_pdf_is_printed_with_the_text_and_tables()
    {
        Assert.SkipWhen(Env.Tools.WebView2 is null, NoWebView2);
        using var scratch = new Scratch();
        var pdf = await Env.RunOne("pdf", scratch.Text("notes.md", Notes));

        Assert.Equal(scratch.PathOf("notes.pdf"), pdf);
        Assert.Equal(1, Inspect.PdfPages(pdf));
        var text = File.ReadAllText(await Env.RunOne("txt", pdf));
        Assert.Contains("Packing for the trip", text);
        Assert.Contains("sturdy shoes", text);
        Assert.Contains("Lisbon", text);
        Assert.Contains("It leaves at nine.", text);
    }

    [Fact]
    public async Task Pages_are_a4_or_letter_with_no_browser_header()
    {
        Assert.SkipWhen(Env.Tools.WebView2 is null, NoWebView2);
        using var scratch = new Scratch();
        var pdf = await Env.RunOne("pdf", scratch.Text("short.md", "# Short\n\nJust one line."));

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(pdf);
        var document = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        using var page = document.GetPage(0);
        var (width, height) = HtmlPrinter.Paper(RegionInfo.CurrentRegion);
        Assert.Equal(width * 96, page.Size.Width, 1.0); // DIPs are 1/96 inch
        Assert.Equal(height * 96, page.Size.Height, 1.0);
        var text = File.ReadAllText(await Env.RunOne("txt", pdf));
        Assert.DoesNotContain("file:", text);
        Assert.DoesNotContain("short.md", text);
    }

    [Fact]
    public async Task Printing_fetches_nothing_from_the_network()
    {
        Assert.SkipWhen(Env.Tools.WebView2 is null, NoWebView2);
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        var connections = 0;
        var listening = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await server.AcceptTcpClientAsync();
                    Interlocked.Increment(ref connections);
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // stopped
            }
        }, TestContext.Current.CancellationToken);
        using var scratch = new Scratch();
        var url = $"http://127.0.0.1:{port}";
        var md = scratch.Text("remote.md", $"""
            # Remote things

            ![The remote logo]({url}/logo.png)

            <img src="{url}/raw.png" alt="raw">
            <link rel="stylesheet" href="{url}/style.css">
            <iframe src="{url}/frame.html"></iframe>
            <script src="{url}/script.js"></script>

            [A link]({url}/page.html)
            """);

        var pdf = await Env.RunOne("pdf", md);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        server.Stop();

        Assert.Equal(0, connections);
        var text = File.ReadAllText(await Env.RunOne("txt", pdf));
        Assert.Contains("The remote logo", text); // the alt text, in the picture's place
        await listening;
    }

    [Theory]
    [InlineData("US", 8.5, 11)]
    [InlineData("CA", 8.5, 11)]
    [InlineData("GB", 8.27, 11.69)]
    [InlineData("DE", 8.27, 11.69)]
    public void Paper_follows_the_region(string region, double width, double height)
    {
        var paper = HtmlPrinter.Paper(new RegionInfo(region));

        Assert.Equal(width, paper.Width, 2);
        Assert.Equal(height, paper.Height, 2);
    }

    [Theory]
    [InlineData("file:///C:/Users/x/page.html", true)]
    [InlineData("data:image/png;base64,AAAA", true)]
    [InlineData("about:blank", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("http://127.0.0.1/x.png", false)]
    [InlineData("ws://example.com/", false)]
    public void Only_local_addresses_are_allowed_while_printing(string uri, bool allowed) => Assert.Equal(allowed, HtmlPrinter.IsLocal(uri));
}

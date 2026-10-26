using System.Text;

namespace Tanjelo.Core.Converters;

/// <summary>Markdown to a web page, a PDF or plain text, with nothing to install: Markdig, and WebView2 to print.</summary>
public sealed class MarkdownConverter(Toolbox tools) : IConverter
{
    static readonly Target Html = new("html", "HTML", "Styled web page, pictures included", Section.Document);
    static readonly Target Pdf = new("pdf", "PDF", "Printed offline by Windows' WebView2", Section.Document);
    static readonly Target Txt = new("txt", "TXT", "The text, without the markup", Section.Document);

    public const string NoWebView2 = "Markdown to PDF needs the WebView2 Runtime: winget install Microsoft.EdgeWebView2Runtime";

    public IEnumerable<Target> TargetsFor(string input) =>
        Formats.KindOf(input) != FileKind.Markdown ? [] : tools.WebView2 is null ? [Html, Txt] : [Html, Pdf, Txt];

    public string? HintFor(string input) => Formats.KindOf(input) == FileKind.Markdown && tools.WebView2 is null ? NoWebView2 : null;

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var markdown = TextFiles.Read(input);
        var output = OutputPaths.Beside(input, target.Key);
        switch (target.Key)
        {
            case "html":
                await File.WriteAllTextAsync(output, MarkdownPage.Html(markdown, input, forPrint: false), new UTF8Encoding(false), ctx.Cancellation);
                break;
            case "txt":
                await File.WriteAllTextAsync(output, MarkdownPage.PlainText(markdown), new UTF8Encoding(false), ctx.Cancellation);
                break;
            case "pdf":
                await PrintAsync(MarkdownPage.Html(markdown, input, forPrint: true), output, ctx);
                break;
            default:
                throw new ConversionException($"Markdown can't become {target.Label}.");
        }
        ctx.Progress.Report(1);
        return [output];
    }

    /// <summary>Prints a page to a PDF with WebView2, shared by the whole batch so it starts only once.</summary>
    internal static async Task PrintAsync(string html, string pdf, JobContext ctx)
    {
        var printer = ctx.Session(() => new HtmlPrinter());
        using var temp = OutputPaths.CreateTemp();
        var page = Path.Combine(temp.Path, "page.html");
        await File.WriteAllTextAsync(page, html, new UTF8Encoding(false), ctx.Cancellation);
        try
        {
            await printer.PrintAsync(page, pdf, ctx.Cancellation);
        }
        catch
        {
            OutputPaths.TryDelete(pdf);
            throw;
        }
    }
}

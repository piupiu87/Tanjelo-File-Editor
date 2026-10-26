namespace Tanjelo.Core.Converters;

/// <summary>
/// A web page saved on the computer, printed to PDF by WebView2 as it is, so its own pictures and styles beside it
/// are used. As for Markdown, scripts are off and nothing is fetched from the web.
/// </summary>
public sealed class WebPageConverter(Toolbox tools) : IConverter
{
    static readonly Target Pdf = new("pdf", "PDF", "Printed offline by Windows' WebView2", Section.Document);

    public IEnumerable<Target> TargetsFor(string input) =>
        Formats.KindOf(input) == FileKind.WebPage && tools.WebView2 is not null ? [Pdf] : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var printer = ctx.Session(() => new HtmlPrinter());
        var output = OutputPaths.Beside(input, "pdf");
        try
        {
            await printer.PrintAsync(input, output, ctx.Cancellation);
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        ctx.Progress.Report(1);
        return [output];
    }
}

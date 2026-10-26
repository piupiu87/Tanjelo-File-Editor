using System.Text;

namespace Tanjelo.Core.Converters;

/// <summary>A PDF's text as plain text or Markdown, in reading order. Pages without a text layer are read with Windows' OCR.</summary>
public sealed class PdfTextConverter : IConverter
{
    static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string> { ["page-breaks"] = "false" };

    static readonly Target[] Outputs =
    [
        new("txt", "TXT", "Text in reading order", Section.Document) { Parameters = Defaults },
        new("md", "Markdown", "Headings, lists and paragraphs", Section.Document) { Parameters = Defaults },
    ];

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? Outputs : [];

    // Opening the PDF is only worth it on the rare PC without any OCR language.
    public string? HintFor(string input) =>
        Formats.Key(input) == "pdf" && !PdfText.CanOcr && PdfText.LooksScanned(input) ? PdfText.NoOcrHint : null;

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        if (!OptionValues.TryFlag(target.Parameter("page-breaks"), out var pageBreaks))
            throw new ConversionException($"'page-breaks' must be true or false, not '{target.Parameter("page-breaks")}'.");

        var text = await PdfText.ReadAsync(input, ctx, ctx.Progress);
        var content = target.Key == "md" ? text.ToMarkdown(pageBreaks) : text.ToPlainText(pageBreaks);
        var output = OutputPaths.Beside(input, target.Key);
        await File.WriteAllTextAsync(output, content, new UTF8Encoding(false), ctx.Cancellation);
        text.AddNotes(ctx);
        return [output];
    }
}

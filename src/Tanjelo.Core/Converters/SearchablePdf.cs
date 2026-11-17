using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Windows.Media.Ocr;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Make searchable: the pages without text, such as scans, are read with Windows' OCR and get each word as invisible
/// text over it, so they can be searched, selected and copied while they look the same. Pages with text are left alone.
/// Shown only when Windows has an OCR language; otherwise PDF → TXT's hint says what to add.
/// </summary>
public sealed class SearchablePdfTool(Toolbox tools) : IConverter
{
    static readonly Target Entry = new("searchable-pdf", Strings.MakeSearchable, Strings.ScannedPagesGetTextYouCanSearch, Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = Strings.ReadingTheScannedPages,
        Done = Strings.MadeSearchable,
        // tanj only: an OCR language other than the user's, such as --language=ja; tanj options lists those installed.
        Parameters = new Dictionary<string, string> { ["language"] = "" },
    };

    public IEnumerable<Target> TargetsFor(string input) => tools.Ocr && Formats.Key(input) == "pdf" ? [Entry] : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var pages = await Task.Run(() => PdfText.PagesWithoutText(input), ctx.Cancellation);
        if (pages.Count == 0) throw new ConversionException(Strings.EveryPageAlreadyHasText);
        var output = OutputPaths.Suffixed(input, "searchable", "pdf");
        await OcrLayer.WriteAsync(input, output, pages, target.Parameter("language"), ctx);
        return [output];
    }
}

/// <summary>Reads pages with Windows' OCR and writes the words into a copy of the PDF as invisible text.</summary>
internal static partial class OcrLayer
{
    /// <summary>A word OCR read and its box, as fractions of the page as seen from its top-left corner.</summary>
    sealed record Word(string Text, double X, double Y, double Width, double Height);

    /// <param name="pages">The 0-based pages to read; null for all.</param>
    /// <param name="language">An OCR language such as "ja"; empty for the user's own.</param>
    public static async Task WriteAsync(string input, string output, IReadOnlyCollection<int>? pages, string language, JobContext ctx)
    {
        var engine = PdfText.CreateOcrEngine(language) ?? throw new ConversionException(language.Trim().Length == 0
            ? PdfText.NoOcrHint
            : string.Format(Strings.ThereSNoOCRLanguage, language.Trim(), string.Join(", ", PdfText.OcrLanguages())));
        var rendered = await PdfConverter.LoadAsync(input);
        pages ??= Enumerable.Range(0, (int)rendered.PageCount).ToList();
        var words = new Dictionary<int, List<Word>>();
        var done = 0;
        foreach (var index in pages)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            using var bitmap = await PdfConverter.RenderPageAsync(rendered, index, 300, OcrEngine.MaxImageDimension);
            var result = await engine.RecognizeAsync(bitmap);
            double width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            words[index] = result.Lines.SelectMany(l => l.Words)
                .Where(w => w.Text.Trim().Length > 0 && w.BoundingRect.Width > 0 && w.BoundingRect.Height > 0)
                .Select(w => new Word(w.Text.Trim(), w.BoundingRect.X / width, w.BoundingRect.Y / height, w.BoundingRect.Width / width, w.BoundingRect.Height / height))
                .ToList();
            ctx.Progress.Report(0.9 * ++done / pages.Count);
        }
        var read = words.Count(w => w.Value.Count > 0);
        if (read == 0) throw new ConversionException(Strings.OCRFoundNoTextOnTheScannedPages);

        await Task.Run(() =>
        {
            using var document = PdfFiles.OpenForChanges(input);
            foreach (var (index, found) in words.Where(w => w.Value.Count > 0))
                Write(document, document.Pages[index], found);
            PdfFiles.NoteIfSigned(document, ctx);
            PdfFiles.Save(document, output);
        }, ctx.Cancellation);
        ctx.Note(read == 1 ? Strings.N1PageReadWithOCR : string.Format(Strings.PagesReadWithOCR, read));
        var empty = words.Count - read;
        if (empty > 0) ctx.Note(empty == 1 ? Strings.N1PageHadNothingOCRCouldRead : string.Format(Strings.PagesHadNothingOCRCouldRead, empty));
        ctx.Progress.Report(1);
    }

    /// <summary>
    /// Each word in a form covering the page as seen, as tall as its box and stretched to its width, so that searching
    /// and selecting line up with the picture. The text is then made invisible (rendering mode 3), which PDFsharp
    /// can't do itself.
    /// </summary>
    static void Write(PdfDocument document, PdfPage page, List<Word> words)
    {
        var stamper = new PageStamper(page);
        var (width, height) = (stamper.Geometry.Width, stamper.Geometry.Height);
        var form = new XForm(document, new XSize(width, height));
        using (var g = XGraphics.FromForm(form))
        {
            foreach (var word in words)
            {
                // Measured at 100 points, then scaled: the box spans the font's height, and its width sets the stretch.
                var line = PdfLettering.Measure(word.Text, PdfLettering.Font(word.Text, 100));
                var size = Math.Max(1, word.Height * height / line.Height * 100);
                var font = PdfLettering.Font(word.Text, size);
                var stretch = word.Width * width / Math.Max(0.01, line.Width * size / 100);
                var state = g.Save();
                g.TranslateTransform(word.X * width, word.Y * height + line.Ascent * size / 100);
                g.ScaleTransform(stretch, 1);
                g.DrawString(word.Text, font, XBrushes.Black, 0, 0, XStringFormats.BaseLineLeft);
                g.Restore(state);
            }
        }
        form.DrawingFinished();
        Invisible(PageStamper.ObjectOf(form));
        stamper.Place(form, Affine.Identity);
        stamper.Finish();
    }

    [GeneratedRegex(@"(?<![\w/])BT(?!\w)")]
    private static partial Regex TextObject();

    /// <summary>Sets text rendering mode 3, neither filled nor stroked, at the start of every text object of a form.</summary>
    static void Invisible(PdfDictionary form)
    {
        var content = Encoding.Latin1.GetString(form.Stream.UnfilteredValue);
        var hidden = Encoding.Latin1.GetBytes(TextObject().Replace(content, "BT 3 Tr"));
        using var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.SmallestSize))
            zlib.Write(hidden);
        form.Stream.Value = packed.ToArray();
        form.Elements["/Filter"] = new PdfName("/FlateDecode");
        form.Elements.Remove("/DecodeParms");
    }
}

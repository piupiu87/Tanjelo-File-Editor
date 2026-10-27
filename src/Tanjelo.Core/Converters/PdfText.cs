using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Exceptions;
using Windows.Media.Ocr;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Converters;

/// <summary>A word of a PDF's text, and whether it was set in bold.</summary>
internal readonly record struct Token(string Text, bool Bold);

internal enum PartKind { Heading, Paragraph, ListItem }

/// <summary>A heading, paragraph or list item of a PDF's text.</summary>
internal sealed record Part(PartKind Kind, IReadOnlyList<Token> Tokens)
{
    /// <summary>For headings: 1 for the largest type, then 2 and 3.</summary>
    public int Level { get; init; }

    /// <summary>For list items: "-" for a bullet, or the item's own number such as "3.".</summary>
    public string Marker { get; init; } = "";

    public string Text => string.Join(" ", Tokens.Select(t => t.Text));
}

/// <summary>
/// The text of a PDF, page by page, worked out from where its letters are. PdfPig groups the letters into words,
/// lines and blocks (Docstrum) and puts the blocks in reading order, so columns are read one after the other. The
/// commonest type size is the body text, and larger sizes are headings, ranked by size. Pages without a text layer,
/// such as scans, are rendered and read with the OCR built into Windows, as paragraphs and lists.
/// </summary>
internal sealed class PdfText
{
    public const string NoOcrHint = "Scanned PDF pages need an OCR language. Add a language in Settings › Time & language › Language & region.";

    const double OcrDpi = 300;

    /// <summary>A run of lines in a heading size longer than this is a paragraph in large type, not a heading.</summary>
    const int MaxHeadingLength = 200;

    static readonly HashSet<char> AttachedBullets = ['•', '◦', '▪', '‣', '●', '○', '■', '□', '➢', '►', '✓', '✔'];
    static readonly HashSet<string> SeparateBullets = ["-", "–", "*", "·", "o"];
    static readonly Regex Numbered = new(@"^\(?(\d{1,3})[.)]$");
    static readonly Regex PageNumber = new(@"^((page|p\.)\s*)?[-–]?\s*\d{1,4}\s*[-–]?(\s*(of|/)\s*\d{1,4})?$", RegexOptions.IgnoreCase);

    PdfText(IReadOnlyList<IReadOnlyList<Part>> pages, int ocrPages, int unreadPages)
    {
        Pages = pages;
        OcrPages = ocrPages;
        UnreadPages = unreadPages;
    }

    public IReadOnlyList<IReadOnlyList<Part>> Pages { get; }

    /// <summary>The text of the first heading, such as a title for an e-book.</summary>
    public string? FirstHeading => Pages.SelectMany(p => p).FirstOrDefault(p => p.Kind == PartKind.Heading)?.Text;

    /// <summary>Pages without a text layer that were read with OCR.</summary>
    public int OcrPages { get; }

    /// <summary>Pages without a text layer that were left out because no OCR language is installed.</summary>
    public int UnreadPages { get; }

    /// <summary>Whether Windows has an OCR language installed, so scanned pages can be read.</summary>
    public static bool CanOcr
    {
        get
        {
            try { return OcrEngine.AvailableRecognizerLanguages.Count > 0; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; } // WinRT reports problems as COM errors of all kinds
        }
    }

    /// <summary>Whether the first page has no text layer, as with a scan. False when the PDF can't be read.</summary>
    public static bool LooksScanned(string path)
    {
        try
        {
            using var document = PigDocument.Open(path);
            return document.NumberOfPages > 0 && !document.GetPage(1).Letters.Any(l => !string.IsNullOrWhiteSpace(l.Value));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <param name="ocr">Whether to read pages without a text layer with OCR, which takes about a second a page.</param>
    public static async Task<PdfText> ReadAsync(string path, JobContext ctx, IProgress<double> progress, bool ocr = true)
    {
        var pages = new List<List<RawBlock>>();
        var scanned = new List<int>();
        await Task.Run(() =>
        {
            using var document = Open(path);
            var count = document.NumberOfPages;
            for (var i = 0; i < count; i++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                List<RawBlock> blocks;
                bool needsOcr;
                try
                {
                    blocks = Blocks(document.GetPage(i + 1), out needsOcr);
                }
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                {
                    // A page PdfPig can't make sense of may still render, so it gets read like a scan.
                    (blocks, needsOcr) = ([], true);
                }
                pages.Add(blocks);
                if (needsOcr) scanned.Add(i);
                progress.Report(0.3 * (i + 1) / count);
            }
        }, ctx.Cancellation);

        int ocrPages = 0, unreadPages = 0;
        if (scanned.Count > 0 && ocr)
        {
            var engine = CreateOcrEngine();
            if (engine is null)
            {
                unreadPages = scanned.Count(i => pages[i].Count == 0);
            }
            else
            {
                var document = await PdfConverter.LoadAsync(path);
                for (var n = 0; n < scanned.Count; n++)
                {
                    ctx.Cancellation.ThrowIfCancellationRequested();
                    var blocks = await OcrAsync(engine, document, scanned[n]);
                    if (blocks.Count > 0)
                    {
                        pages[scanned[n]] = blocks;
                        ocrPages++;
                    }
                    progress.Report(0.3 + 0.7 * (n + 1) / scanned.Count);
                }
            }
        }
        progress.Report(1);

        return new PdfText(Classify(pages), ocrPages, unreadPages);
    }

    /// <summary>Fails when nothing was read, saying why.</summary>
    public void EnsureText()
    {
        if (Pages.All(p => p.Count == 0))
            throw new ConversionException(UnreadPages > 0
                ? "This PDF has no text layer, and no OCR language is installed to read it. " + NoOcrHint[(NoOcrHint.IndexOf('.') + 2)..]
                : "This PDF has no text in it.");
    }

    public void AddNotes(JobContext ctx)
    {
        if (OcrPages > 0) ctx.Note(OcrPages == 1 ? "1 page read with OCR" : $"{OcrPages} pages read with OCR");
        if (UnreadPages > 0) ctx.Note(UnreadPages == 1 ? "1 scanned page left out: no OCR language is installed" : $"{UnreadPages} scanned pages left out: no OCR language is installed");
    }

    static PigDocument Open(string path)
    {
        try
        {
            return PigDocument.Open(path);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new ConversionException("This PDF is password-protected.", ex);
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException or OutOfMemoryException))
        {
            throw new ConversionException("This doesn't look like a valid PDF.", ex);
        }
    }

    // ---- Reading pages

    /// <summary>
    /// A line of words. For OCR'd pages the size is NaN and positions are in pixels from the top instead of points
    /// from the bottom; only distances between lines of one page are compared.
    /// </summary>
    sealed record RawLine(IReadOnlyList<Token> Tokens, double Size, double Left, double Right, double Baseline, double Height);

    sealed record RawBlock(IReadOnlyList<RawLine> Lines);

    static List<RawBlock> Blocks(Page page, out bool needsOcr)
    {
        var letters = DuplicateOverlappingTextProcessor.Get(page.Letters); // drops the copies some PDFs print to fake bold
        var visible = letters.Count(l => !string.IsNullOrWhiteSpace(l.Value));
        // A few letters over a picture is usually a page number stamped on a scan.
        needsOcr = visible == 0 || (visible < 10 && page.GetImages().Any());
        if (visible == 0) return [];

        var words = NearestNeighbourWordExtractor.Instance.GetWords(letters);
        var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
        return WithMarkersJoined(UnsupervisedReadingOrderDetector.Instance.Get(blocks)
            .OrderBy(b => b.ReadingOrder)
            .Select(b => new RawBlock(b.TextLines.Select(Line).Where(l => l.Tokens.Count > 0).ToList()))
            .Where(b => b.Lines.Count > 0)
            .ToList());
    }

    static RawLine Line(TextLine line)
    {
        var tokens = line.Words.Select(w => new Token(Clean(w.Text), IsBold(w))).Where(t => t.Text.Length > 0).ToList();
        var letters = line.Words.SelectMany(w => w.Letters).Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
        // The size most of its letters have, to the half point, so a footnote mark doesn't change it.
        var size = letters.GroupBy(l => Math.Round(l.PointSize * 2) / 2).MaxBy(g => g.Count())?.Key ?? 0;
        var baseline = letters.Count > 0 ? letters.Average(l => l.StartBaseLine.Y) : line.BoundingBox.Bottom;
        return new RawLine(tokens, size, line.BoundingBox.Left, line.BoundingBox.Right, baseline, size);
    }

    /// <summary>
    /// Word sets a list's bullets and numbers apart from the text with a tab, so Docstrum tends to make a block of
    /// them alone. Each goes back to the start of the line beside it, on the same baseline.
    /// </summary>
    static List<RawBlock> WithMarkersJoined(List<RawBlock> blocks)
    {
        var markerBlocks = blocks.Where(b => b.Lines.All(l => l.Tokens.Count == 1 && IsMarker(l.Tokens[0].Text))).ToHashSet();
        if (markerBlocks.Count == 0) return blocks;

        var candidates = blocks.Where(b => !markerBlocks.Contains(b)).SelectMany(b => b.Lines).ToList();
        var joined = new Dictionary<RawLine, RawLine>(ReferenceEqualityComparer.Instance);
        var unjoined = new Dictionary<RawBlock, List<RawLine>>();
        foreach (var block in markerBlocks)
        {
            unjoined[block] = [];
            foreach (var marker in block.Lines)
            {
                var line = candidates
                    .Where(l => !joined.ContainsKey(l) && Math.Abs(l.Baseline - marker.Baseline) < marker.Height * 0.4
                        && l.Left > marker.Right - 1 && l.Left - marker.Right < marker.Height * 6)
                    .MinBy(l => l.Left - marker.Right);
                if (line is null) unjoined[block].Add(marker);
                else joined[line] = line with { Tokens = [marker.Tokens[0], .. line.Tokens], Left = marker.Left };
            }
        }
        return blocks
            .Select(b => markerBlocks.Contains(b) ? new RawBlock(unjoined[b]) : new RawBlock(b.Lines.Select(l => joined.GetValueOrDefault(l, l)).ToList()))
            .Where(b => b.Lines.Count > 0)
            .ToList();
    }

    static bool IsMarker(string word) =>
        (word.Length == 1 && AttachedBullets.Contains(word[0])) || SeparateBullets.Contains(word) || Numbered.IsMatch(word);

    static bool IsBold(Word word)
    {
        var bold = word.Letters.Count(l => l.FontDetails?.IsBold == true || BoldName(l.FontName));
        return bold * 2 > word.Letters.Count;
    }

    static bool BoldName(string? font) => font is not null
        && (font.Contains("bold", StringComparison.OrdinalIgnoreCase) || font.Contains("black", StringComparison.OrdinalIgnoreCase)
            || font.Contains("heavy", StringComparison.OrdinalIgnoreCase));

    /// <summary>Spells out ligatures, maps Symbol and Wingdings bullets to •, and drops control characters.</summary>
    static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case 'ﬀ': clean.Append("ff"); break;
                case 'ﬁ': clean.Append("fi"); break;
                case 'ﬂ': clean.Append("fl"); break;
                case 'ﬃ': clean.Append("ffi"); break;
                case 'ﬄ': clean.Append("ffl"); break;
                case 'ﬅ' or 'ﬆ': clean.Append("st"); break;
                case '' or '' or '' or '': clean.Append('•'); break;
                // A soft hyphen only matters at the end of a line, where it can still join a broken word.
                case '­': if (i == text.Length - 1) clean.Append(c); break;
                case ' ': clean.Append(' '); break;
                default: if (!char.IsControl(c)) clean.Append(c); break;
            }
        }
        return clean.ToString().Trim();
    }

    // ---- OCR

    internal static OcrEngine? CreateOcrEngine()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e is not null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    static async Task<List<RawBlock>> OcrAsync(OcrEngine engine, Windows.Data.Pdf.PdfDocument document, int index)
    {
        using var bitmap = await PdfConverter.RenderPageAsync(document, index, OcrDpi, OcrEngine.MaxImageDimension);
        return Blocks(await engine.RecognizeAsync(bitmap));
    }

    /// <summary>The text in a picture, read with Windows' OCR, as paragraphs and lists; null when there's no OCR language.</summary>
    /// <param name="bitmap">At most <see cref="OcrEngine.MaxImageDimension"/> pixels on its longer side.</param>
    public static async Task<PdfText?> ReadPictureAsync(Windows.Graphics.Imaging.SoftwareBitmap bitmap)
    {
        if (CreateOcrEngine() is not { } engine) return null;
        var blocks = Blocks(await engine.RecognizeAsync(bitmap));
        return new PdfText(Classify([blocks]), blocks.Count > 0 ? 1 : 0, 0);
    }

    /// <summary>OCR'd lines grouped into blocks by the gaps between them.</summary>
    static List<RawBlock> Blocks(OcrResult result)
    {
        var lines = result.Lines
            .Where(l => l.Words.Count > 0)
            .Select(l =>
            {
                var boxes = l.Words.Select(w => w.BoundingRect).ToList();
                double top = boxes.Min(b => b.Y), bottom = boxes.Max(b => b.Y + b.Height);
                double left = boxes.Min(b => b.X), right = boxes.Max(b => b.X + b.Width);
                var tokens = l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => new Token(Clean(t), false)).Where(t => t.Text.Length > 0).ToList();
                return (Line: new RawLine(tokens, double.NaN, left, right, bottom, bottom - top), Top: top, Bottom: bottom);
            })
            .Where(l => l.Line.Tokens.Count > 0)
            .ToList();
        if (lines.Count == 0) return [];

        // Lines close together, one under the other, make a paragraph; a wider gap or a jump to another column starts the next.
        var typical = lines.Select(l => l.Line.Height).Order().ElementAt(lines.Count / 2);
        var blocks = new List<RawBlock>();
        var current = new List<RawLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                var (above, line) = (lines[i - 1], lines[i]);
                var gap = line.Top - above.Bottom;
                var overlaps = line.Line.Left < above.Line.Right && above.Line.Left < line.Line.Right;
                if (gap > typical * 0.8 || gap < -typical || !overlaps)
                {
                    blocks.Add(new RawBlock(current));
                    current = [];
                }
            }
            current.Add(lines[i].Line);
        }
        blocks.Add(new RawBlock(current));
        return blocks;
    }

    // ---- Structure

    static List<IReadOnlyList<Part>> Classify(List<List<RawBlock>> pages)
    {
        // The body is the size with the most text; clearly larger sizes are headings, the largest being level 1.
        var weights = pages.SelectMany(p => p).SelectMany(b => b.Lines)
            .Where(l => l.Size > 0)
            .GroupBy(l => l.Size)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Tokens.Sum(t => t.Text.Length)));
        var body = weights.Count == 0 ? 0 : weights.MaxBy(w => w.Value).Key;
        var levels = new Dictionary<double, int>();
        var level = 0;
        var rankSize = double.MaxValue;
        foreach (var size in weights.Keys.Where(s => IsHeadingSize(s, body)).OrderDescending())
        {
            if (size < rankSize * 0.96) (level, rankSize) = (level + 1, size); // sizes a few percent apart are one level
            levels[size] = Math.Min(level, 3);
        }

        return pages.Select(page => (IReadOnlyList<Part>)WithoutPageNumbers(page.SelectMany(b => Parts(b, levels)).ToList())).ToList();
    }

    static bool IsHeadingSize(double size, double body) => body > 0 && size >= body * 1.15 && size >= body + 1;

    /// <summary>A page number alone at the top or bottom of a page.</summary>
    static List<Part> WithoutPageNumbers(List<Part> parts)
    {
        if (parts.Count > 0 && PageNumber.IsMatch(parts[^1].Text)) parts.RemoveAt(parts.Count - 1);
        if (parts.Count > 0 && PageNumber.IsMatch(parts[0].Text)) parts.RemoveAt(0);
        return parts;
    }

    static IEnumerable<Part> Parts(RawBlock block, Dictionary<double, int> levels)
    {
        // One size at a time, so a heading that Docstrum put in the block of its paragraph still comes out as a heading.
        int LevelOf(RawLine line) => levels.GetValueOrDefault(line.Size);
        var lines = block.Lines;
        for (var start = 0; start < lines.Count;)
        {
            var level = LevelOf(lines[start]);
            var end = start;
            while (end < lines.Count && LevelOf(lines[end]) == level) end++;
            var run = lines.Skip(start).Take(end - start).ToList();
            if (level > 0 && run.Sum(l => l.Tokens.Sum(t => t.Text.Length + 1)) <= MaxHeadingLength)
            {
                var tokens = new List<Token>();
                foreach (var line in run) Append(tokens, line.Tokens);
                yield return new Part(PartKind.Heading, tokens) { Level = level };
            }
            else
            {
                foreach (var part in Body(run)) yield return part;
            }
            start = end;
        }
    }

    /// <summary>Paragraphs and list items. A list item goes on while its lines are indented past its bullet.</summary>
    static IEnumerable<Part> Body(List<RawLine> lines)
    {
        var margin = lines.Min(l => l.Left);
        List<Token>? tokens = null;
        var kind = PartKind.Paragraph;
        var marker = "";
        var itemLeft = 0.0;
        foreach (var line in lines)
        {
            var item = ListItem(line.Tokens);
            var startsNew = item is not null
                || tokens is null
                || (kind == PartKind.ListItem && line.Left <= itemLeft + line.Height * 0.3)
                || (kind == PartKind.Paragraph && line.Left > margin + line.Height * 0.8 && EndsSentence(tokens)); // a first-line indent
            if (startsNew)
            {
                if (tokens is { Count: > 0 }) yield return new Part(kind, tokens) { Marker = marker };
                tokens = [];
                (kind, marker, itemLeft) = item is { } found ? (PartKind.ListItem, found.Marker, line.Left) : (PartKind.Paragraph, "", 0);
            }
            Append(tokens!, item?.Words ?? line.Tokens);
        }
        if (tokens is { Count: > 0 }) yield return new Part(kind, tokens) { Marker = marker };
    }

    /// <summary>The marker of a line that starts a list item, and the rest of its words.</summary>
    static (string Marker, IReadOnlyList<Token> Words)? ListItem(IReadOnlyList<Token> tokens)
    {
        var first = tokens[0].Text;
        if (AttachedBullets.Contains(first[0]))
        {
            IReadOnlyList<Token> rest = first.Length > 1 ? [tokens[0] with { Text = first[1..].TrimStart() }, .. tokens.Skip(1)] : tokens.Skip(1).ToList();
            return rest.Count > 0 ? ("-", rest) : null;
        }
        if (tokens.Count > 1 && SeparateBullets.Contains(first))
            return ("-", tokens.Skip(1).ToList());
        if (tokens.Count > 1 && Numbered.Match(first) is { Success: true } number)
            return (number.Groups[1].Value + ".", tokens.Skip(1).ToList());
        return null;
    }

    /// <summary>Adds a line's words, rejoining a word that was hyphenated at the end of the line before.</summary>
    static void Append(List<Token> into, IReadOnlyList<Token> tokens)
    {
        if (tokens.Count == 0) return;
        if (into.Count > 0 && into[^1].Text is { Length: > 1 } last && last[^1] is '-' or '­' or '‐' && char.IsLetter(last[^2])
            && char.IsLower(tokens[0].Text[0]))
        {
            into[^1] = into[^1] with { Text = last[..^1] + tokens[0].Text };
            into.AddRange(tokens.Skip(1));
            return;
        }
        if (into.Count > 0 && into[^1].Text.EndsWith('­'))
            into[^1] = into[^1] with { Text = into[^1].Text.TrimEnd('­') };
        into.AddRange(tokens);
    }

    static bool EndsSentence(IReadOnlyList<Token> tokens) => tokens.Count > 0 && tokens[^1].Text[^1] is '.' or '!' or '?' or ':' or ';' or '"' or '”' or '’' or ')' or '…';

    // ---- Writing

    /// <summary>The parts in order, with null for a page break. Without page breaks, a paragraph that runs onto the next page is put back together.</summary>
    List<Part?> Flow(bool pageBreaks)
    {
        var flow = new List<Part?>();
        foreach (var page in Pages.Where(p => p.Count > 0))
        {
            if (flow.Count > 0 && pageBreaks) flow.Add(null);
            for (var i = 0; i < page.Count; i++)
            {
                if (i == 0 && !pageBreaks && flow.Count > 0 && flow[^1] is { } last && ContinuesWith(last, page[0]))
                {
                    var joined = last.Tokens.ToList();
                    Append(joined, page[0].Tokens);
                    flow[^1] = last with { Tokens = joined };
                }
                else
                {
                    flow.Add(page[i]);
                }
            }
        }
        return flow;
    }

    static bool ContinuesWith(Part last, Part next) =>
        last.Kind is PartKind.Paragraph or PartKind.ListItem && next.Kind == PartKind.Paragraph
        && !EndsSentence(last.Tokens) && char.IsLower(next.Tokens[0].Text[0]);

    public string ToPlainText(bool pageBreaks) => Write(Flow(pageBreaks), markdown: false, "\r\n");

    public string ToMarkdown(bool pageBreaks) => Write(Flow(pageBreaks), markdown: true, "\n");

    /// <summary>One page's text, as plain text, such as for a slide's speaker notes.</summary>
    public string PageText(int index) => Write([.. Pages[index]], markdown: false, "\r\n").TrimEnd();

    static string Write(IReadOnlyList<Part?> parts, bool markdown, string newline)
    {
        var text = new StringBuilder();
        Part? previous = null;
        foreach (var part in parts)
        {
            // Items of one list go on consecutive lines; a bulleted list next to a numbered one is another list.
            var sameList = previous?.Kind == PartKind.ListItem && part?.Kind == PartKind.ListItem && (previous.Marker == "-") == (part.Marker == "-");
            if (text.Length > 0)
                text.Append(sameList ? newline : newline + newline);
            previous = part;
            if (part is null)
            {
                text.Append("---");
                continue;
            }
            text.Append(part.Kind switch
            {
                PartKind.Heading when markdown => new string('#', part.Level) + " " + Inline(part.Tokens.Select(t => t with { Bold = false }).ToList()),
                PartKind.ListItem when markdown => part.Marker + " " + Inline(part.Tokens),
                PartKind.ListItem => (part.Marker == "-" ? "•" : part.Marker) + " " + part.Text,
                _ when markdown => EscapeStart(Inline(part.Tokens)),
                _ => part.Text,
            });
        }
        return text.Append(newline).ToString();
    }

    /// <summary>Markdown for a run of words, with consecutive bold words in one pair of **.</summary>
    static string Inline(IReadOnlyList<Token> tokens)
    {
        var text = new StringBuilder();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i > 0) text.Append(' ');
            var opens = tokens[i].Bold && (i == 0 || !tokens[i - 1].Bold);
            var closes = tokens[i].Bold && (i == tokens.Count - 1 || !tokens[i + 1].Bold);
            text.Append(opens ? "**" : "").Append(Escape(tokens[i].Text)).Append(closes ? "**" : "");
        }
        return text.ToString();
    }

    /// <summary>Escapes what Markdown would read as formatting in a word, but leaves snake_case and the like alone.</summary>
    internal static string Escape(string word)
    {
        var text = new StringBuilder(word.Length + 4);
        for (var i = 0; i < word.Length; i++)
        {
            var c = word[i];
            var next = i + 1 < word.Length ? word[i + 1] : '\0';
            var escape = c switch
            {
                '\\' or '*' or '`' => true,
                '_' => i == 0 || i == word.Length - 1 || !char.IsLetterOrDigit(word[i - 1]) || !char.IsLetterOrDigit(next),
                '<' => char.IsLetter(next) || next is '/' or '!' or '?',
                '[' => true,
                ']' => next == '(',
                '~' => next == '~',
                _ => false,
            };
            if (escape) text.Append('\\');
            text.Append(c);
        }
        return text.ToString();
    }

    /// <summary>Escapes a paragraph's start where Markdown would see a heading, quote, list or rule.</summary>
    internal static string EscapeStart(string paragraph)
    {
        if (paragraph.Length == 0) return paragraph;
        if (paragraph[0] is '#' or '>' or '-' or '+' or '=' or '|') return "\\" + paragraph;
        var digits = 0;
        while (digits < paragraph.Length && char.IsAsciiDigit(paragraph[digits])) digits++;
        if (digits is > 0 and < 10 && digits < paragraph.Length && paragraph[digits] is '.' or ')'
            && (digits + 1 == paragraph.Length || paragraph[digits + 1] == ' '))
            return paragraph[..digits] + "\\" + paragraph[digits..];
        return paragraph;
    }
}

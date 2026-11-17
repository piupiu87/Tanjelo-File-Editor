using System.Globalization;
using System.Security.Cryptography;
using ImageMagick;
using PdfSharp.Pdf;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Tokens;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Split…: a PDF into parts of every N pages, at its top-level bookmarks, at listed pages, or each under a size in
/// MB, in a "report parts" folder. Pages are copied as they are, and the bookmarks into each part go with it, as in
/// Organize pages. The quick Split into pages stays as it is.
/// </summary>
public sealed class SplitPdfPartsTool : IConverter, IPreviewer, IValidator
{
    const long Megabyte = 1024 * 1024;

    static readonly Target Entry = new Target("split-pdf-parts", Strings.SplitParts, Strings.EveryFewPagesAtBookmarksOrUnderASize, Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = Strings.Splitting,
        Done = Strings.SplitIntoPages,
    }.WithOptions(
        new Option("by", Strings.SplitBy, OptionKind.Choice, "pages")
        {
            Choices = [new("pages", Strings.EveryFewPages), new("bookmarks", Strings.AtBookmarks), new("list", Strings.AtListedPages), new("size", Strings.UnderASize)],
            Hint = Strings.SplitHint,
        },
        new Option("every", Strings.EveryHowManyPages, OptionKind.Number, "2") { Min = 1, Max = 100_000, Whole = true, ShownWhen = new("by", "pages") },
        new Option("list", Strings.Parts, OptionKind.Text, "") { Remember = false, ShownWhen = new("by", "list"), Hint = Strings.PartsHint },
        new Option("size", Strings.AtMost, OptionKind.Number, "10") { Min = 0.1, Max = 100_000, Unit = Strings.MB, ShownWhen = new("by", "size") });

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? [Entry] : [];

    /// <summary>A part: its first and last page, 0-based, and its bookmark's title when split at bookmarks.</summary>
    internal sealed record Part(int First, int Last, string? Title = null);

    /// <summary>The parts for every way but by size, which is worked out by saving.</summary>
    static List<Part> Plan(PdfDocument document, Target target, string name)
    {
        var count = document.PageCount;
        switch (target.Choice("by"))
        {
            case "list":
                return PageRanges.Parse(target.Text("list"), count, name).Select(r => new Part(r.First, r.Last)).ToList();
            case "bookmarks":
                var starts = OrganizePdfTool.Bookmarks.Read(document)
                    .Where(b => b.Page is not null)
                    .GroupBy(b => b.Page!.Value)
                    .Select(g => (Page: g.Key, g.First().Title))
                    .OrderBy(s => s.Page)
                    .ToList();
                if (starts.Count == 0) throw new ConversionException(Strings.ThisPDFHasNoBookmarksToSplitAt);
                if (starts[0].Page > 0) starts.Insert(0, (0, Strings.BeforeTheFirstBookmark));
                return starts.Select((s, i) => new Part(s.Page, i + 1 < starts.Count ? starts[i + 1].Page - 1 : count - 1, s.Title)).ToList();
            default:
                var every = (int)target.Number("every");
                return Enumerable.Range(0, (count + every - 1) / every).Select(i => new Part(i * every, Math.Min(count, (i + 1) * every) - 1)).ToList();
        }
    }

    public Task<SettingsProblem?> ValidateAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx) => Task.Run<SettingsProblem?>(() =>
    {
        if (target.Choice("by") is not ("list" or "bookmarks")) return null;
        PdfDocument document;
        try
        {
            document = PdfFiles.OpenForImport(inputs[0]);
        }
        catch (ConversionException)
        {
            return null; // the run says what's wrong with the file
        }
        using (document)
        {
            try
            {
                Plan(document, target, Path.GetFileName(inputs[0]));
                return null;
            }
            catch (ConversionException ex)
            {
                return new SettingsProblem(target.Choice("by") == "list" ? "list" : "by", ex.Message);
            }
        }
    }, ctx.Cancellation);

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var source = PdfFiles.OpenForImport(input);
        var name = Path.GetFileName(input);
        var bookmarks = OrganizePdfTool.Bookmarks.Read(source);
        var baseName = Formats.BaseName(input);
        var folder = OutputPaths.Unique(OutputPaths.DirectoryOf(input), baseName + " parts", "");
        Directory.CreateDirectory(folder);
        try
        {
            if (target.Choice("by") == "size")
                BySize(source, bookmarks, (long)(target.Number("size") * Megabyte), folder, baseName, ctx);
            else
            {
                var parts = Plan(source, target, name);
                var digits = Math.Max(target.Choice("by") == "bookmarks" ? 2 : 1, parts.Count.ToString(CultureInfo.InvariantCulture).Length);
                for (var i = 0; i < parts.Count; i++)
                {
                    ctx.Cancellation.ThrowIfCancellationRequested();
                    var number = (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
                    var title = parts[i].Title is { } t && Safe(t) is { Length: > 0 } safe ? $" {safe}" : "";
                    using var part = Copy(source, bookmarks, parts[i].First, parts[i].Last);
                    PdfFiles.Save(part, OutputPaths.Unique(folder, $"{baseName}-{number}{title}", "pdf"));
                    ctx.Progress.Report((i + 1.0) / parts.Count);
                }
            }
        }
        catch
        {
            OutputPaths.TryDelete(folder);
            throw;
        }
        if (OrganizePdfTool.Losses(source) is { } losses) ctx.Note(losses);
        return [folder];
    }, ctx.Cancellation);

    /// <summary>A new PDF of pages first to last, with the bookmarks that open one of them.</summary>
    static PdfDocument Copy(PdfDocument source, IReadOnlyList<OrganizePdfTool.Bookmark> bookmarks, int first, int last)
    {
        var part = PdfFiles.NewLike(source);
        var copies = new Dictionary<int, PdfPage>();
        for (var page = first; page <= last; page++)
            copies[page] = part.AddPage(source.Pages[page]);
        OrganizePdfTool.Bookmarks.Write(part.Outlines, bookmarks, page => copies.GetValueOrDefault(page));
        return part;
    }

    /// <summary>
    /// Fills each part with as many pages as fit under the size, measured by saving: the count is doubled until it's
    /// too many, then halved back. A page bigger than the size on its own makes a part by itself.
    /// </summary>
    static void BySize(PdfDocument source, IReadOnlyList<OrganizePdfTool.Bookmark> bookmarks, long limit, string folder, string baseName, JobContext ctx)
    {
        var count = source.PageCount;
        var parts = new List<byte[]>();
        var alone = new List<int>();
        for (var first = 0; first < count;)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            byte[]? Saved(int pages)
            {
                using var part = Copy(source, bookmarks, first, first + pages - 1);
                using var bytes = new MemoryStream();
                part.Save(bytes, closeStream: false);
                return bytes.Length <= limit ? bytes.ToArray() : null;
            }
            var fits = Saved(1);
            int good = 1, bad = 0;
            if (fits is null)
            {
                alone.Add(first + 1);
                using var big = Copy(source, bookmarks, first, first);
                using var bytes = new MemoryStream();
                big.Save(bytes, closeStream: false);
                fits = bytes.ToArray();
            }
            else
            {
                for (var step = 2; first + good < count; step *= 2)
                {
                    var tried = Math.Min(step, count - first);
                    if (Saved(tried) is { } more) (good, fits) = (tried, more);
                    else
                    {
                        bad = tried;
                        break;
                    }
                    if (tried == count - first) break;
                }
                while (bad > good + 1)
                {
                    var middle = (good + bad) / 2;
                    if (Saved(middle) is { } more) (good, fits) = (middle, more);
                    else bad = middle;
                }
            }
            parts.Add(fits);
            first += good;
            ctx.Progress.Report(0.9 * first / count);
        }
        var digits = parts.Count.ToString(CultureInfo.InvariantCulture).Length;
        for (var i = 0; i < parts.Count; i++)
            File.WriteAllBytes(OutputPaths.Unique(folder, $"{baseName}-{(i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}", "pdf"), parts[i]);
        if (alone.Count > 0)
        {
            var size = Describe.Size(limit);
            ctx.Note(alone.Count == 1 ? string.Format(Strings.N1PageIsBiggerThanOnItsOwn, alone[0], size)
                : string.Format(Strings.PagesAreBiggerThanOnTheirOwn, string.Join(", ", alone), size));
        }
    }

    /// <summary>A bookmark's title fit for a file name: no characters Windows forbids, and not too long.</summary>
    static string Safe(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(title.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        safe = string.Join(' ', safe.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
        return safe.Length > 60 ? safe[..60].TrimEnd('.', ' ') : safe;
    }

    /// <summary>How many parts, and which pages each has.</summary>
    public Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx) => Task.Run(() =>
    {
        using var source = PdfFiles.OpenForImport(inputs[0]);
        var now = new Fact(Strings.Now, $"{Describe.Size(new FileInfo(inputs[0]).Length)} · {Describe.Count(source.PageCount, Strings.OnePage, Strings.NPages)}");
        if (target.Choice("by") == "size")
            return new Preview(null, [now, new Fact(Strings.Parts, string.Format(Strings.PartsFilledUpTo, Describe.Size((long)(target.Number("size") * Megabyte))))]);
        var parts = Plan(source, target, Path.GetFileName(inputs[0]));
        string Pages(Part p) => p.First == p.Last ? $"{p.First + 1}" : $"{p.First + 1}–{p.Last + 1}";
        var shown = string.Join(", ", parts.Take(12).Select(p => p.Title is { } title ? $"{title} ({Pages(p)})" : Pages(p))) + (parts.Count > 12 ? ", …" : "");
        return new Preview(null, [now, new Fact(Strings.Parts, $"{Describe.Count(parts.Count, Strings.OnePart, Strings.NParts)}: {shown}")]);
    }, ctx.Cancellation);
}

/// <summary>
/// Save pictures: the pictures in a PDF as files in a "report pictures" folder, named by page, such as
/// report-p03-1.jpg. JPEGs are written byte for byte (CMYK ones turned RGB so they look right everywhere), JPEG 2000
/// as .jp2, and the rest as PNG, with transparency where the PDF has a soft mask. Each picture is saved once, however
/// many pages show it; stencil masks and pictures under 16 pixels are left out.
/// </summary>
public sealed class SavePicturesTool : IConverter
{
    static readonly Target Entry = new("save-pictures", Strings.SavePictures, Strings.ThePicturesInItAsFiles, Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = Strings.SavingThePictures,
        Done = Strings.PicturesSaved,
    };

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? [Entry] : [];

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var pdf = Open(input);
        var baseName = Formats.BaseName(input);
        var digits = Math.Max(2, pdf.NumberOfPages.ToString(CultureInfo.InvariantCulture).Length);
        var folder = OutputPaths.Unique(OutputPaths.DirectoryOf(input), baseName + " pictures", "");
        var seen = new HashSet<string>();
        var saved = 0;
        var undecoded = 0;
        try
        {
            for (var n = 1; n <= pdf.NumberOfPages; n++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                var onPage = 0;
                foreach (var picture in pdf.GetPage(n).GetImages())
                {
                    if (picture.IsImageMask || picture.WidthInSamples < 16 || picture.HeightInSamples < 16) continue;
                    if (!seen.Add(Fingerprint(picture))) continue;
                    if (Encode(picture) is not { } file)
                    {
                        undecoded++;
                        continue;
                    }
                    Directory.CreateDirectory(folder);
                    var name = $"{baseName}-p{n.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}-{++onPage}";
                    File.WriteAllBytes(OutputPaths.Unique(folder, name, file.Ext), file.Bytes);
                    saved++;
                }
                ctx.Progress.Report((double)n / pdf.NumberOfPages);
            }
        }
        catch
        {
            OutputPaths.TryDelete(folder);
            throw;
        }
        if (saved == 0)
        {
            OutputPaths.TryDelete(folder);
            throw new ConversionException(undecoded > 0 ? string.Format(Strings.PicturesCouldnTBeSaved, undecoded) : Strings.ThisPDFHasNoPictures);
        }
        if (undecoded > 0) ctx.Note(undecoded == 1 ? Strings.N1PictureCouldnTBeSaved : string.Format(Strings.PicturesCouldnTBeSaved, undecoded));
        return [folder];
    }, ctx.Cancellation);

    static PigDocument Open(string path)
    {
        try
        {
            return PigDocument.Open(path);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new ConversionException(Strings.ThisPDFIsPasswordProtected, ex);
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException or OutOfMemoryException))
        {
            throw new ConversionException(Strings.ThisDoesnTLookLikeAValid3, ex);
        }
    }

    /// <summary>The same picture drawn on several pages has the same data and size.</summary>
    static string Fingerprint(IPdfImage picture) =>
        $"{picture.WidthInSamples}x{picture.HeightInSamples}:{Convert.ToHexString(SHA256.HashData(picture.RawBytes))}";

    static string? Filter(IPdfImage picture) => picture.ImageDictionary.Data.GetValueOrDefault("Filter") switch
    {
        NameToken name => name.Data,
        ArrayToken { Data.Count: > 0 } array when array.Data[^1] is NameToken last => last.Data,
        _ => null,
    };

    /// <summary>The picture as a file's bytes and its extension; null when it can't be decoded, such as JBIG2.</summary>
    static (byte[] Bytes, string Ext)? Encode(IPdfImage picture)
    {
        var raw = picture.RawBytes.ToArray();
        switch (Filter(picture))
        {
            case "DCTDecode":
                if (PdfCompressor.JpegChannels(raw) != 4) return (raw, "jpg");
                try
                {
                    // CMYK JPEGs show wrongly in most apps; this one is turned RGB.
                    using var cmyk = new MagickImage(raw);
                    cmyk.ColorSpace = ColorSpace.sRGB;
                    cmyk.Quality = 92;
                    return (cmyk.ToByteArray(MagickFormat.Jpeg), "jpg");
                }
                catch (MagickException)
                {
                    return null;
                }
            case "JPXDecode":
                return (raw, "jp2");
        }
        if (!picture.TryGetPng(out var png)) return null;
        if (picture.MaskImage is not { } mask || !mask.TryGetPng(out var alpha)) return (png, "png");
        try
        {
            using var image = new MagickImage(png);
            using var opacity = new MagickImage(alpha);
            opacity.ColorSpace = ColorSpace.Gray;
            if (opacity.Width != image.Width || opacity.Height != image.Height)
                opacity.Resize(new MagickGeometry(image.Width, image.Height) { IgnoreAspectRatio = true });
            image.Alpha(AlphaAction.Opaque);
            image.Composite(opacity, CompositeOperator.CopyAlpha);
            return (image.ToByteArray(MagickFormat.Png), "png");
        }
        catch (MagickException)
        {
            return (png, "png");
        }
    }
}

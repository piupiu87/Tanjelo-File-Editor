using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Tanjelo.Core.Converters;

/// <summary>
/// A list of pages in their new order, as tanj takes it: "3,1-2,5r90,1". A range runs either way ("5-1"), r90, r180
/// and r270 turn a page clockwise, a page listed twice is copied, and a page left out is deleted. Pages of other PDFs
/// have a letter, b for the first, c for the next: "1-2,b1-3".
/// </summary>
public static partial class PageList
{
    /// <param name="Source">0 for the PDF being organized, 1 for the first other PDF (b), and so on.</param>
    /// <param name="Index">The page's 0-based place in its PDF.</param>
    /// <param name="Rotation">How far to turn it clockwise from how it is now: 0, 90, 180 or 270.</param>
    public readonly record struct Page(int Source, int Index, int Rotation = 0);

    [GeneratedRegex(@"^([a-z])?(\d+)(?:-(\d+))?(?:r(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    /// <param name="counts">How many pages each PDF has, the one being organized first.</param>
    /// <param name="names">Their file names, for errors.</param>
    public static IReadOnlyList<Page> Parse(string text, IReadOnlyList<int> counts, IReadOnlyList<string> names)
    {
        var pages = new List<Page>();
        foreach (var token in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Token().Match(token);
            var rotation = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
            if (!match.Success || rotation is not (0 or 90 or 180 or 270))
                throw new ConversionException(string.Format(Strings.CouldnTReadInThePageList, token));
            var source = match.Groups[1].Success ? char.ToLowerInvariant(match.Groups[1].Value[0]) - 'a' : 0;
            if (source >= counts.Count)
                throw new ConversionException(string.Format(Strings.IsAPageOfAnotherPDFBut, token,
                    counts.Count == 1 ? Strings.NoOtherPDFWasGiven : string.Format(Strings.OnlyOtherGiven, counts.Count - 1, counts.Count == 2 ? Strings.PDFWas : Strings.PDFsWere)));
            var first = Number(match.Groups[2].Value, source);
            var last = match.Groups[3].Success ? Number(match.Groups[3].Value, source) : first;
            var step = last >= first ? 1 : -1;
            for (var page = first; page != last + step; page += step)
                pages.Add(new Page(source, page - 1, rotation));
        }
        if (pages.Count == 0)
            throw new ConversionException(Strings.ThePageListLeavesNoPages);
        return pages;

        int Number(string digits, int source)
        {
            var page = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
            if (page < 1 || page > counts[source])
                throw new ConversionException(string.Format(Strings.ThereSNoPageHas, digits, names[source], Describe.Count(counts[source], Strings.OnePage, Strings.NPages)));
            return page;
        }
    }

    /// <summary>The shortest list for these pages, with runs written as ranges: "3,1-2,2r90".</summary>
    public static string Format(IReadOnlyList<Page> pages)
    {
        var parts = new List<string>();
        for (var i = 0; i < pages.Count;)
        {
            var first = pages[i];
            var end = i;
            var step = i + 1 < pages.Count && Alike(pages[i + 1], first) ? pages[i + 1].Index - first.Index : 0;
            if (Math.Abs(step) == 1)
                while (end + 1 < pages.Count && Alike(pages[end + 1], first) && pages[end + 1].Index - pages[end].Index == step)
                    end++;
            var letter = first.Source == 0 ? "" : ((char)('a' + first.Source)).ToString();
            var numbers = end > i ? $"{first.Index + 1}-{pages[end].Index + 1}" : $"{first.Index + 1}";
            parts.Add(letter + numbers + (first.Rotation == 0 ? "" : $"r{first.Rotation}"));
            i = end + 1;
        }
        return string.Join(",", parts);

        static bool Alike(Page a, Page b) => a.Source == b.Source && a.Rotation == b.Rotation;
    }
}

/// <summary>
/// Puts a PDF's pages in a new order, turned, copied or left out, and can take pages from other PDFs too. Pages are
/// copied as they are with PDFsharp. Bookmarks are rebuilt: each one whose page is kept opens that page's first copy,
/// and those for deleted pages go, their sub-bookmarks moving up a level.
/// </summary>
public sealed class OrganizePdfTool : IConverter
{
    static readonly Target Entry = new Target("organize-pdf", Strings.OrganizePages, Strings.ReorderTurnOrDelete, Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = Strings.SavingThePages,
        Done = Strings.PagesOrganized,
        Confirm = Strings.Save,
        Editor = EditorKind.OrganizePdf,
    }.WithOptions(
        new Option("pages", Strings.Pages, OptionKind.Text, "")
        {
            Hint = Strings.InTheirNewOrderSuchAs3 +
                Strings.AndAPageLeftOutIsDeleted,
        },
        new Option("with", Strings.PagesFrom, OptionKind.Text, "") { Optional = true, Hint = Strings.OtherPDFsToTakePagesFromSeparated });

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? [Entry] : [];

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var others = Others(target.Text("with"), input);
        var sources = new List<PdfDocument>();
        try
        {
            foreach (var path in others.Prepend(input))
                sources.Add(PdfFiles.OpenForImport(path));
            var pages = PageList.Parse(target.Text("pages"), sources.Select(s => s.PageCount).ToList(),
                others.Prepend(input).Select(Path.GetFileName).ToList()!);

            var main = sources[0];
            using var output = PdfFiles.NewLike(main);
            foreach (var source in sources.Skip(1)) output.Version = Math.Max(output.Version, source.Version);
            // Where each of the organized PDF's pages went first, for its bookmarks.
            var firstCopy = new Dictionary<int, PdfPage>();
            for (var i = 0; i < pages.Count; i++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                var (source, index, rotation) = pages[i];
                var original = sources[source].Pages[index];
                var copy = output.AddPage(original);
                copy.Rotate = (Rotation(original) + rotation) % 360;
                if (source == 0) firstCopy.TryAdd(index, copy);
                ctx.Progress.Report((i + 1.0) / (pages.Count + 1));
            }
            Bookmarks.Write(output.Outlines, Bookmarks.Read(main), page => firstCopy.GetValueOrDefault(page));
            if (Losses(main) is { } losses) ctx.Note(losses);

            var outputPath = OutputPaths.Suffixed(input, "organized", "pdf");
            PdfFiles.Save(output, outputPath);
            ctx.Progress.Report(1);
            return [outputPath];
        }
        finally
        {
            foreach (var source in sources) source.Dispose();
        }
    }, ctx.Cancellation);

    /// <summary>The other PDFs to take pages from, "a.pdf|b.pdf", found beside the organized one when not given in full.</summary>
    static IReadOnlyList<string> Others(string list, string input)
    {
        var paths = list.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Path.GetFullPath(p, OutputPaths.DirectoryOf(input)))
            .ToList();
        if (paths.FirstOrDefault(p => !File.Exists(p)) is { } missing)
            throw new ConversionException(string.Format(Strings.CouldnTFindToTakePagesFrom, missing));
        return paths;
    }

    static int Rotation(PdfPage page) => ((page.Elements.GetInteger("/Rotate") % 360) + 360) % 360;

    /// <summary>What a reorganized copy of this PDF can't keep working, to say before saving; null when nothing.</summary>
    public static string? Losses(string path)
    {
        using var document = PdfFiles.OpenForImport(path);
        return Losses(document);
    }

    static string? Losses(PdfDocument document)
    {
        var form = document.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is { Elements.Count: > 0 };
        var links = document.Pages.Cast<PdfPage>().Any(page =>
            page.Elements.GetArray("/Annots")?.Elements.Select(Resolve).OfType<PdfDictionary>().Any(IsInternalLink) == true);
        return (form, links) switch
        {
            (true, true) => Strings.ThisPDFSFormFieldsAndIts,
            (true, false) => Strings.ThisPDFSFormFieldsWonT,
            (false, true) => Strings.ThisPDFSLinksBetweenPagesWon,
            _ => null,
        };
    }

    static bool IsInternalLink(PdfDictionary annotation) =>
        annotation.Elements.GetName("/Subtype") == "/Link"
        && (annotation.Elements.ContainsKey("/Dest") || annotation.Elements.GetDictionary("/A")?.Elements.GetName("/S") == "/GoTo");

    internal static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    /// <summary>A PDF's bookmarks as a tree, each with the 0-based page it opens, if any.</summary>
    internal sealed record Bookmark(string Title, int? Page, bool Open, IReadOnlyList<Bookmark> Children);

    internal static class Bookmarks
    {
        public static IReadOnlyList<Bookmark> Read(PdfDocument document)
        {
            var catalog = document.Internals.Catalog;
            if (catalog.Elements.GetDictionary("/Outlines") is not { } outlines) return [];
            var pages = new Dictionary<PdfObjectID, int>();
            for (var i = 0; i < document.PageCount; i++)
                pages.TryAdd(PdfInternals.GetObjectID(document.Pages[i]), i);
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            return Children(outlines, 0);

            List<Bookmark> Children(PdfDictionary parent, int depth)
            {
                var list = new List<Bookmark>();
                // Broken files can loop; a sane outline is never this deep.
                for (var item = parent.Elements.GetDictionary("/First"); item is not null && depth < 64 && seen.Add(item); item = item.Elements.GetDictionary("/Next"))
                {
                    var title = (Resolve(item.Elements["/Title"]) as PdfString)?.Value ?? "";
                    var open = item.Elements.GetInteger("/Count") > 0;
                    list.Add(new Bookmark(title, PageOf(item), open, Children(item, depth + 1)));
                }
                return list;
            }

            int? PageOf(PdfDictionary item)
            {
                var destination = item.Elements["/Dest"];
                if (destination is null && item.Elements.GetDictionary("/A") is { } action && action.Elements.GetName("/S") == "/GoTo")
                    destination = action.Elements["/D"];
                return Page(destination, 0);
            }

            int? Page(PdfItem? destination, int depth)
            {
                if (depth > 4) return null;
                switch (destination is PdfReference reference ? reference.Value : destination)
                {
                    case PdfArray { Elements.Count: > 0 } array:
                        return array.Elements[0] is PdfReference target && pages.TryGetValue(target.ObjectID, out var page) ? page : null;
                    case PdfDictionary { } entry:
                        return Page(entry.Elements["/D"], depth + 1);
                    case PdfString name:
                        return Page(Named(name.Value), depth + 1);
                    case PdfName name:
                        return Page(Named(name.Value.TrimStart('/')), depth + 1);
                    default:
                        return null;
                }
            }

            // Named destinations: in the /Dests name tree, or the older /Dests dictionary.
            PdfItem? Named(string name)
            {
                if (catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests") is { } tree && Find(tree, name, 0) is { } found)
                    return found;
                return catalog.Elements.GetDictionary("/Dests")?.Elements["/" + name];
            }

            static PdfItem? Find(PdfDictionary node, string name, int depth)
            {
                if (depth > 32) return null;
                if (node.Elements.GetArray("/Names") is { } names)
                    for (var i = 0; i + 1 < names.Elements.Count; i += 2)
                        if (Resolve(names.Elements[i]) is PdfString key && key.Value == name)
                            return names.Elements[i + 1];
                foreach (var kid in node.Elements.GetArray("/Kids")?.Elements.Select(Resolve).OfType<PdfDictionary>() ?? [])
                    if (Find(kid, name, depth + 1) is { } found)
                        return found;
                return null;
            }
        }

        /// <summary>Adds the bookmarks whose pages are kept; a bookmark without one makes way for its sub-bookmarks.</summary>
        public static void Write(PdfOutlineCollection into, IReadOnlyList<Bookmark> bookmarks, Func<int, PdfPage?> copyOf)
        {
            foreach (var bookmark in bookmarks)
            {
                if (bookmark.Page is { } page && copyOf(page) is { } copy)
                    Write(into.Add(bookmark.Title, copy, bookmark.Open).Outlines, bookmark.Children, copyOf);
                else
                    Write(into, bookmark.Children, copyOf);
            }
        }
    }
}

/// <summary>A PDF's pages as small pictures for the page editor, drawn by Windows' PDF engine one at a time as they're asked for.</summary>
public sealed class PdfThumbnails
{
    readonly Windows.Data.Pdf.PdfDocument _document;
    readonly SemaphoreSlim _one = new(1, 1);

    PdfThumbnails(Windows.Data.Pdf.PdfDocument document) => _document = document;

    public static async Task<PdfThumbnails> OpenAsync(string path) => new(await PdfConverter.LoadAsync(path));

    public int Count => (int)_document.PageCount;

    /// <summary>A page's width and height as it shows, turned by its own rotation; only the shape matters.</summary>
    public (double Width, double Height) Size(int index)
    {
        using var page = _document.GetPage((uint)index);
        return (page.Size.Width, page.Size.Height);
    }

    /// <summary>A page as a PNG whose longer side is <paramref name="longest"/> pixels.</summary>
    public async Task<byte[]> RenderAsync(int index, int longest, CancellationToken cancel)
    {
        await _one.WaitAsync(cancel);
        try
        {
            return (await PdfConverter.RenderPagePngAsync(_document, index, 300, (uint)longest)).Png;
        }
        finally
        {
            _one.Release();
        }
    }
}

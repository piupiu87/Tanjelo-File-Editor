using ImageMagick;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Tests;

/// <summary>Split… into parts, and Save pictures.</summary>
public class SplitPartsTests
{
    static async Task<BatchResult> Split(string pdf, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "split-pdf-parts", [pdf]).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        var result = await engine.RunAsync([pdf], target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("\n", result.Failures.Select(f => f.Message)));
        return result;
    }

    /// <summary>Each part's file name, and the text of its pages.</summary>
    static List<(string Name, string[] Pages)> Parts(string folder) =>
        Directory.GetFiles(folder).Order(StringComparer.Ordinal).Select(file =>
        {
            using var pdf = PigDocument.Open(file);
            return (Path.GetFileName(file), pdf.GetPages().Select(p => p.Text).ToArray());
        }).ToList();

    static string Book(Scratch scratch) => scratch.BookmarkedPdf("report.pdf", ("Introduction", 2), ("Methods", 3), ("Results", 1));

    [Fact]
    public async Task Every_n_pages_makes_parts_of_that_many_in_a_folder()
    {
        using var scratch = new Scratch();

        var folder = Assert.Single((await Split(Book(scratch), ("every", "4"))).Outputs);

        Assert.Equal(scratch.PathOf("report parts"), folder);
        Assert.Equal([("report-1.pdf", 4), ("report-2.pdf", 2)], Parts(folder).Select(p => (p.Name, p.Pages.Length)));
        Assert.Equal("Methods page 3", Parts(folder)[1].Pages[0]);
    }

    [Fact]
    public async Task At_bookmarks_each_part_is_named_after_its_bookmark_and_keeps_the_ones_inside()
    {
        using var scratch = new Scratch();

        var folder = Assert.Single((await Split(Book(scratch), ("by", "bookmarks"))).Outputs);

        Assert.Equal([("report-01 Introduction.pdf", 2), ("report-02 Methods.pdf", 3), ("report-03 Results.pdf", 1)],
            Parts(folder).Select(p => (p.Name, p.Pages.Length)));
        using var methods = PigDocument.Open(Path.Combine(folder, "report-02 Methods.pdf"));
        Assert.True(methods.TryGetBookmarks(out var bookmarks));
        var top = Assert.Single(bookmarks.Roots);
        Assert.Equal("Methods", top.Title);
        Assert.Equal("Methods details", Assert.Single(top.Children).Title);
    }

    [Fact]
    public async Task At_listed_pages_each_range_is_a_part_and_a_page_it_hasnt_is_caught()
    {
        using var scratch = new Scratch();
        var pdf = Book(scratch);

        var folder = Assert.Single((await Split(pdf, ("by", "list"), ("list", "1-3, 4-5, 6-"))).Outputs);

        Assert.Equal([3, 2, 1], Parts(folder).Select(p => p.Pages.Length));
        var engine = Env.Engine();
        var target = Env.Offered(engine, "split-pdf-parts", [pdf]);
        Task<SettingsProblem?> Check(params (string, string)[] values) =>
            engine.ValidateAsync([pdf], target.WithParameters(values.ToDictionary(v => v.Item1, v => v.Item2)), TestContext.Current.CancellationToken);
        Assert.Equal(new SettingsProblem("list", "There's no page 9: report.pdf has 6 pages."), await Check(("by", "list"), ("list", "1-3,7-9")));
        Assert.Equal(new SettingsProblem("by", "This PDF has no bookmarks to split at."),
            await engine.ValidateAsync([scratch.Pdf("plain.pdf", pages: 2)], Env.Offered(engine, "split-pdf-parts", [scratch.PathOf("plain.pdf")])
                .WithParameters(new Dictionary<string, string> { ["by"] = "bookmarks" }), TestContext.Current.CancellationToken));
        Assert.Null(await Check(("by", "list"), ("list", "1-3,4-")));
    }

    /// <summary>Pages that are each a noisy photo of about 150 KB.</summary>
    static string Photos(Scratch scratch, int pages)
    {
        var path = scratch.PathOf("photos.pdf");
        using var collection = new MagickImageCollection();
        for (var i = 0; i < pages; i++)
        {
            var page = new MagickImage("plasma:orange-steelblue", 640, 480) { Density = new Density(96, 96, DensityUnit.PixelsPerInch) };
            page.AddNoise(NoiseType.Gaussian);
            page.Settings.Compression = CompressionMethod.JPEG;
            page.Quality = 92;
            collection.Add(page);
        }
        collection.Write(path, MagickFormat.Pdf);
        return path;
    }

    [Fact]
    public async Task Under_a_size_parts_are_filled_up_to_it_and_a_bigger_page_goes_alone()
    {
        using var scratch = new Scratch();
        var pdf = Photos(scratch, 7);
        var page = new FileInfo(pdf).Length / 7.0;

        var limit = page * 2.6 / 1024 / 1024; // room for two pages and a bit, not three
        var folder = Assert.Single((await Split(pdf, ("by", "size"), ("size", limit.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))).Outputs);

        var parts = Directory.GetFiles(folder).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([2, 2, 2, 1], parts.Select(Inspect.PdfPages));
        Assert.All(parts, part => Assert.True(new FileInfo(part).Length <= limit * 1024 * 1024));

        var small = await Split(pdf, ("by", "size"), ("size", (page * 0.5 / 1024 / 1024).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(7, Directory.GetFiles(Assert.Single(small.Outputs)).Length);
        Assert.StartsWith("Pages 1, 2, 3, 4, 5, 6, 7 are bigger than ", Assert.Single(small.Notes));
    }

    [Fact]
    public async Task The_preview_lists_the_parts()
    {
        using var scratch = new Scratch();
        var pdf = Book(scratch);
        var engine = Env.Engine();
        var target = Env.Offered(engine, "split-pdf-parts", [pdf]).WithParameters(new Dictionary<string, string> { ["by"] = "bookmarks" });

        var preview = await engine.PreviewAsync([pdf], target, new PreviewRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("3 parts: Introduction (1–2), Methods (3–5), Results (6)", preview!.Facts[1].Value);
    }

    [Fact]
    public async Task Save_pictures_writes_each_picture_once_in_a_file_of_its_kind()
    {
        using var scratch = new Scratch();
        var pdf = scratch.PicturesPdf("report.pdf");

        var folder = await Env.RunOne("save-pictures", pdf);

        Assert.Equal(scratch.PathOf("report pictures"), folder);
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.All(files, f => Assert.Matches(@"^report-p0[12]-\d\.(jpg|png|jp2)$", f));
        var pictures = files.Select(f => (Name: f!, Info: f!.EndsWith(".jp2") ? null : new MagickImageInfo(Path.Combine(folder, f)))).ToList();
        // The JPEG shown on both pages is saved once, from page 1, as it was.
        var jpegs = pictures.Where(p => p.Name.EndsWith(".jpg")).ToList();
        Assert.Equal([1200, 560], jpegs.Select(p => (int)p.Info!.Width).Order().Reverse());
        Assert.Equal(ColorSpace.sRGB, jpegs.Single(p => p.Info!.Width == 560).Info!.ColorSpace); // the CMYK one, turned RGB
        // The lossless photo, the screenshot, the masked picture and the 1-bit scan as PNG; no tiny picture or stencil.
        var pngs = pictures.Where(p => p.Name.EndsWith(".png")).Select(p => (int)p.Info!.Width).Order();
        Assert.Equal([200, 400, 408, 640], pngs);
        using (var masked = new MagickImage(Path.Combine(folder, pictures.Single(p => p.Info?.Width == 200).Name)))
        {
            Assert.True(masked.HasAlpha);
            Assert.Equal(0, masked.GetPixels().GetPixel(2, 2).ToColor()!.A); // a corner, outside the disc
            Assert.Equal(255, masked.GetPixels().GetPixel(100, 100).ToColor()!.A);
        }
        Assert.Equal(pictures.Any(p => p.Name.EndsWith(".jp2")), Directory.GetFiles(folder, "*.jp2").Length == 1);
    }

    [Fact]
    public async Task A_pdf_without_pictures_says_drawings_arent_pictures()
    {
        using var scratch = new Scratch();
        var pdf = Book(scratch);
        var engine = Env.Engine();

        var result = await engine.RunAsync([pdf], Env.Offered(engine, "save-pictures", [pdf]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("This PDF has no pictures. Drawings aren't pictures: PDF → PNG saves whole pages.", Assert.Single(result.Failures).Message);
        Assert.Equal(["report.pdf"], scratch.Files());
    }
}

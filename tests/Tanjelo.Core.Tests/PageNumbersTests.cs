using System.Globalization;
using ImageMagick;
using UglyToad.PdfPig.Content;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Tests;

/// <summary>Page numbers…: lines of text at the top and bottom of each page, where the page is seen, and Bates numbers.</summary>
public class PageNumbersTests
{
    const double Margin = 12 / 25.4 * 72; // the default 12 mm
    const double Size = 10;
    // Arial reaches 0.905 of its size above the baseline and 0.212 below.
    const double Ascent = 0.905 * Size, Descent = 0.212 * Size;

    static async Task<BatchResult> Number(string[] pdfs, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "page-numbers", pdfs).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        var result = await engine.RunAsync(pdfs, target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("\n", result.Failures.Select(f => f.Message)));
        return result;
    }

    /// <summary>The stamp's letters on a page, which are Arial or a fallback; the fixtures' own text is Helvetica.</summary>
    static List<Letter> Stamp(Page page) => page.Letters.Where(l => l.FontName?.Contains("Helvetica", StringComparison.Ordinal) != true).ToList();

    /// <summary>Where a stamp's text sits on the page as seen: the middle of its baseline, and its size.</summary>
    static (string Text, double Middle, double Baseline, double Size) Seen(Page page)
    {
        var letters = Stamp(page);
        Assert.NotEmpty(letters);
        return (string.Concat(letters.Select(l => l.Value)), (letters[0].StartBaseLine.X + letters[^1].EndBaseLine.X) / 2,
            letters[0].StartBaseLine.Y, letters[0].PointSize);
    }

    [Fact]
    public async Task The_default_numbers_each_page_at_the_bottom_in_the_middle()
    {
        using var scratch = new Scratch();
        var pdf = scratch.BookmarkedPdf("report.pdf", ("Introduction", 2), ("Methods", 1));

        var output = Assert.Single((await Number([pdf])).Outputs);

        Assert.Equal(scratch.PathOf("report-numbered.pdf"), output);
        using var numbered = PigDocument.Open(output);
        for (var n = 1; n <= 3; n++)
        {
            var (text, middle, baseline, size) = Seen(numbered.GetPage(n));
            Assert.Equal(n.ToString(CultureInfo.CurrentCulture), text);
            Assert.Equal(306, middle, 1);
            Assert.Equal(Margin + Descent, baseline, 1);
            Assert.Equal(Size, size, 1);
        }
        Assert.Contains("Introduction page 1", numbered.GetPage(1).Text); // what was there stays
        Assert.True(numbered.TryGetBookmarks(out var bookmarks));
        Assert.Equal(2, bookmarks.Roots.Count);
    }

    [Theory]
    [InlineData(0, 612, 792)]
    [InlineData(90, 792, 612)]
    [InlineData(180, 612, 792)]
    [InlineData(270, 792, 612)]
    public async Task Turned_pages_get_their_numbers_where_the_page_is_seen(int rotation, int width, int height)
    {
        using var scratch = new Scratch();
        var output = Assert.Single((await Number([scratch.RotatedPdf("turned.pdf", rotation)], ("top", "Top {page}"), ("top-place", "right"))).Outputs);

        // PdfPig gives positions as the page shows, turned.
        using (var numbered = PigDocument.Open(output))
        {
            var page = numbered.GetPage(1);
            var bottom = Stamp(page).Where(l => l.StartBaseLine.Y < height / 2).ToList();
            var top = Stamp(page).Where(l => l.StartBaseLine.Y > height / 2).ToList();
            Assert.Equal("1", string.Concat(bottom.Select(l => l.Value)));
            Assert.Equal(width / 2.0, (bottom[0].StartBaseLine.X + bottom[^1].EndBaseLine.X) / 2, 1);
            Assert.Equal(Margin + Descent, bottom[0].StartBaseLine.Y, 1);
            Assert.Equal("Top 1", string.Concat(top.Select(l => l.Value)));
            Assert.Equal(width - Margin, top[^1].EndBaseLine.X, 1);
            Assert.Equal(height - Margin - Ascent, top[0].StartBaseLine.Y, 1);
        }
        // Windows draws the number at the bottom in the middle, and nothing at the top in the middle.
        var document = await Converters.PdfConverter.LoadAsync(output);
        using var picture = new MagickImage((await Converters.PdfConverter.RenderPagePngAsync(document, 0, 72, 2000)).Png);
        Assert.Equal((width, height), ((int)picture.Width, (int)picture.Height));
        Assert.True(Darkest(picture, width / 2 - 10, height - 45, 20, 20) < 128, "no number at the bottom");
        Assert.True(Darkest(picture, width / 2 - 10, 25, 20, 20) > 200, "something at the top in the middle");
    }

    static double Darkest(MagickImage picture, int x, int y, int width, int height)
    {
        using var area = picture.Clone();
        area.Crop(new MagickGeometry(x, y, (uint)width, (uint)height));
        area.Grayscale();
        return area.Statistics().GetChannel(PixelChannel.Gray)!.Minimum / (Quantum.Max / 255.0);
    }

    [Fact]
    public async Task Crop_boxes_moved_media_boxes_and_user_units_are_allowed_for()
    {
        using var scratch = new Scratch();
        var output = Assert.Single((await Number([scratch.BoxesPdf("boxes.pdf")])).Outputs);

        // PdfPig gives positions from the visible box's corner: the middle of its width, the margin above its bottom.
        using (var numbered = PigDocument.Open(output))
        {
            var cropped = Seen(numbered.GetPage(1));
            Assert.Equal((200, Margin + Descent), (Math.Round(cropped.Middle), Math.Round(cropped.Baseline, 1)), Near);
            var moved = Seen(numbered.GetPage(2));
            Assert.Equal((306, Margin + Descent), (Math.Round(moved.Middle), Math.Round(moved.Baseline, 1)), Near);
            // A unit is two points there, so everything is half as many units.
            var scaled = Seen(numbered.GetPage(3));
            Assert.Equal((153, (Margin + Descent) / 2), (Math.Round(scaled.Middle), Math.Round(scaled.Baseline, 1)), Near);
            Assert.Equal(Size / 2, scaled.Size, 1);
        }
        // Windows shows the visible box, with the number at the bottom in the middle.
        var document = await Converters.PdfConverter.LoadAsync(output);
        foreach (var (page, width, height) in new[] { (0, 400, 550), (1, 612, 792) })
        {
            using var picture = new MagickImage((await Converters.PdfConverter.RenderPagePngAsync(document, page, 72, 2000)).Png);
            Assert.Equal((width, height), ((int)picture.Width, (int)picture.Height));
            Assert.True(Darkest(picture, width / 2 - 10, height - 45, 20, 20) < 128, $"no number at the bottom of page {page + 1}");
        }
    }

    static readonly IEqualityComparer<(double, double)> Near = EqualityComparer<(double, double)>.Create(
        (a, b) => Math.Abs(a.Item1 - b.Item1) <= 1 && Math.Abs(a.Item2 - b.Item2) <= 1, _ => 0);

    [Fact]
    public async Task Content_that_leaves_the_graphics_state_changed_doesnt_move_the_numbers()
    {
        using var scratch = new Scratch();
        var output = Assert.Single((await Number([scratch.ChangedStatePdf("state.pdf")])).Outputs);

        using var numbered = PigDocument.Open(output);
        foreach (var n in new[] { 1, 2 })
        {
            var (text, middle, baseline, size) = Seen(numbered.GetPage(n));
            Assert.Equal(n.ToString(CultureInfo.CurrentCulture), text);
            Assert.Equal((306, Margin + Descent, Size), (Math.Round(middle), Math.Round(baseline, 1), Math.Round(size, 1)), (a, b) =>
                Math.Abs(a.Item1 - b.Item1) <= 1 && Math.Abs(a.Item2 - b.Item2) <= 1 && Math.Abs(a.Item3 - b.Item3) < 0.1);
        }
        Assert.Contains("Left halved", numbered.GetPage(1).Text);
    }

    [Fact]
    public async Task Bates_numbers_carry_on_across_the_pdfs_in_the_order_selected()
    {
        using var scratch = new Scratch();
        var pdfs = new[] { scratch.Pdf("b.pdf", pages: 2), scratch.Pdf("a.pdf", pages: 3), scratch.Pdf("c.pdf", pages: 1) };

        var result = await Number(pdfs, ("bottom", "{bates}"), ("bottom-place", "right"), ("bates-prefix", "ABC"));

        Assert.Equal(["b-numbered.pdf", "a-numbered.pdf", "c-numbered.pdf"], result.Outputs.Select(Path.GetFileName));
        Assert.Equal(["b.pdf: ABC000001–ABC000002", "a.pdf: ABC000003–ABC000005", "c.pdf: ABC000006–ABC000006"], result.Notes);
        using var middle = PigDocument.Open(result.Outputs[1]);
        Assert.Equal(["ABC000003", "ABC000004", "ABC000005"], middle.GetPages().Select(p => p.Text));
        Assert.Equal(612 - Margin, Stamp(middle.GetPage(1))[^1].EndBaseLine.X, 1);
    }

    [Fact]
    public async Task A_cover_can_be_left_out_and_tokens_are_filled_in()
    {
        using var scratch = new Scratch();
        var pdf = scratch.Pdf("report.pdf", pages: 3);

        var output = Assert.Single((await Number([pdf], ("skip", "1"), ("bottom", "Page {page} of {pages}"), ("top", "{file}, {date}"), ("top-place", "left"))).Outputs);

        using var numbered = PigDocument.Open(output);
        Assert.Empty(numbered.GetPage(1).Letters);
        var date = DateTime.Now.ToString("d", CultureInfo.CurrentCulture);
        Assert.Equal($"report, {date}Page 1 of 2".Replace(" ", ""), numbered.GetPage(2).Text.Replace(" ", ""));
        Assert.Contains("Page2of2", numbered.GetPage(3).Text.Replace(" ", ""));
    }

    [Theory]
    [InlineData("第 {page} 页", "第 1 页", "YaHei")]
    [InlineData("페이지 {page}", "페이지 1", "Malgun")]
    public async Task Letters_Arial_lacks_take_a_font_that_has_them(string line, string expected, string font)
    {
        Assert.SkipUnless(new System.Windows.Media.Typeface(font == "YaHei" ? "Microsoft YaHei" : "Malgun Gothic").TryGetGlyphTypeface(out _), $"{font} isn't installed");
        using var scratch = new Scratch();

        var output = Assert.Single((await Number([scratch.Pdf("report.pdf")], ("bottom", line))).Outputs);

        using var numbered = PigDocument.Open(output);
        var page = numbered.GetPage(1);
        Assert.Equal(expected.Replace(" ", ""), page.Text.Replace(" ", ""));
        Assert.All(page.Letters.Where(l => l.Value.Trim().Length > 0 && !char.IsAsciiDigit(l.Value[0])), l => Assert.Contains(font, l.FontName));
    }

    [Fact]
    public async Task The_preview_shows_the_first_numbered_page_and_the_bates_range()
    {
        using var scratch = new Scratch();
        var pdfs = new[] { scratch.Pdf("a.pdf", pages: 2), scratch.Pdf("b.pdf", pages: 3) };
        var engine = Env.Engine();
        var target = Env.Offered(engine, "page-numbers", pdfs)
            .WithParameters(new Dictionary<string, string> { ["bottom"] = "{bates}", ["top"] = "Page {page} of {pages}", ["bates-prefix"] = "X-" });

        var preview = await engine.PreviewAsync(pdfs, target, new PreviewRequest(400), TestContext.Current.CancellationToken);

        Assert.Equal([new Fact("First numbered page", "Page 1 of 2 · X-000001"), new Fact("Bates", "X-000001–X-000005")], preview!.Facts);
        using var picture = new MagickImage(preview.Image!);
        Assert.Equal(400u, picture.Height);
        Assert.Equal(["a.pdf", "b.pdf"], scratch.Files());
        Assert.Equal("bottom", (await engine.ValidateAsync(pdfs, target.WithParameters(new Dictionary<string, string> { ["bottom"] = "", ["top"] = "" }),
            TestContext.Current.CancellationToken))!.Option);
    }
}

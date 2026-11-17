using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Tests;

/// <summary>Compress… for PDFs: the pictures inside scaled down and re-encoded, everything else kept.</summary>
public class CompressPdfTests
{
    /// <summary>A picture as written in a PDF.</summary>
    sealed record Picture(int Width, int Height, string Filter, string ColourSpace, bool SoftMask, int Bits, int Bytes, string Decode);

    static List<Picture> Pictures(string path)
    {
        using var document = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return document.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Elements.GetName("/Subtype") == "/Image")
            .Select(d => new Picture(d.Elements.GetInteger("/Width"), d.Elements.GetInteger("/Height"), d.Elements["/Filter"]?.ToString() ?? "",
                d.Elements["/ColorSpace"]?.ToString() ?? "", d.Elements.ContainsKey("/SMask"), d.Elements.GetInteger("/BitsPerComponent"),
                d.Stream.Value.Length, d.Elements["/Decode"]?.ToString() ?? ""))
            .ToList();
    }

    static async Task<BatchResult> Compress(string pdf, string quality = "medium")
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "compress", [pdf]).WithParameters(new Dictionary<string, string> { ["quality"] = quality });
        return await engine.RunAsync([pdf], target, ct: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Each_kind_of_picture_is_made_smaller_or_left_as_it_is()
    {
        using var scratch = new Scratch();
        var pdf = scratch.PicturesPdf("pictures.pdf");
        var before = Pictures(pdf);

        var result = await Compress(pdf);

        var output = Assert.Single(result.Outputs);
        Assert.Equal(scratch.PathOf("pictures-compressed.pdf"), output);
        var after = Pictures(output);
        Picture Only(int width) => Assert.Single(after, p => p.Width == width);
        // The JPEG is drawn three times, the largest 240 points wide: 3⅓ inches at 150 pixels an inch. One copy is kept.
        Assert.Equal((500, 375, "/DCTDecode"), (Only(500).Width, Only(500).Height, Only(500).Filter));
        Assert.DoesNotContain(after, p => p.Width == 1200);
        // A lossless photo drawn 200 by 150 points becomes a JPEG (416.7 by 312.5 pixels), and so does the CMYK JPEG, kept in CMYK and inverted.
        Assert.True(2 == after.Count(p => p is { Width: 417, Height: 312, Filter: "/DCTDecode" }), string.Join(" | ", after));
        Assert.Contains(after, p => p is { Width: 417, ColourSpace: "/DeviceCMYK" } && p.Decode.Replace(" ", "") == "[10101010]");
        // Left as they are: the screenshot, the tiny picture, the masked one and its mask, the 1-bit scan, the stencil and JPEG 2000.
        Assert.Equal("/FlateDecode", Only(400).Filter);
        Assert.Equal("/FlateDecode", Only(10).Filter);
        Assert.Contains(after, p => p is { Width: 200, SoftMask: true, Filter: "/FlateDecode" });
        Assert.Equal(1, Only(408).Bits);
        Assert.Equal(1, Only(96).Bits);
        if (before.Any(p => p.Filter == "/JPXDecode")) Assert.Equal("/JPXDecode", Only(300).Filter);
        Assert.Equal(before.Count, after.Count); // each changed in place, the shared JPEG once

        // Nothing got bigger, and the whole file is much smaller.
        foreach (var picture in after.Where(p => before.Any(b => b.Width == p.Width && b.Filter == p.Filter)))
            Assert.True(picture.Bytes <= before.First(b => b.Width == picture.Width && b.Filter == picture.Filter).Bytes, $"{picture} grew");
        Assert.True(new FileInfo(output).Length < new FileInfo(pdf).Length / 2, $"{new FileInfo(output).Length} of {new FileInfo(pdf).Length}");
        Assert.Matches(@"^\d+(\.\d)? [KM]B → \d+(\.\d)? [KM]B, 3 of [89] pictures made smaller$", Assert.Single(result.Notes));
    }

    [Fact]
    public async Task Text_bookmarks_and_the_look_of_the_pages_survive()
    {
        using var scratch = new Scratch();
        var pdf = scratch.PicturesPdf("pictures.pdf");

        var output = Assert.Single((await Compress(pdf)).Outputs);

        using (var compressed = PigDocument.Open(output))
        {
            Assert.Contains("Pictures page one", compressed.GetPage(1).Text);
            Assert.Contains("Pictures page two", compressed.GetPage(2).Text);
            Assert.True(compressed.TryGetBookmarks(out var bookmarks));
            Assert.Equal("Second page", Assert.Single(bookmarks.Roots).Title);
        }
        // Rendered by Windows, each page looks as it did, CMYK picture and all.
        for (var page = 0; page < 2; page++)
        {
            using var was = new MagickImage(await Render(pdf, page));
            using var now = new MagickImage(await Render(output, page));
            var difference = was.Compare(now, ErrorMetric.MeanAbsolute);
            Assert.True(difference < 0.03, $"page {page + 1} differs by {difference:P1}");
        }
    }

    static async Task<byte[]> Render(string pdf, int page)
    {
        var document = await Converters.PdfConverter.LoadAsync(pdf);
        return (await Converters.PdfConverter.RenderPagePngAsync(document, page, 50, 1000)).Png;
    }

    [Fact]
    public async Task Lower_quality_makes_a_smaller_file()
    {
        using var scratch = new Scratch();
        var pdf = scratch.PicturesPdf("pictures.pdf");

        var high = Assert.Single((await Compress(pdf, "high")).Outputs);
        var low = Assert.Single((await Compress(pdf, "low")).Outputs);

        Assert.True(new FileInfo(low).Length < new FileInfo(high).Length);
        Assert.Contains(Pictures(high), p => p is { Width: 667, Filter: "/DCTDecode" }); // 200 pixels an inch
        Assert.Contains(Pictures(low), p => p is { Width: 333, Filter: "/DCTDecode" }); // 100
    }

    [Fact]
    public async Task A_pdf_already_as_small_as_it_gets_writes_nothing()
    {
        using var scratch = new Scratch();
        var once = Assert.Single((await Compress(scratch.PicturesPdf("pictures.pdf"))).Outputs);
        var text = scratch.BookmarkedPdf("text.pdf", ("Introduction", 2));

        foreach (var pdf in new[] { once, text })
        {
            var result = await Compress(pdf);
            Assert.Equal("Already as small as Tanjelo can make it.", Assert.Single(result.Failures).Message);
            Assert.Empty(result.Outputs);
        }
        Assert.Equal(["pictures-compressed.pdf", "pictures.pdf", "text.pdf"], scratch.Files());
    }

    [Fact]
    public async Task The_preview_counts_the_pictures_and_shows_the_largest_before_and_after()
    {
        using var scratch = new Scratch();
        var pdf = scratch.PicturesPdf("pictures.pdf");
        var engine = Env.Engine();

        var preview = await engine.PreviewAsync([pdf], Env.Offered(engine, "compress", [pdf]), new PreviewRequest(600), TestContext.Current.CancellationToken);

        Assert.Equal(["Now", "Pictures", "Largest"], preview!.Facts.Select(f => f.Label));
        Assert.Matches(@"^[89], \d+% of the file$", preview.Facts[1].Value); // a soft mask counts as part of its picture
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(preview.Facts[2].Value, @"^1200 × 900 · .+ → 500 × 375 · .+$"), preview.Facts[2].Value);
        using var picture = new MagickImage(preview.Image!);
        Assert.True(picture.Width is > 500 and <= 600, $"{picture.Width}");
        Assert.Equal(["pictures.pdf"], scratch.Files());
    }

    [Fact]
    public async Task Protected_and_signed_pdfs_say_so()
    {
        using var scratch = new Scratch();
        var locked = scratch.EncryptedPdf("locked.pdf", "aes128", "made-up-1");

        Assert.Equal("This PDF is password-protected. Remove its password first with Remove password…", Assert.Single((await Compress(locked)).Failures).Message);
        Assert.Contains("compress", Env.Keys(scratch.Pdf("plain.pdf")));
    }
}

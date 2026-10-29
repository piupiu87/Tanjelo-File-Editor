using ImageMagick;

namespace Tanjelo.Core.Converters;

/// <summary>
/// A PDF as a PowerPoint deck, one picture slide per page, written directly with the Open XML SDK. Each page's text
/// goes in its slide's speaker notes, so the deck can still be searched.
/// </summary>
public sealed class PdfSlidesConverter : IConverter
{
    static readonly Target Pptx = new("pptx", Strings.PPTX, Strings.OneSlidePerPage, Section.Document);

    // Sharp on a projector, but a poster-sized page still stays near 4K.
    const double Dpi = 200;
    const uint MaxSide = 3840;

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? [Pptx] : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var document = await PdfConverter.LoadAsync(input);
        var count = (int)document.PageCount;

        // Scans have no text for the notes, and aren't worth a second of OCR a page here.
        PdfText? text = null;
        try
        {
            text = await PdfText.ReadAsync(input, ctx, new ActionProgress<double>(f => ctx.Progress.Report(0.1 * f)), ocr: false);
        }
        catch (ConversionException)
        {
            // Windows can render some PDFs that PdfPig can't read; they just get no notes.
        }

        Windows.Foundation.Size first;
        using (var page = document.GetPage(0))
            first = page.Size;
        var output = OutputPaths.Beside(input, "pptx");
        try
        {
            // Page sizes are in DIPs, 1/96 inch, so the slides have the pages' own size.
            using var deck = new SlideDeck(output, (long)(first.Width * SlideDeck.EmuPerInch / 96), (long)(first.Height * SlideDeck.EmuPerInch / 96));
            for (var i = 0; i < count; i++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                var (png, width, height) = await PdfConverter.RenderPagePngAsync(document, i, Dpi, MaxSide);
                var (bytes, jpeg) = Pictures.Smaller(png);
                deck.Add(bytes, jpeg, width, height, $"Page {i + 1}", text?.PageText(i));
                ctx.Progress.Report(0.1 + 0.9 * (i + 1) / count);
            }
            deck.Save();
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        return [output];
    }
}

/// <summary>Several pictures become the slides of one PowerPoint deck, in the order selected.</summary>
public sealed class CombinePptxOperation : IBatchOperation
{
    // A widescreen slide is 13.333 inches wide; the deck takes the first picture's shape at that size.
    const long LongSide = 12192000;

    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        if (inputs.Count >= 2 && inputs.All(i => Formats.KindOf(i) == FileKind.Image))
            yield return new Target("combine-pptx", Strings.OnePPTX, string.Format(Strings.CombineImagesAsSlides, inputs.Count), Section.Document)
            {
                IsBatch = true,
                Running = Strings.CombiningIntoAPPTX,
                Done = Strings.CombinedIntoOnePPTX,
            };
    }

    public Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var output = OutputPaths.Unique(OutputPaths.DirectoryOf(inputs[0]), "Combined", "pptx");
        SlideDeck? deck = null;
        try
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                var (bytes, jpeg, width, height) = Pictures.ForSlide(inputs[i]);
                deck ??= width >= height
                    ? new SlideDeck(output, LongSide, LongSide * height / width)
                    : new SlideDeck(output, LongSide * width / height, LongSide);
                deck.Add(bytes, jpeg, width, height, Path.GetFileName(inputs[i]));
                ctx.Progress.Report((i + 1.0) / (inputs.Count + 1));
            }
            deck!.Save();
        }
        catch
        {
            deck?.Dispose();
            OutputPaths.TryDelete(output);
            throw;
        }
        return [output];
    }, ctx.Cancellation);
}

/// <summary>Pictures as a slide holds them: PNG or JPEG, upright, and at most about 4K.</summary>
static class Pictures
{
    const uint MaxSide = 3840;

    /// <summary>PNG keeps text sharp, but a page that is mostly photo is far smaller as a JPEG.</summary>
    public static (byte[] Bytes, bool Jpeg) Smaller(byte[] png)
    {
        if (png.Length < 1_500_000) return (png, false);
        using var image = new MagickImage(png);
        image.Quality = 90;
        var jpeg = image.ToByteArray(MagickFormat.Jpeg);
        return jpeg.Length < png.Length * 0.6 ? (jpeg, true) : (png, false);
    }

    /// <summary>A JPEG or PNG that's already upright and small enough goes in as it is; anything else is converted once.</summary>
    public static (byte[] Bytes, bool Jpeg, int Width, int Height) ForSlide(string path)
    {
        var key = Formats.Key(path);
        using var frames = ImageConverter.Read(path);
        var image = frames[0];
        var upright = image.Orientation is Orientation.Undefined or Orientation.TopLeft;
        if (key is "jpg" or "png" && upright && Math.Max(image.Width, image.Height) <= MaxSide)
            return (File.ReadAllBytes(path), key == "jpg", (int)image.Width, (int)image.Height);

        image.AutoOrient();
        if (Math.Max(image.Width, image.Height) > MaxSide)
            image.Resize(new MagickGeometry(MaxSide, MaxSide));
        // Photos stay photos; screenshots, drawings and anything see-through stay lossless.
        var jpeg = key is "jpg" or "heic" or "avif" or "webp" && !image.HasAlpha;
        if (jpeg) image.Quality = 90;
        return (image.ToByteArray(jpeg ? MagickFormat.Jpeg : MagickFormat.Png), jpeg, (int)image.Width, (int)image.Height);
    }
}

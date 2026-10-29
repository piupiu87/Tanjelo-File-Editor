using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Tanjelo.Core.Converters;

/// <summary>Renders PDF pages to images with the PDF engine built into Windows; no Ghostscript needed.</summary>
public sealed class PdfConverter : IConverter
{
    static readonly Target[] Outputs =
    [
        new("png", Strings.PNG, Strings.OneImagePerPage, Section.Image),
        new("jpg", Strings.JPG, Strings.OneImagePerPage, Section.Image),
    ];

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? Outputs : [];

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) =>
        RenderAsync(input, input, target.Key, ctx, ctx.Progress);

    /// <summary>
    /// Renders every page of <paramref name="pdf"/> as PNG or JPG. The images are named after <paramref name="namedAfter"/>
    /// and go beside it: one image for a one-page PDF, otherwise a "name pages" folder of numbered images.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> RenderAsync(string pdf, string namedAfter, string key, JobContext ctx, IProgress<double> progress)
    {
        var document = await LoadAsync(pdf);
        var count = (int)document.PageCount;
        var baseName = Formats.BaseName(namedAfter);
        var directory = OutputPaths.DirectoryOf(namedAfter);
        if (count > 1)
            directory = OutputPaths.Unique(directory, baseName + " pages", "");
        Directory.CreateDirectory(directory);

        var digits = count.ToString().Length;
        var scale = ctx.Options.PdfRenderDpi / 96.0; // page sizes are reported in DIPs (1/96 inch)
        var outputs = new List<string>();
        try
        {
            for (uint i = 0; i < count; i++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                using var page = document.GetPage(i);
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Round(page.Size.Width * scale),
                    DestinationHeight = (uint)Math.Round(page.Size.Height * scale),
                    BitmapEncoderId = key == "jpg" ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId,
                };
                var name = count == 1 ? baseName : $"{baseName}-{(i + 1).ToString().PadLeft(digits, '0')}";
                var output = OutputPaths.Unique(directory, name, key);

                using (var rendered = new InMemoryRandomAccessStream())
                {
                    await page.RenderToStreamAsync(rendered, options);
                    rendered.Seek(0);
                    await using var file = File.Create(output);
                    await rendered.AsStreamForRead().CopyToAsync(file, ctx.Cancellation);
                }
                outputs.Add(output);
                progress.Report((i + 1.0) / count);
            }
        }
        catch
        {
            if (count > 1) OutputPaths.TryDelete(directory);
            else outputs.ForEach(OutputPaths.TryDelete);
            throw;
        }
        return count > 1 ? [directory] : outputs;
    }

    /// <summary>Opens a PDF with Windows' PDF engine, with a password-protected file reported as such.</summary>
    internal static async Task<PdfDocument> LoadAsync(string pdf)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdf));
            return await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B))
        {
            throw new ConversionException(Strings.ThisPDFIsPasswordProtected, ex);
        }
    }

    /// <summary>One page as a bitmap at <paramref name="dpi"/>, shrunk if needed so neither side is longer than <paramref name="maxSide"/> pixels.</summary>
    internal static async Task<SoftwareBitmap> RenderPageAsync(PdfDocument document, int index, double dpi, uint maxSide = uint.MaxValue)
    {
        using var rendered = await RenderToStreamAsync(document, index, dpi, maxSide, BitmapEncoder.BmpEncoderId);
        var decoder = await BitmapDecoder.CreateAsync(rendered);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>One page as a PNG at <paramref name="dpi"/>, with neither side longer than <paramref name="maxSide"/> pixels.</summary>
    internal static async Task<(byte[] Png, int Width, int Height)> RenderPagePngAsync(PdfDocument document, int index, double dpi, uint maxSide)
    {
        using var rendered = await RenderToStreamAsync(document, index, dpi, maxSide, BitmapEncoder.PngEncoderId);
        var decoder = await BitmapDecoder.CreateAsync(rendered);
        rendered.Seek(0);
        using var bytes = new MemoryStream();
        await rendered.AsStreamForRead().CopyToAsync(bytes);
        return (bytes.ToArray(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
    }

    static async Task<InMemoryRandomAccessStream> RenderToStreamAsync(PdfDocument document, int index, double dpi, uint maxSide, Guid encoder)
    {
        using var page = document.GetPage((uint)index);
        var scale = Math.Min(dpi / 96.0, maxSide / Math.Max(page.Size.Width, page.Size.Height));
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
            BitmapEncoderId = encoder,
        };
        var rendered = new InMemoryRandomAccessStream();
        try
        {
            await page.RenderToStreamAsync(rendered, options);
        }
        catch
        {
            rendered.Dispose();
            throw;
        }
        rendered.Seek(0);
        return rendered;
    }
}

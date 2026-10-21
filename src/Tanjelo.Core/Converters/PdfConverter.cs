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
        new("png", "PNG", "One image per page", Section.Image),
        new("jpg", "JPG", "One image per page", Section.Image),
    ];

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? Outputs : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        PdfDocument document;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(input));
            document = await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B))
        {
            throw new ConversionException("This PDF is password-protected.", ex);
        }

        var count = (int)document.PageCount;
        var baseName = Formats.BaseName(input);
        var directory = OutputPaths.DirectoryOf(input);
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
                    BitmapEncoderId = target.Key == "jpg" ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId,
                };
                var name = count == 1 ? baseName : $"{baseName}-{(i + 1).ToString().PadLeft(digits, '0')}";
                var output = OutputPaths.Unique(directory, name, target.Key);

                using (var rendered = new InMemoryRandomAccessStream())
                {
                    await page.RenderToStreamAsync(rendered, options);
                    rendered.Seek(0);
                    await using var file = File.Create(output);
                    await rendered.AsStreamForRead().CopyToAsync(file, ctx.Cancellation);
                }
                outputs.Add(output);
                ctx.Progress.Report((i + 1.0) / count);
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
}

using System.Text;
using ImageMagick;
using Windows.Media.Ocr;

namespace Tanjelo.Core.Converters;

/// <summary>The text in a picture, such as a screenshot or a photo of a page, read with the OCR built into Windows.</summary>
public sealed class PictureTextConverter(Toolbox tools) : IConverter
{
    static readonly Target Txt = new("txt", "TXT", "The text in the picture, by OCR", Section.Document);

    public IEnumerable<Target> TargetsFor(string input) => tools.Ocr && Formats.KindOf(input) == FileKind.Image ? [Txt] : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        PdfText? text;
        using (var frames = ImageConverter.Read(input))
        {
            var image = frames[0];
            image.AutoOrient();
            var largest = OcrEngine.MaxImageDimension;
            if (Math.Max(image.Width, image.Height) > largest) image.Resize(new MagickGeometry(largest, largest));
            if (image.ColorSpace == ColorSpace.CMYK) image.TransformColorSpace(ColorProfiles.SRGB);
            if (image.HasAlpha)
            {
                // Text on a see-through background reads best on white.
                image.BackgroundColor = MagickColors.White;
                image.Alpha(AlphaAction.Remove);
            }
            using var bitmap = HeicEncoder.Bitmap(image);
            text = await PdfText.ReadPictureAsync(bitmap);
        }
        if (text is null)
            throw new ConversionException("Reading pictures needs an OCR language. " + PdfText.NoOcrHint[(PdfText.NoOcrHint.IndexOf('.') + 2)..]);
        if (text.Pages[0].Count == 0)
            throw new ConversionException("No text was found in this picture.");

        var output = OutputPaths.Beside(input, "txt");
        await File.WriteAllTextAsync(output, text.ToPlainText(pageBreaks: false), new UTF8Encoding(false), ctx.Cancellation);
        ctx.Progress.Report(1);
        return [output];
    }
}

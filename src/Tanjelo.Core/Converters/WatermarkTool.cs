using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Watermark…: text or a picture across the pages, diagonally, straight across the middle or tiled, at a share of
/// the page's width and an opacity, over the content or under it. A picture keeps its transparency. A watermark marks
/// a copy; it doesn't protect it.
/// </summary>
public sealed class WatermarkTool : IConverter, IPreviewer, IValidator
{
    static readonly Target Entry = new Target("watermark", Strings.Watermark, Strings.TextOrAPictureAcrossThePages, Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = Strings.AddingTheWatermark,
        Done = Strings.Watermarked,
        Confirm = Strings.AddWatermark,
    }.WithOptions(
        new Option("mark", Strings.MarkWith, OptionKind.Choice, "text")
        {
            Choices = [new("text", Strings.TextKind), new("picture", Strings.Picture)],
            Hint = Strings.AWatermarkIsAMark,
        },
        new Option("text", Strings.WatermarkText, OptionKind.Text, Strings.Confidential) { ShownWhen = new("mark", "text") },
        new Option("picture", Strings.Picture, OptionKind.File, "") { FileFilter = BackgroundTool.Pictures, ShownWhen = new("mark", "picture") },
        new Option("layout", Strings.Layout, OptionKind.Choice, "diagonal")
        {
            Choices = [new("diagonal", Strings.Diagonal), new("straight", Strings.StraightAcross), new("tiled", Strings.Tiled)],
        },
        new Option("size", Strings.Size, OptionKind.Number, "60") { Min = 5, Max = 100, Whole = true, Unit = "%", Slider = true, Hint = Strings.WatermarkSizeHint },
        new Option("opacity", Strings.Opacity, OptionKind.Number, "25") { Min = 5, Max = 100, Whole = true, Unit = "%", Slider = true },
        new Option("colour", Strings.Colour, OptionKind.Colour, "#808080") { ShownWhen = new("mark", "text") },
        new Option("pages", Strings.OnPages, OptionKind.Text, "") { Optional = true, Remember = false, Hint = Strings.OnPagesHint },
        new Option("under", Strings.UnderThePageContent, OptionKind.Toggle, "false") { Hint = Strings.UnderThePageContentHint });

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? [Entry] : [];

    public Task<SettingsProblem?> ValidateAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx) => Task.Run<SettingsProblem?>(() =>
    {
        int count;
        try
        {
            using var pdf = PigDocument.Open(inputs[0]);
            count = pdf.NumberOfPages;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // the run says what's wrong with the file
        }
        try
        {
            PageRanges.Parse(target.Text("pages"), count, Path.GetFileName(inputs[0]));
            return null;
        }
        catch (ConversionException ex)
        {
            return new SettingsProblem("pages", ex.Message);
        }
    }, ctx.Cancellation);

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var document = PdfFiles.OpenForChanges(input);
        var pages = PageRanges.Pages(target.Text("pages"), document.PageCount, Path.GetFileName(input));
        using var temp = OutputPaths.CreateTemp();
        using var mark = Mark.Make(document, target, temp.Path);
        var done = 0;
        foreach (var index in pages)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            Lay(document.Pages[index], mark, target);
            ctx.Progress.Report(0.9 * ++done / pages.Count);
        }
        PdfFiles.NoteIfSigned(document, ctx);
        var output = OutputPaths.Suffixed(input, "watermarked", "pdf");
        PdfFiles.Save(document, output);
        ctx.Progress.Report(1);
        return [output];
    }, ctx.Cancellation);

    /// <summary>The first page it goes on, watermarked, and which pages those are.</summary>
    public async Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx)
    {
        var (page, pages) = await Task.Run(() =>
        {
            using var source = PdfFiles.OpenForImport(inputs[0]);
            var pages = PageRanges.Pages(target.Text("pages"), source.PageCount, Path.GetFileName(inputs[0]));
            using var copy = PdfFiles.NewLike(source);
            copy.AddPage(source.Pages[pages.Min]);
            using var temp = OutputPaths.CreateTemp();
            using (var mark = Mark.Make(copy, target, temp.Path))
                Lay(copy.Pages[0], mark, target);
            using var saved = new MemoryStream();
            copy.Save(saved, closeStream: false);
            var which = pages.Count == source.PageCount ? string.Format(Strings.OnAllNPages, pages.Count) : string.Format(Strings.NOfMPages, pages.Count, source.PageCount);
            return (saved.ToArray(), which);
        }, ctx.Cancellation);
        return new Preview(await PdfConverter.PreviewAsync(page, request.MaxSize), [new Fact(Strings.OnPages, pages)]);
    }

    /// <summary>The watermark drawn once into a form, at its natural size, to be placed on each page.</summary>
    sealed class Mark(XForm form, double width, double height, XImage? picture) : IDisposable
    {
        public XForm Form { get; } = form;
        public double Width { get; } = width;
        public double Height { get; } = height;

        public static Mark Make(PdfDocument document, Target target, string temp)
        {
            if (target.Choice("mark") == "text")
            {
                var text = target.Text("text");
                var line = PdfLettering.Measure(text, PdfLettering.Font(text, 100, bold: true));
                return new Mark(PdfLettering.Form(document, line, PdfLettering.ColourOf(target.Colour("colour") with { A = 255 })), line.Width, line.Height, null);
            }
            // PDFsharp writes a picture's transparency only into a PDF 1.4 or later, as transparency came in then.
            document.Version = Math.Max(document.Version, 14);
            var picture = XImage.FromFile(Readable(target.File("picture")!, temp));
            var form = new XForm(document, new XSize(picture.PixelWidth, picture.PixelHeight));
            using (var g = XGraphics.FromForm(form))
                g.DrawImage(picture, 0, 0, picture.PixelWidth, picture.PixelHeight);
            form.DrawingFinished();
            PageStamper.Repack(form);
            return new Mark(form, picture.PixelWidth, picture.PixelHeight, picture);
        }

        /// <summary>
        /// A file PDFsharp can draw: an RGB or grey JPEG as it is, anything else, such as a PNG with a palette or a
        /// HEIC, as a full-colour PNG with its transparency.
        /// </summary>
        static string Readable(string picture, string temp)
        {
            using var frames = ImageConverter.Read(picture);
            var image = frames[0];
            if (Formats.Key(picture) == "jpg" && image.ColorSpace is ColorSpace.sRGB or ColorSpace.Gray && image.Orientation is Orientation.Undefined or Orientation.TopLeft)
                return picture;
            image.AutoOrient();
            image.ColorSpace = ColorSpace.sRGB;
            var png = Path.Combine(temp, "watermark.png");
            image.Write(png, MagickFormat.Png32);
            return png;
        }

        public void Dispose() => picture?.Dispose();
    }

    /// <summary>Places the watermark on a page as it's seen.</summary>
    static void Lay(PdfPage page, Mark mark, Target target)
    {
        var stamper = new PageStamper(page);
        var (width, height) = (stamper.Geometry.Width, stamper.Geometry.Height);
        var share = target.Number("size") / 100;
        var opacity = target.Number("opacity") / 100;
        var under = target.Flag("under");
        // From the corner at the bottom left to the one at the top right.
        var slope = Math.Atan2(height, width) * 180 / Math.PI;
        Affine Centred(double scale, double angle, double x, double y) =>
            Affine.Translate(-mark.Width / 2, -mark.Height / 2).Then(Affine.Scale(scale)).Then(Affine.Rotate(angle)).Then(Affine.Translate(x, y));

        switch (target.Choice("layout"))
        {
            case "straight":
                stamper.Place(mark.Form, Centred(share * width / mark.Width, 0, width / 2, height / 2), under, opacity);
                break;
            case "tiled":
                // Smaller copies in staggered rows across the whole page, each turned like the diagonal one.
                var scale = share * width / 2 / mark.Width;
                var (across, down) = (mark.Width * scale * 1.5, Math.Max(mark.Height * scale * 3, mark.Width * scale * 0.6));
                var reach = mark.Width * scale;
                for (var row = 0; row * down < height + reach; row++)
                    for (var x = row % 2 * across / 2; x < width + reach; x += across)
                        stamper.Place(mark.Form, Centred(scale, slope, x, row * down), under, opacity);
                break;
            default:
                stamper.Place(mark.Form, Centred(share * width / mark.Width, slope, width / 2, height / 2), under, opacity);
                break;
        }
        stamper.Finish();
    }
}

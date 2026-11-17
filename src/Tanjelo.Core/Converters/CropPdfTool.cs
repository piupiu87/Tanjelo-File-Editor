using System.Globalization;
using ImageMagick;
using ImageMagick.Drawing;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Crop margins…: trims a PDF's pages to their content, keeping some room around it, or by set amounts, by setting
/// each page's visible box (/CropBox) through the page geometry, so turned pages work. To find the content, each page
/// is drawn small by Windows. Cropping hides the margins; what's in them stays in the file.
/// </summary>
public sealed class CropPdfTool : IConverter, IPreviewer
{
    const double PointsPerMm = 72 / 25.4;

    /// <summary>Drawn this small to find the content: a point is under 1.5 pixels, which is close enough.</summary>
    const double FindDpi = 50;

    static readonly Target Entry = new Target("crop-pdf", Strings.CropMargins, Strings.TrimTheMarginsAroundThePages, Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = Strings.Cropping,
        Done = Strings.Cropped,
        Confirm = Strings.CropButton,
    }.WithOptions(
        new Option("mode", Strings.CropBy, OptionKind.Choice, "auto")
        {
            Choices = [new("auto", Strings.AutomaticallyToTheContent), new("amounts", Strings.BySetAmounts)],
            Hint = Strings.CroppingHidesTheMargins,
        },
        new Option("padding", Strings.PaddingKept, OptionKind.Number, "5") { Min = 0, Max = 100, Unit = Strings.Mm, ShownWhen = new("mode", "auto") },
        new Option("boxes", Strings.BoxesFor, OptionKind.Choice, "same")
        {
            Choices = [new("same", Strings.OneBoxForAll), new("each", Strings.EachItsOwn)],
            ShownWhen = new("mode", "auto"),
        },
        new Option("top", Strings.TopMargin, OptionKind.Number, "10") { Min = 0, Max = 1000, Unit = Strings.Mm, ShownWhen = new("mode", "amounts") },
        new Option("bottom", Strings.BottomMargin, OptionKind.Number, "10") { Min = 0, Max = 1000, Unit = Strings.Mm, ShownWhen = new("mode", "amounts") },
        new Option("left", Strings.LeftMargin, OptionKind.Number, "10") { Min = 0, Max = 1000, Unit = Strings.Mm, ShownWhen = new("mode", "amounts") },
        new Option("right", Strings.RightMargin, OptionKind.Number, "10") { Min = 0, Max = 1000, Unit = Strings.Mm, ShownWhen = new("mode", "amounts") });

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? [Entry] : [];

    /// <summary>A box on a page as it's seen, in points from its top-left corner.</summary>
    internal readonly record struct Box(double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;
        public double Bottom => Y + Height;

        public Box Union(Box other)
        {
            double x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
            return new(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
        }

        /// <summary>Grown by <paramref name="by"/> on every side, and kept within a page of this size.</summary>
        public Box Padded(double by, double width, double height)
        {
            double x = Math.Max(0, X - by), y = Math.Max(0, Y - by);
            return new(x, y, Math.Min(width, Right + by) - x, Math.Min(height, Bottom + by) - y);
        }
    }

    /// <summary>The box to keep of each page, or null to leave a page as it is.</summary>
    static async Task<IReadOnlyList<Box?>> BoxesAsync(string input, Target target, IReadOnlyList<PageGeometry> pages, JobContext ctx, IProgress<double> progress)
    {
        if (target.Choice("mode") == "amounts")
        {
            var (top, bottom, left, right) = (target.Number("top") * PointsPerMm, target.Number("bottom") * PointsPerMm,
                target.Number("left") * PointsPerMm, target.Number("right") * PointsPerMm);
            return pages.Select((page, i) =>
            {
                var box = new Box(left, top, page.Width - left - right, page.Height - top - bottom);
                if (box.Width < 1 || box.Height < 1) throw new ConversionException(string.Format(Strings.TheAmountsLeaveNothingOfPage, i + 1));
                return (Box?)box;
            }).ToList();
        }

        var rendered = await PdfConverter.LoadAsync(input);
        var found = new List<Box?>();
        for (var i = 0; i < pages.Count; i++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var (png, width, height) = await PdfConverter.RenderPagePngAsync(rendered, i, FindDpi, 4000);
            found.Add(Content(png, pages[i].Width / width, pages[i].Height / height));
            progress.Report(0.8 * (i + 1) / pages.Count);
        }
        var padding = target.Number("padding") * PointsPerMm;
        if (target.Choice("boxes") == "each")
            return found.Select((box, i) => box?.Padded(padding, pages[i].Width, pages[i].Height)).ToList();
        // One box for all: the content of every page together, kept within each page.
        var all = found.OfType<Box>().Aggregate((Box?)null, (sum, box) => sum?.Union(box) ?? box);
        return pages.Select(page => all?.Padded(padding, page.Width, page.Height)).ToList();
    }

    /// <summary>Where a page's content is, from a small picture of it: whatever differs from the colour at its edges.</summary>
    static Box? Content(byte[] png, double xScale, double yScale)
    {
        using var picture = new MagickImage(png);
        picture.ColorFuzz = new Percentage(6);
        var bounds = picture.BoundingBox;
        if (bounds is null || bounds.Width == 0 || bounds.Height == 0 || (bounds.Width == picture.Width && bounds.Height == picture.Height && IsBlank(picture)))
            return null;
        // A pixel's whole width counts, so content isn't cut by rounding.
        return new Box(bounds.X * xScale, bounds.Y * yScale, bounds.Width * xScale, bounds.Height * yScale);
    }

    static bool IsBlank(MagickImage picture) => picture.Statistics().GetChannel(PixelChannel.Composite)?.StandardDeviation < 0.01 * Quantum.Max;

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var geometry = await Task.Run(() =>
        {
            using var document = PdfFiles.OpenForImport(input);
            return document.Pages.Cast<PdfPage>().Select(p => new PageGeometry(p)).ToList();
        }, ctx.Cancellation);
        var boxes = await BoxesAsync(input, target, geometry, ctx, ctx.Progress);
        // Nothing to do means no file: every box is the whole page as it is, give or take a point.
        if (boxes.Select((box, i) => box is not { } b || (b.X < 1 && b.Y < 1 && b.Width > geometry[i].Width - 2 && b.Height > geometry[i].Height - 2)).All(same => same))
            throw new ConversionException(Strings.ThereAreNoMarginsToCrop);
        return [await Task.Run(() =>
        {
            using var document = PdfFiles.OpenForChanges(input);
            for (var i = 0; i < document.PageCount; i++)
                if (boxes[i] is { } box)
                    Crop(document.Pages[i], box);
            PdfFiles.NoteIfSigned(document, ctx);
            var output = OutputPaths.Suffixed(input, "cropped", "pdf");
            PdfFiles.Save(document, output);
            ctx.Progress.Report(1);
            return output;
        }, ctx.Cancellation)];
    }

    /// <summary>Sets the page's visible box to <paramref name="box"/> of the page as seen.</summary>
    static void Crop(PdfPage page, Box box)
    {
        var (x0, y0, x1, y1) = new PageGeometry(page).ToPage(box.X, box.Y, box.Width, box.Height);
        page.CropBox = new PdfRectangle(new XPoint(x0, y0), new XPoint(x1, y1));
    }

    /// <summary>The first page with what's kept outlined and the rest shaded, and the size kept.</summary>
    public async Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx)
    {
        var geometry = await Task.Run(() =>
        {
            using var document = PdfFiles.OpenForImport(inputs[0]);
            return document.Pages.Cast<PdfPage>().Select(p => new PageGeometry(p)).ToList();
        }, ctx.Cancellation);
        var boxes = await BoxesAsync(inputs[0], target, target.Choice("boxes") == "each" ? geometry.Take(1).ToList() : geometry, ctx, new ActionProgress<double>(_ => { }));
        var page = geometry[0];
        var kept = boxes[0] ?? new Box(0, 0, page.Width, page.Height);
        var rendered = await PdfConverter.LoadAsync(inputs[0]);
        var (png, width, height) = await PdfConverter.RenderPagePngAsync(rendered, 0, 300, (uint)request.MaxSize);
        using var picture = new MagickImage(png);
        var (sx, sy) = (width / page.Width, height / page.Height);
        double left = kept.X * sx, top = kept.Y * sy, right = kept.Right * sx, bottom = kept.Bottom * sy;
        new Drawables()
            .FillColor(new MagickColor(0, 0, 0, 110)).StrokeColor(MagickColors.Transparent)
            .Rectangle(0, 0, width, top).Rectangle(0, bottom, width, height).Rectangle(0, top, left, bottom).Rectangle(right, top, width, bottom)
            .FillColor(MagickColors.Transparent).StrokeColor(new MagickColor("#F97A3D")).StrokeWidth(2)
            .Rectangle(left, top, right, bottom)
            .Draw(picture);
        string Mm(Box b) => string.Format(Strings.MmSize, Math.Round(b.Width / PointsPerMm).ToString(CultureInfo.CurrentCulture), Math.Round(b.Height / PointsPerMm).ToString(CultureInfo.CurrentCulture));
        return new Preview(picture.ToByteArray(MagickFormat.Png), [new Fact(Strings.Kept, string.Format(Strings.KeptOf, Mm(kept), Mm(new Box(0, 0, page.Width, page.Height))))]);
    }
}

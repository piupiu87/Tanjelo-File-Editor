using System.Globalization;
using System.Reflection;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace Tanjelo.Core.Converters;

/// <summary>A PDF transformation matrix: x' = a·x + c·y + e, y' = b·x + d·y + f.</summary>
internal readonly record struct Affine(double A, double B, double C, double D, double E, double F)
{
    public static Affine Identity => new(1, 0, 0, 1, 0, 0);

    public static Affine Scale(double factor) => new(factor, 0, 0, factor, 0, 0);

    public static Affine Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    public static Affine Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    /// <summary>Turned anticlockwise by <paramref name="degrees"/>, as PDF's y axis points up.</summary>
    public static Affine Rotate(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (sin, cos) = Math.SinCos(radians);
        return new(cos, sin, -sin, cos, 0, 0);
    }

    /// <summary>This transformation, then <paramref name="next"/>, as PDF's cm concatenates them.</summary>
    public Affine Then(Affine next) => new(
        A * next.A + B * next.C, A * next.B + B * next.D,
        C * next.A + D * next.C, C * next.B + D * next.D,
        E * next.A + F * next.C + next.E, E * next.B + F * next.D + next.F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>As the operands of PDF's cm operator.</summary>
    public override string ToString() => string.Join(" ", new[] { A, B, C, D, E, F }.Select(Number));

    internal static string Number(double value) => Math.Abs(value) < 1e-9 ? "0" : value.ToString("0.#####", CultureInfo.InvariantCulture);
}

/// <summary>
/// Where a page is seen: its visible box (the crop box, within the media box), turned by /Rotate and scaled by
/// /UserUnit. Tools work in the page as it's seen, in points, as people point at it; this maps that onto the page's
/// own coordinates, so stamps, OCR text and crops land in the right place on turned, cropped and scaled pages.
/// </summary>
internal sealed class PageGeometry
{
    public PageGeometry(PdfPage page)
    {
        var media = Box(page, "/MediaBox") ?? (0, 0, 612, 792);
        var crop = Box(page, "/CropBox") is { } c
            ? (Math.Max(c.X0, media.X0), Math.Max(c.Y0, media.Y0), Math.Min(c.X1, media.X1), Math.Min(c.Y1, media.Y1))
            : media;
        if (crop.Item3 <= crop.Item1 || crop.Item4 <= crop.Item2) crop = media;
        (Left, Bottom, Right, Top) = crop;
        Rotation = ((int)Math.Round(page.Elements.GetInteger("/Rotate") / 90.0) % 4 + 4) % 4 * 90;
        Unit = page.Elements.GetReal("/UserUnit") is > 0 and var unit ? unit : 1;
        var (width, height) = ((Right - Left) * Unit, (Top - Bottom) * Unit);
        (Width, Height) = Rotation is 90 or 270 ? (height, width) : (width, height);
    }

    /// <summary>The visible box in the page's own coordinates.</summary>
    public double Left { get; }
    public double Bottom { get; }
    public double Right { get; }
    public double Top { get; }

    /// <summary>How far the page is turned clockwise when shown: 0, 90, 180 or 270.</summary>
    public int Rotation { get; }

    /// <summary>The size of a unit of the page's coordinates in points.</summary>
    public double Unit { get; }

    /// <summary>The page as seen, in points.</summary>
    public double Width { get; }
    public double Height { get; }

    /// <summary>
    /// From the page as seen, in points from its bottom-left corner with y up (the way a form is drawn), to the
    /// page's own coordinates.
    /// </summary>
    public Affine FromSeen
    {
        get
        {
            var u = 1 / Unit;
            return Rotation switch
            {
                90 => new(0, u, -u, 0, Right, Bottom),
                180 => new(-u, 0, 0, -u, Right, Top),
                270 => new(0, -u, u, 0, Left, Top),
                _ => new(u, 0, 0, u, Left, Bottom),
            };
        }
    }

    /// <summary>A point of the page as seen, from its top-left corner with y down, in the page's own coordinates.</summary>
    public (double X, double Y) ToPage(double x, double y) => FromSeen.Apply(x, Height - y);

    /// <summary>A rectangle of the page as seen, from its top-left corner with y down, in the page's own coordinates.</summary>
    public (double X0, double Y0, double X1, double Y1) ToPage(double x, double y, double width, double height)
    {
        var (ax, ay) = ToPage(x, y);
        var (bx, by) = ToPage(x + width, y + height);
        return (Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
    }


    /// <summary>A box of the page, which PDFsharp may hold as an array or as its own rectangle; null when it's missing or empty.</summary>
    static (double X0, double Y0, double X1, double Y1)? Box(PdfPage page, string key)
    {
        if (!page.Elements.ContainsKey(key)) return null;
        var box = page.Elements.GetRectangle(key);
        if (box.IsZero || box.Width <= 0 || box.Height <= 0) return null;
        return (Math.Min(box.X1, box.X2), Math.Min(box.Y1, box.Y2), Math.Max(box.X1, box.X2), Math.Max(box.Y1, box.Y2));
    }
}

/// <summary>
/// Puts things drawn with PDFsharp onto existing pages: each is drawn into a form (a small page of its own, in its own
/// clean coordinates) and placed with a matrix from <see cref="PageGeometry"/>. What goes over the page is drawn after
/// its content, which is first wrapped in q and Q so any state it leaves behind (a scale never undone, an extra Q)
/// can't move it; what goes under is drawn before it.
/// </summary>
internal sealed class PageStamper(PdfPage page)
{
    // PDFsharp keeps the form object of an XForm to itself. Tanjelo pins PDFsharp at 6.2.4, the last MIT release,
    // so the name can't change under it.
    static readonly PropertyInfo FormObject = typeof(XForm).GetProperty("PdfForm", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("PDFsharp's XForm has no PdfForm.");

    readonly StringBuilder _over = new();
    readonly StringBuilder _under = new();

    public PageGeometry Geometry { get; } = new(page);

    /// <summary>The PDF object of a form drawn with XGraphics.</summary>
    public static PdfFormXObject ObjectOf(XForm form) => (PdfFormXObject)FormObject.GetValue(form)!;

    /// <summary>Places <paramref name="form"/> so that <paramref name="toSeen"/> maps its space onto the page as seen, in points from its bottom-left corner.</summary>
    public void Place(XForm form, Affine toSeen, bool under = false, double opacity = 1) => Place(ObjectOf(form), toSeen, under, opacity);

    public void Place(PdfDictionary form, Affine toSeen, bool under = false, double opacity = 1)
    {
        var name = Add("/XObject", "TjForm", form.Reference!);
        var into = under ? _under : _over;
        into.Append("q ");
        if (opacity < 1)
        {
            var alpha = new PdfDictionary(page.Owner);
            alpha.Elements["/Type"] = new PdfName("/ExtGState");
            alpha.Elements["/ca"] = new PdfReal(opacity);
            alpha.Elements["/CA"] = new PdfReal(opacity);
            into.Append(Add("/ExtGState", "TjAlpha", alpha)).Append(" gs ");
        }
        into.Append(toSeen.Then(Geometry.FromSeen)).Append(" cm ").Append(name).Append(" Do Q\n");
    }

    /// <summary>Writes what was placed into the page's content.</summary>
    public void Finish()
    {
        if (_under.Length > 0)
            page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes(_under.ToString()));
        if (_over.Length == 0) return;
        var (open, close) = Balance(page);
        page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("q ", open)) + "\n"));
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Q ", close)) + "\n" + _over));
    }

    /// <summary>
    /// How many q to put before the page's content and Q after it, so that it neither pops more than it pushed nor
    /// leaves anything pushed: one more q than the most Q it closes beyond its own q, and as many Q as are left open.
    /// </summary>
    static (int Open, int Close) Balance(PdfPage page)
    {
        int depth = 0, lowest = 0;
        try
        {
            foreach (var op in ContentReader.ReadContent(page).OfType<COperator>())
            {
                if (op.Name == "q") depth++;
                else if (op.Name == "Q") lowest = Math.Min(lowest, --depth);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (1, 1);
        }
        var open = 1 - lowest;
        return (open, open + depth);
    }

    /// <summary>Adds a resource under a name the page doesn't use yet and returns the name.</summary>
    string Add(string kind, string prefix, PdfItem value)
    {
        var resources = page.Elements.GetDictionary("/Resources");
        if (resources is null)
        {
            resources = new PdfDictionary(page.Owner);
            page.Elements["/Resources"] = resources;
        }
        var group = resources.Elements.GetDictionary(kind);
        if (group is null)
        {
            group = new PdfDictionary(page.Owner);
            resources.Elements[kind] = group;
        }
        var name = $"/{prefix}0";
        for (var i = 1; group.Elements.ContainsKey(name); i++) name = $"/{prefix}{i}";
        group.Elements[name] = value;
        return name;
    }
}

using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Flatten: form fields, comments, stamps and drawn signatures become part of the page. Each visible annotation's
/// normal appearance is drawn into the page where it shows (its bounding box, through its matrix, onto its
/// rectangle, as the PDF standard sets out), and the annotation goes. Links stay and popups go. A text field with no
/// appearance, as forms that ask readers to draw them (/NeedAppearances) have, gets one drawn from its value first;
/// other fields with none are left as they are and counted. The form goes once no fields are left. XFA forms are
/// refused.
/// </summary>
public sealed partial class FlattenPdfTool : IConverter
{
    static readonly Target Entry = new("flatten-pdf", Strings.Flatten, Strings.FormsAndCommentsBecomePartOfThePage, Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = Strings.Flattening,
        Done = Strings.Flattened,
    };

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? [Entry] : [];

    // Annotation flags (PDF 2.0, table 167).
    const int Hidden = 1 << 1, NoView = 1 << 5;

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var document = PdfFiles.OpenForChanges(input);
        var catalog = document.Internals.Catalog;
        var form = catalog.Elements.GetDictionary("/AcroForm");
        if (form?.Elements.ContainsKey("/XFA") == true) throw new ConversionException(Strings.ThisIsAnXFAForm);
        var resources = form?.Elements.GetDictionary("/DR");

        var changed = 0;
        var kept = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance); // widgets left as they are
        var signatures = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance); // invisible signatures, also left
        for (var i = 0; i < document.PageCount; i++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            var page = document.Pages[i];
            if (page.Elements.GetArray("/Annots") is not { } annotations) continue;
            var stamper = new PageStamper(page);
            var remaining = new PdfArray(document);
            foreach (var item in annotations.Elements.ToList())
            {
                if (Resolve(item) is not PdfDictionary annotation)
                    continue;
                var subtype = annotation.Elements.GetName("/Subtype");
                if (subtype == "/Link")
                {
                    remaining.Elements.Add(item);
                    continue;
                }
                changed++;
                if (subtype == "/Popup" || (annotation.Elements.GetInteger("/F") & (Hidden | NoView)) != 0)
                    continue; // never drawn, so nothing to keep
                var appearance = Appearance(annotation) ?? (Field(annotation, "/FT") is PdfName { Value: "/Tx" } ? TextAppearance(document, annotation, form) : null);
                if (appearance is null)
                {
                    if (subtype != "/Widget") continue; // a comment with nothing to show
                    changed--;
                    remaining.Elements.Add(item);
                    // An invisible signature is meant to have nothing to show; other fields are counted.
                    if (Field(annotation, "/FT") is not PdfName { Value: "/Sig" }) kept.Add(annotation);
                    else signatures.Add(annotation);
                    continue;
                }
                if (!appearance.Elements.ContainsKey("/Resources") && resources is not null)
                    appearance.Elements["/Resources"] = resources;
                if (Placement(appearance, annotation) is { } placement)
                    stamper.PlaceInPage(appearance, placement);
            }
            stamper.Finish();
            if (remaining.Elements.Count > 0) page.Elements["/Annots"] = remaining;
            else page.Elements.Remove("/Annots");
            ctx.Progress.Report(0.9 * (i + 1) / document.PageCount);
        }
        if (changed == 0) throw new ConversionException(Strings.ThereSNothingToFlatten);

        // The form keeps only fields whose widgets are still there, and goes when none are.
        if (form is not null)
        {
            var fields = form.Elements.GetArray("/Fields")?.Elements.Where(f => Resolve(f) is PdfDictionary field && (Keeps(field, kept, 0) || Keeps(field, signatures, 0))).ToList() ?? [];
            if (fields.Count == 0) catalog.Elements.Remove("/AcroForm");
            else
            {
                var array = new PdfArray(document);
                foreach (var field in fields) array.Elements.Add(field);
                form.Elements["/Fields"] = array;
            }
        }
        if (kept.Count > 0) ctx.Note(kept.Count == 1 ? Strings.N1FieldHadNoLook : string.Format(Strings.FieldsHadNoLook, kept.Count));
        PdfFiles.NoteIfSigned(document, ctx);
        var output = OutputPaths.Suffixed(input, "flattened", "pdf");
        PdfFiles.Save(document, output);
        ctx.Progress.Report(1);
        return [output];
    }, ctx.Cancellation);

    static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    /// <summary>A field's entry, from the widget or the field it belongs to, as fields pass entries down to their kids.</summary>
    static PdfItem? Field(PdfDictionary annotation, string key)
    {
        for (var (at, depth) = (annotation, 0); at is not null && depth < 32; at = at.Elements.GetDictionary("/Parent"), depth++)
            if (Resolve(at.Elements[key]) is { } value)
                return value;
        return null;
    }

    static bool Keeps(PdfDictionary field, HashSet<PdfDictionary> kept, int depth) =>
        kept.Contains(field) || (depth < 32 && field.Elements.GetArray("/Kids")?.Elements.Select(Resolve).OfType<PdfDictionary>().Any(k => Keeps(k, kept, depth + 1)) == true);

    /// <summary>The annotation's normal appearance: its stream, or for a check box or the like, the one for its state.</summary>
    static PdfDictionary? Appearance(PdfDictionary annotation)
    {
        var normal = Resolve(annotation.Elements.GetDictionary("/AP")?.Elements["/N"]);
        PdfDictionary? stream = normal switch
        {
            PdfDictionary { Stream: not null } single => single,
            PdfDictionary states when annotation.Elements.GetName("/AS") is { Length: > 0 } state => Resolve(states.Elements[state]) as PdfDictionary,
            PdfDictionary { Elements.Count: 1 } states => Resolve(states.Elements.Values.First()) as PdfDictionary,
            _ => null,
        };
        if (stream?.Stream is null) return null;
        // An appearance is a form; some writers leave out saying so, which drawing it from the page needs.
        stream.Elements["/Type"] = new PdfName("/XObject");
        stream.Elements["/Subtype"] = new PdfName("/Form");
        return stream;
    }

    /// <summary>
    /// The matrix that puts an appearance on the page: its bounding box, through its own matrix, scaled and moved onto
    /// the annotation's rectangle (PDF 2.0, 12.5.5). Null when either is empty.
    /// </summary>
    static Affine? Placement(PdfDictionary appearance, PdfDictionary annotation)
    {
        if (Rect(appearance.Elements.GetArray("/BBox") ?? appearance.Elements["/BBox"]) is not { } box
            || Rect(annotation.Elements.GetArray("/Rect") ?? annotation.Elements["/Rect"]) is not { } rect)
            return null;
        var matrix = Numbers(appearance.Elements.GetArray("/Matrix")) is [var a, var b, var c, var d, var e, var f] ? new Affine(a, b, c, d, e, f) : Affine.Identity;
        var corners = new[] { matrix.Apply(box.X0, box.Y0), matrix.Apply(box.X1, box.Y0), matrix.Apply(box.X0, box.Y1), matrix.Apply(box.X1, box.Y1) };
        double x0 = corners.Min(p => p.X), y0 = corners.Min(p => p.Y), x1 = corners.Max(p => p.X), y1 = corners.Max(p => p.Y);
        if (x1 - x0 < 1e-6 || y1 - y0 < 1e-6) return null;
        // The form's own matrix is applied when it's drawn, so this maps the transformed box onto the rectangle.
        return Affine.Translate(-x0, -y0).Then(Affine.Scale((rect.X1 - rect.X0) / (x1 - x0), (rect.Y1 - rect.Y0) / (y1 - y0))).Then(Affine.Translate(rect.X0, rect.Y0));
    }

    static (double X0, double Y0, double X1, double Y1)? Rect(PdfItem? item)
    {
        var values = item switch
        {
            PdfRectangle r => [r.X1, r.Y1, r.X2, r.Y2],
            PdfArray array => Numbers(array),
            _ => null,
        };
        if (values is not [var a, var b, var c, var d]) return null;
        return (Math.Min(a, c), Math.Min(b, d), Math.Max(a, c), Math.Max(b, d));
    }

    static double[]? Numbers(PdfArray? array)
    {
        if (array is null) return null;
        var numbers = new List<double>();
        foreach (var item in array.Elements.Select(Resolve))
        {
            switch (item)
            {
                case PdfInteger n: numbers.Add(n.Value); break;
                case PdfReal n: numbers.Add(n.Value); break;
                default: return null;
            }
        }
        return [.. numbers];
    }

    [GeneratedRegex(@"(-?[\d.]+)\s+Tf")]
    private static partial Regex FontSize();

    [GeneratedRegex(@"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+rg")]
    private static partial Regex FillColour();

    [GeneratedRegex(@"(-?[\d.]+)\s+g(?![\w])")]
    private static partial Regex FillGrey();

    /// <summary>
    /// An appearance for a text field that has none, drawn from its value the way readers draw one: the size and
    /// colour its default appearance (/DA) gives, Arial (or a font with its letters) in place of the font it names,
    /// aligned as its /Q says, a line at a time.
    /// </summary>
    static PdfDictionary? TextAppearance(PdfDocument document, PdfDictionary annotation, PdfDictionary? form)
    {
        var value = Field(annotation, "/V") switch
        {
            PdfString s => s.Value,
            PdfName n => n.Value.TrimStart('/'),
            _ => null,
        };
        if (string.IsNullOrEmpty(value) || Rect(annotation.Elements.GetArray("/Rect") ?? annotation.Elements["/Rect"]) is not { } rect) return null;
        var (width, height) = (rect.X1 - rect.X0, rect.Y1 - rect.Y0);
        if (width < 1 || height < 1) return null;
        var da = (Field(annotation, "/DA") as PdfString)?.Value ?? (form?.Elements["/DA"] as PdfString)?.Value ?? "";
        var lines = value.Replace("\r\n", "\n").Split('\n', '\r');
        var size = FontSize().Match(da) is { Success: true } m && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s2) && s2 > 0
            ? s2
            : Math.Clamp((height - 4) / lines.Length / 1.15, 4, 12); // 0 means as large as fits
        var colour = XColors.Black;
        if (FillColour().Match(da) is { Success: true } rgb)
            colour = XColor.FromArgb(Byte(rgb.Groups[1].Value), Byte(rgb.Groups[2].Value), Byte(rgb.Groups[3].Value));
        else if (FillGrey().Match(da) is { Success: true } grey)
            colour = XColor.FromArgb(Byte(grey.Groups[1].Value), Byte(grey.Groups[1].Value), Byte(grey.Groups[1].Value));
        var align = Field(annotation, "/Q") is PdfInteger q ? q.Value : 0;

        var drawn = new XForm(document, new XSize(width, height));
        using (var g = XGraphics.FromForm(drawn))
        {
            var top = lines.Length == 1 ? (height - size * 1.15) / 2 : 2;
            foreach (var line in lines)
            {
                var measured = PdfLettering.Measure(line, PdfLettering.Font(line, size));
                var x = align switch { 1 => (width - measured.Width) / 2, 2 => width - 2 - measured.Width, _ => 2 };
                g.DrawString(line, measured.Font, new XSolidBrush(colour), x, top + measured.Ascent, XStringFormats.BaseLineLeft);
                top += size * 1.15;
            }
        }
        drawn.DrawingFinished();
        return PageStamper.ObjectOf(drawn);

        static byte Byte(string fraction) => (byte)Math.Clamp(Math.Round(double.Parse(fraction, CultureInfo.InvariantCulture) * 255), 0, 255);
    }
}

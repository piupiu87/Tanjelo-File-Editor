using System.Collections.Concurrent;
using System.Text;
using System.Windows;
using System.Windows.Media;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Text that PDF tools draw onto pages, such as page numbers and watermarks, as real text in Windows' own fonts with
/// only the letters used embedded. Arial by default; text with letters Arial lacks takes the first installed font that
/// has them all, so Chinese, Japanese, Korean and Indic text shows. Fonts that forbid embedding are skipped.
/// </summary>
internal static class PdfLettering
{
    /// <summary>The fonts tried in order. They come with Windows 10 and 11, as long as the language's fonts are installed.</summary>
    static readonly string[] Families = ["Arial", "Segoe UI", "Microsoft YaHei", "Yu Gothic", "Malgun Gothic", "Nirmala UI", "Segoe UI Symbol"];

    static readonly object Once = new();
    static bool _ready;

    /// <summary>
    /// Lets PDFsharp find fonts in Windows. It can only be told once, before it draws any text, so everything that
    /// draws calls this first; the tests' fixtures too.
    /// </summary>
    public static void EnsureFonts()
    {
        lock (Once)
        {
            if (_ready) return;
            GlobalFontSettings.FontResolver = new WindowsFonts();
            _ready = true;
        }
    }

    /// <summary>The first of <see cref="Families"/> that has every letter of <paramref name="text"/>, or Arial.</summary>
    public static string FamilyFor(string text) => Families.FirstOrDefault(family => Covers(family, text)) ?? "Arial";

    public static XFont Font(string text, double size, bool bold = false)
    {
        EnsureFonts();
        return new XFont(FamilyFor(text), size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
    }

    static bool Covers(string family, string text)
    {
        if (WindowsFonts.Find(family, bold: false, italic: false) is not { } glyphs || glyphs.EmbeddingRights == FontEmbeddingRight.RestrictedLicense)
            return false;
        foreach (var letter in text.EnumerateRunes())
            if (!Rune.IsWhiteSpace(letter) && !Rune.IsControl(letter) && !glyphs.CharacterToGlyphMap.ContainsKey(letter.Value))
                return false;
        return true;
    }

    /// <summary>A line of text measured in a font: its width, and how far it reaches above and below its baseline, in points.</summary>
    public sealed record Line(string Text, XFont Font, double Width, double Ascent, double Descent)
    {
        public double Height => Ascent + Descent;
    }

    public static Line Measure(string text, XFont font)
    {
        using var measure = XGraphics.CreateMeasureContext(new XSize(1000, 1000), XGraphicsUnit.Point, XPageDirection.Downwards);
        var width = measure.MeasureString(text, font).Width;
        var metrics = font.Metrics;
        var scale = font.Size / metrics.UnitsPerEm;
        return new Line(text, font, width, metrics.Ascent * scale, Math.Abs(metrics.Descent) * scale);
    }

    /// <summary>A form holding one line of text, as wide as the text and as tall as the font reaches, with the baseline <see cref="Line.Descent"/> above its bottom.</summary>
    public static XForm Form(PdfDocument document, Line line, XColor colour)
    {
        var form = new XForm(document, new XSize(Math.Max(1, line.Width), Math.Max(1, line.Height)));
        using (var g = XGraphics.FromForm(form))
            g.DrawString(line.Text, line.Font, new XSolidBrush(colour), 0, line.Ascent, XStringFormats.BaseLineLeft);
        form.DrawingFinished();
        return form;
    }

    public static XColor ColourOf(Colour colour) => XColor.FromArgb(colour.A, colour.R, colour.G, colour.B);

    /// <summary>
    /// Finds fonts for PDFsharp through WPF, which knows every installed family and the file each style lives in,
    /// collections (.ttc) included; PDFsharp's own Windows lookup knows only a few families. Unknown families get Arial.
    /// </summary>
    sealed class WindowsFonts : IFontResolver
    {
        static readonly ConcurrentDictionary<string, byte[]> Files = new(StringComparer.OrdinalIgnoreCase);

        public static GlyphTypeface? Find(string family, bool bold, bool italic)
        {
            var typeface = new Typeface(new System.Windows.Media.FontFamily(family), italic ? FontStyles.Italic : FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            return typeface.TryGetGlyphTypeface(out var glyphs) ? glyphs : null;
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var glyphs = Find(familyName, isBold, isItalic) ?? Find("Arial", isBold, isItalic);
            if (glyphs is null) return null;
            // The face is named by its file and its place in a collection, which WPF gives as the URI's fragment.
            var face = glyphs.FontUri.LocalPath + (glyphs.FontUri.Fragment.Length > 1 ? glyphs.FontUri.Fragment : "#0");
            return new FontResolverInfo(face, mustSimulateBold: isBold && glyphs.Weight.ToOpenTypeWeight() < 600,
                mustSimulateItalic: isItalic && glyphs.Style == FontStyles.Normal);
        }

        public byte[]? GetFont(string faceName) => Files.GetOrAdd(faceName, name =>
        {
            var hash = name.LastIndexOf('#');
            var bytes = File.ReadAllBytes(name[..hash]);
            return int.TryParse(name[(hash + 1)..], out var index) ? Face(bytes, index) : bytes;
        });

        /// <summary>One font out of a TrueType collection, as a font file of its own; any other file as it is.</summary>
        static byte[] Face(byte[] file, int index)
        {
            if (file.Length < 12 || Encoding.ASCII.GetString(file, 0, 4) != "ttcf") return file;
            var count = (int)U32(file, 8);
            var start = (int)U32(file, 12 + 4 * Math.Clamp(index, 0, count - 1));
            int tables = U16(file, start + 4);
            // A new file: the offset table, then the table records with new offsets, then each table, 4-byte aligned.
            var header = 12 + 16 * tables;
            var output = new MemoryStream();
            output.Write(file, start, 12);
            var records = new byte[16 * tables];
            var data = new MemoryStream();
            for (var i = 0; i < tables; i++)
            {
                var record = start + 12 + 16 * i;
                Array.Copy(file, record, records, 16 * i, 16);
                int offset = (int)U32(file, record + 8), length = (int)U32(file, record + 12);
                var at = (uint)(header + data.Length);
                records[16 * i + 8] = (byte)(at >> 24);
                records[16 * i + 9] = (byte)(at >> 16);
                records[16 * i + 10] = (byte)(at >> 8);
                records[16 * i + 11] = (byte)at;
                data.Write(file, offset, length);
                while (data.Length % 4 != 0) data.WriteByte(0);
            }
            output.Write(records);
            data.Position = 0;
            data.CopyTo(output);
            return output.ToArray();
        }

        static uint U32(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

        static int U16(byte[] b, int at) => b[at] << 8 | b[at + 1];
    }
}

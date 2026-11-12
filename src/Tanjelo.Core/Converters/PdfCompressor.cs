using System.IO.Compression;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Makes a PDF smaller the way Acrobat's Compress does, with nothing but PDFsharp and Magick.NET: pictures are scaled
/// down to a target resolution for the largest size they're drawn at, and re-encoded as JPEG; everything else is
/// recompressed losslessly. Fonts, text, drawings, forms, bookmarks, links and metadata are left as they are.
/// </summary>
static class PdfCompressor
{
    /// <param name="Ppi">Pixels per inch, for the largest size a picture is drawn at.</param>
    /// <param name="Jpeg">JPEG quality, 1 to 100.</param>
    public sealed record Quality(int Ppi, int Jpeg)
    {
        public static Quality Of(string choice) => choice switch
        {
            "high" => new(200, 85),
            "low" => new(100, 60),
            _ => new(150, 75),
        };
    }

    /// <summary>A picture of the document, once however often it's drawn, and the largest size it's drawn at, in points.</summary>
    internal sealed class Picture(PdfDictionary image)
    {
        public PdfDictionary Image { get; } = image;
        public double Width { get; set; }
        public double Height { get; set; }
        public int Pixels => Image.Elements.GetInteger("/Width") * Image.Elements.GetInteger("/Height");
        public int Bytes => Image.Stream?.Length ?? 0;

        public void DrawnAt(double width, double height)
        {
            Width = Math.Max(Width, width);
            Height = Math.Max(Height, height);
        }
    }

    /// <summary>What was done, for the result card and the preview.</summary>
    public sealed record Outcome(byte[] Saved, int Pictures, int Replaced);

    /// <summary>Compresses the open document in place and returns it saved to memory.</summary>
    public static Outcome Compress(PdfDocument document, Quality quality, CancellationToken ct, IProgress<double>? progress = null)
    {
        // PDFsharp turns page contents into its own objects as they're read; do that first, so they're the ones changed.
        foreach (var page in document.Pages.Cast<PdfPage>())
        {
            _ = page.Contents;
            page.Elements.Remove("/Thumb"); // a picture of the page that viewers make themselves
        }
        var pictures = Pictures(document);
        var masks = Masks(document);
        var replaced = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var done = 0;
        foreach (var picture in pictures.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (!masks.Contains(picture.Image) && Shrink(picture, quality) is { } shrunk)
            {
                Replace(picture.Image, shrunk);
                replaced.Add(picture.Image);
            }
            progress?.Report(0.8 * ++done / pictures.Count);
        }
        foreach (var stream in document.Internals.GetAllObjects().OfType<PdfDictionary>().Where(d => d.Stream is not null && !replaced.Contains(d)))
        {
            ct.ThrowIfCancellationRequested();
            Recompress(stream);
        }
        document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        using var saved = new MemoryStream();
        document.Save(saved, closeStream: false);
        progress?.Report(1);
        return new Outcome(saved.ToArray(), pictures.Count, replaced.Count);
    }

    // ---- Finding pictures and the size they're drawn at

    /// <summary>
    /// Every picture the pages draw, with the largest size each is drawn at, read from the content streams: the
    /// transformation in force when a picture is drawn maps a unit square onto the page. Where a page's content can't
    /// be read, its pictures count as filling the page.
    /// </summary>
    internal static Dictionary<PdfDictionary, Picture> Pictures(PdfDocument document)
    {
        var pictures = new Dictionary<PdfDictionary, Picture>(ReferenceEqualityComparer.Instance);
        foreach (var page in document.Pages.Cast<PdfPage>())
        {
            var unit = page.Elements.GetReal("/UserUnit") is > 0 and var u ? u : 1;
            var resources = page.Elements.GetDictionary("/Resources");
            try
            {
                Walk(ContentReader.ReadContent(page), resources, Affine.Scale(unit), 0, pictures, []);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                // Read as far as possible; anything not seen drawn gets the page's size below.
            }
            // Pictures in the page's resources that weren't seen drawn, such as on a page whose content couldn't be
            // read: as large as the page, which errs towards keeping detail.
            foreach (var image in Images(resources, 0, []))
                if (!pictures.ContainsKey(image))
                    pictures[image] = new Picture(image);
            foreach (var picture in pictures.Values.Where(p => p.Width == 0))
                picture.DrawnAt(page.Width.Point * unit, page.Height.Point * unit);
        }
        return pictures;
    }

    static void Walk(CSequence content, PdfDictionary? resources, Affine ctm, int depth, Dictionary<PdfDictionary, Picture> pictures, HashSet<PdfDictionary> open)
    {
        var saved = new Stack<Affine>();
        foreach (var op in content.OfType<COperator>())
        {
            switch (op.Name)
            {
                case "q":
                    saved.Push(ctm);
                    break;
                case "Q":
                    if (saved.Count > 0) ctm = saved.Pop();
                    break;
                case "cm" when Numbers(op.Operands) is [var a, var b, var c, var d, var e, var f]:
                    ctm = new Affine(a, b, c, d, e, f).Then(ctm);
                    break;
                case "Do" when op.Operands.Count > 0 && op.Operands[^1] is CName name:
                    if (Resolve(resources?.Elements.GetDictionary("/XObject")?.Elements[name.Name]) is not PdfDictionary xobject) break;
                    if (xobject.Elements.GetName("/Subtype") == "/Image")
                    {
                        if (!pictures.TryGetValue(xobject, out var picture)) pictures[xobject] = picture = new Picture(xobject);
                        picture.DrawnAt(Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B), Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D));
                    }
                    else if (xobject.Elements.GetName("/Subtype") == "/Form" && depth < 8 && open.Add(xobject))
                    {
                        var matrix = Numbers(xobject.Elements.GetArray("/Matrix")) is [var a2, var b2, var c2, var d2, var e2, var f2]
                            ? new Affine(a2, b2, c2, d2, e2, f2)
                            : Affine.Identity;
                        Walk(ContentReader.ReadContent(xobject.Stream.UnfilteredValue), xobject.Elements.GetDictionary("/Resources") ?? resources,
                            matrix.Then(ctm), depth + 1, pictures, open);
                        open.Remove(xobject);
                    }
                    break;
            }
        }
    }

    /// <summary>The pictures named in resources, including those of the forms they name.</summary>
    static IEnumerable<PdfDictionary> Images(PdfDictionary? resources, int depth, HashSet<PdfDictionary> seen)
    {
        var xobjects = resources?.Elements.GetDictionary("/XObject");
        if (xobjects is null || depth > 8) yield break;
        foreach (var item in xobjects.Elements.Values)
        {
            if (Resolve(item) is not PdfDictionary xobject || !seen.Add(xobject)) continue;
            if (xobject.Elements.GetName("/Subtype") == "/Image") yield return xobject;
            else foreach (var inner in Images(xobject.Elements.GetDictionary("/Resources"), depth + 1, seen)) yield return inner;
        }
    }

    /// <summary>Soft masks and stencil masks other pictures use, which are left as they are.</summary>
    static HashSet<PdfDictionary> Masks(PdfDocument document)
    {
        var masks = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var image in document.Internals.GetAllObjects().OfType<PdfDictionary>().Where(d => d.Elements.GetName("/Subtype") == "/Image"))
            foreach (var key in new[] { "/SMask", "/Mask" })
                if (Resolve(image.Elements[key]) is PdfDictionary mask)
                    masks.Add(mask);
        return masks;
    }

    static double[]? Numbers(IEnumerable<object>? items)
    {
        if (items is null) return null;
        var numbers = new List<double>();
        foreach (var item in items)
        {
            switch (item)
            {
                case CInteger i: numbers.Add(i.Value); break;
                case CReal r: numbers.Add(r.Value); break;
                case PdfInteger i: numbers.Add(i.Value); break;
                case PdfReal r: numbers.Add(r.Value); break;
                default: return null;
            }
        }
        return [.. numbers];
    }

    static double[]? Numbers(CSequence operands) => Numbers(operands.Cast<object>());

    static double[]? Numbers(PdfArray? array) => array is null ? null : Numbers(array.Elements.Cast<object>());

    static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    // ---- Re-encoding pictures

    /// <summary>The new data for a picture and its size, or null when it's left as it is.</summary>
    internal sealed record Shrunk(byte[] Jpeg, int Width, int Height);

    /// <summary>
    /// Scales a picture down to the target resolution for the largest size it's drawn at and re-encodes it as JPEG:
    /// JPEGs (8-bit grey, RGB or CMYK) always, and lossless 8-bit grey or RGB pictures when they're photo-like.
    /// Null for everything else, such as 1-bit scans, JPEG 2000, indexed colour and masked pictures, or when the
    /// result isn't smaller.
    /// </summary>
    internal static Shrunk? Shrink(Picture picture, Quality quality)
    {
        var image = picture.Image;
        var elements = image.Elements;
        int width = elements.GetInteger("/Width"), height = elements.GetInteger("/Height");
        if (image.Stream is null || width < 1 || height < 1 || elements.GetInteger("/BitsPerComponent") != 8
            || elements.GetBoolean("/ImageMask") || elements.ContainsKey("/SMask") || elements.ContainsKey("/Mask")
            || elements.ContainsKey("/DecodeParms") && FilterOf(image) == "/DCTDecode")
            return null;
        var channels = Channels(elements["/ColorSpace"]);
        var filter = FilterOf(image);
        // CMYK JPEGs saved by Photoshop, and by ImageMagick, are inverted and say so with /Decode, which is kept.
        var decode = Numbers(elements.GetArray("/Decode"));
        if (decode is not null && !(filter == "/DCTDecode" && channels == 4 && decode.SequenceEqual([1d, 0, 1, 0, 1, 0, 1, 0])))
            return null;
        // No smaller than the target resolution at the largest size it's drawn.
        var scale = Math.Min(1, Math.Max(picture.Width / 72 * quality.Ppi / width, picture.Height / 72 * quality.Ppi / height));

        MagickImage pixels;
        if (filter == "/DCTDecode" && channels is 1 or 3 or 4)
        {
            try
            {
                pixels = new MagickImage(image.Stream.Value);
            }
            catch (MagickException)
            {
                return null;
            }
            if (pixels.Width != width || pixels.Height != height)
            {
                pixels.Dispose();
                return null;
            }
        }
        else if (filter == "/FlateDecode" && channels is 1 or 3)
        {
            byte[] raw;
            try
            {
                raw = image.Stream.UnfilteredValue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
            // Screenshots and diagrams stay lossless; only the lossless recompression applies to them.
            if (raw.Length < (long)width * height * channels || !PhotoLike(raw, width * height, channels)) return null;
            pixels = new MagickImage(channels == 3 ? raw.AsSpan(0, width * height * 3).ToArray() : Grey(raw, width * height),
                new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
        }
        else
        {
            return null;
        }

        using (pixels)
        {
            var resized = scale < 0.95;
            if (resized)
                pixels.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale))) { IgnoreAspectRatio = true });
            pixels.Strip();
            pixels.Quality = (uint)quality.Jpeg;
            // ImageMagick would write a picture that looks grey as a one-channel JPEG; the PDF's colour space says how many there are.
            pixels.ColorType = channels switch { 1 => ColorType.Grayscale, 4 => ColorType.ColorSeparation, _ => ColorType.TrueColor };
            var jpeg = pixels.ToByteArray(MagickFormat.Jpeg);
            // A JPEG re-encoded at its own size loses a little each time, so it's only worth it for a real saving.
            var worth = resized || filter == "/FlateDecode" ? image.Stream.Length : image.Stream.Length * 0.9;
            if (JpegChannels(jpeg) != channels || jpeg.Length >= worth) return null;
            return new Shrunk(jpeg, (int)pixels.Width, (int)pixels.Height);
        }
    }

    /// <summary>A picture's pixels, for the preview; null for kinds not decoded here, such as 1-bit scans.</summary>
    internal static MagickImage? Decode(PdfDictionary image)
    {
        try
        {
            var filter = FilterOf(image);
            if (filter is "/DCTDecode" or "/JPXDecode") return new MagickImage(image.Stream.Value);
            int width = image.Elements.GetInteger("/Width"), height = image.Elements.GetInteger("/Height");
            var channels = Channels(image.Elements["/ColorSpace"]);
            if (filter is not (null or "/FlateDecode") || image.Elements.GetInteger("/BitsPerComponent") != 8 || channels is not (1 or 3)) return null;
            var raw = image.Stream.UnfilteredValue;
            if (raw.Length < width * height * channels) return null;
            return new MagickImage(channels == 3 ? raw.AsSpan(0, width * height * 3).ToArray() : Grey(raw, width * height),
                new PixelReadSettings((uint)width, (uint)height, StorageType.Char, PixelMapping.RGB));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    static void Replace(PdfDictionary image, Shrunk shrunk)
    {
        image.Stream.Value = shrunk.Jpeg;
        image.Elements["/Filter"] = new PdfName("/DCTDecode");
        image.Elements.Remove("/DecodeParms");
        image.Elements.SetInteger("/Width", shrunk.Width);
        image.Elements.SetInteger("/Height", shrunk.Height);
        image.Elements.SetInteger("/Length", shrunk.Jpeg.Length);
    }

    /// <summary>How many colour channels a colour space has: 1, 3 or 4, or 0 for kinds that are left alone, such as indexed colour.</summary>
    static int Channels(PdfItem? space) => Resolve(space) switch
    {
        PdfName { Value: "/DeviceGray" or "/CalGray" } => 1,
        PdfName { Value: "/DeviceRGB" or "/CalRGB" } => 3,
        PdfName { Value: "/DeviceCMYK" } => 4,
        PdfArray { Elements.Count: 2 } array when Resolve(array.Elements[0]) is PdfName { Value: "/ICCBased" }
            && Resolve(array.Elements[1]) is PdfDictionary profile => profile.Elements.GetInteger("/N") switch { 1 => 1, 3 => 3, 4 => 4, _ => 0 },
        PdfArray { Elements.Count: > 0 } array when Resolve(array.Elements[0]) is PdfName { Value: "/CalRGB" } => 3,
        PdfArray { Elements.Count: > 0 } array when Resolve(array.Elements[0]) is PdfName { Value: "/CalGray" } => 1,
        _ => 0,
    };

    /// <summary>The stream's one filter, null for none, or "" for several or an unusual one.</summary>
    static string? FilterOf(PdfDictionary dictionary) => Resolve(dictionary.Elements["/Filter"]) switch
    {
        null => null,
        PdfName name => name.Value,
        PdfArray { Elements.Count: 0 } => null,
        PdfArray { Elements.Count: 1 } array when Resolve(array.Elements[0]) is PdfName only => only.Value,
        _ => "",
    };

    /// <summary>
    /// Whether a picture is photo-like rather than a screenshot or diagram: in a photo no few colours cover much of
    /// it, while flat areas of a few exact colours cover most of a screenshot.
    /// </summary>
    internal static bool PhotoLike(byte[] raw, int pixels, int channels)
    {
        var step = Math.Max(1, pixels / 65536);
        var counts = new Dictionary<int, int>();
        var sampled = 0;
        for (var i = 0; i < pixels; i += step, sampled++)
        {
            var at = i * channels;
            var colour = channels == 1 ? raw[at] : raw[at] << 16 | raw[at + 1] << 8 | raw[at + 2];
            counts[colour] = counts.GetValueOrDefault(colour) + 1;
        }
        var common = counts.Values.OrderDescending().Take(16).Sum();
        return common < sampled / 2;
    }

    static byte[] Grey(byte[] raw, int pixels)
    {
        var rgb = new byte[pixels * 3];
        for (var i = 0; i < pixels; i++) rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = raw[i];
        return rgb;
    }

    /// <summary>The number of colour channels in a JPEG's frame header, or 0 if there's none.</summary>
    internal static int JpegChannels(byte[] jpeg)
    {
        for (var at = 2; at + 9 < jpeg.Length;)
        {
            if (jpeg[at] != 0xFF) return 0;
            var marker = jpeg[at + 1];
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC)) return jpeg[at + 9];
            at += 2 + (jpeg[at + 2] << 8 | jpeg[at + 3]);
        }
        return 0;
    }

    // ---- Everything else

    /// <summary>Recompresses a stream losslessly at the best setting, when that's smaller; metadata stays readable.</summary>
    static void Recompress(PdfDictionary dictionary)
    {
        if (dictionary.Elements.GetName("/Type") == "/Metadata" || dictionary.Elements.ContainsKey("/DecodeParms")) return;
        var filter = FilterOf(dictionary);
        byte[] plain;
        if (filter is null)
        {
            plain = dictionary.Stream.Value;
        }
        else if (filter == "/FlateDecode")
        {
            try
            {
                plain = dictionary.Stream.UnfilteredValue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return;
            }
            // PDFsharp hands back the data as it is when it can't inflate it.
            if (plain.AsSpan().SequenceEqual(dictionary.Stream.Value)) return;
        }
        else
        {
            return;
        }
        var packed = Deflate(plain);
        if (packed.Length >= dictionary.Stream.Value.Length) return;
        dictionary.Stream.Value = packed;
        dictionary.Elements["/Filter"] = new PdfName("/FlateDecode");
        dictionary.Elements.SetInteger("/Length", packed.Length);
    }

    static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize))
            zlib.Write(data);
        return output.ToArray();
    }
}

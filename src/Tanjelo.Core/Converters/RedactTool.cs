using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using ImageMagick;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;

namespace Tanjelo.Core.Converters;

/// <summary>An area of a picture to hide, in the picture's own pixels as it shows, upright.</summary>
public readonly record struct RedactBox(int X, int Y, int Width, int Height)
{
    /// <summary>Areas written "x,y,width,height", separated by semicolons.</summary>
    public static IReadOnlyList<RedactBox> ParseAll(string text)
    {
        var boxes = new List<RedactBox>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var numbers = part.Split(',', StringSplitOptions.TrimEntries);
            var values = numbers.Select(n => int.TryParse(n, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : (int?)null).ToList();
            if (values.Count != 4 || values.Any(v => v is null) || values[2] < 1 || values[3] < 1)
                throw new ConversionException($"Couldn't read '{part}' as an area. Write each as x,y,width,height in pixels, such as 10,20,200,80.");
            boxes.Add(new RedactBox(values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value));
        }
        return boxes;
    }

    public static string FormatAll(IEnumerable<RedactBox> boxes) =>
        string.Join(";", boxes.Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.X},{b.Y},{b.Width},{b.Height}")));

    /// <summary>The part of the area inside a picture this size; null when none of it is.</summary>
    public RedactBox? Within(int width, int height)
    {
        int left = Math.Max(0, X), top = Math.Max(0, Y), right = Math.Min(width, X + Width), bottom = Math.Min(height, Y + Height);
        return right > left && bottom > top ? new RedactBox(left, top, right - left, bottom - top) : null;
    }

    /// <summary>Grown by shares of its size on each side, such as a face's box widened to take in the hair.</summary>
    public RedactBox Grown(double sides, double top, double bottom) =>
        new((int)Math.Floor(X - Width * sides), (int)Math.Floor(Y - Height * top),
            (int)Math.Ceiling(Width * (1 + 2 * sides)), (int)Math.Ceiling(Height * (1 + top + bottom)));

    public RedactBox Scaled(double by) =>
        new((int)Math.Floor(X * by), (int)Math.Floor(Y * by), (int)Math.Ceiling(Width * by), (int)Math.Ceiling(Height * by));
}

/// <summary>
/// Hides parts of a picture for good: the pixels are replaced, not covered, and what's left of the metadata that could
/// give the hidden parts away goes too. Faces can be found with YuNet run by Windows' own machine learning, and text with
/// Windows' OCR, on this PC.
/// </summary>
public static class Redaction
{
    public static readonly Choice[] Styles = [new("solid", "Solid"), new("pixelate", "Pixelate"), new("blur", "Blur")];

    /// <summary>Replaces each area's pixels. Blocks and blurs are sized from the area, so a smaller copy looks the same.</summary>
    public static void Apply(IMagickImage<byte> image, IEnumerable<RedactBox> boxes, string style, Colour colour)
    {
        var (width, height) = ((int)image.Width, (int)image.Height);
        // Each from the picture as it was, so overlapping areas look the same whatever their order, as in the editor.
        var covers = boxes.Select(b => b.Within(width, height)).OfType<RedactBox>().Select(area => (area, Cover(image, area, style, colour))).ToList();
        foreach (var (area, cover) in covers)
        {
            image.Composite(cover, area.X, area.Y, CompositeOperator.Copy, Channels.All);
            cover.Dispose();
        }
    }

    /// <summary>What goes over one area, which must lie inside the picture: the editor shows exactly this.</summary>
    public static IMagickImage<byte> Cover(IMagickImage<byte> image, RedactBox area, string style, Colour colour) => style switch
    {
        "pixelate" => Pixelated(image, area),
        "blur" => Blurred(image, area),
        // Opaque even when the colour isn't: a see-through cover would show what's under it.
        _ => new MagickImage(new MagickColor(colour.R, colour.G, colour.B), (uint)area.Width, (uint)area.Height),
    };

    /// <summary>The block size for pixelating an area: coarse enough that a face or a word can't be made out.</summary>
    internal static int Block(RedactBox area) =>
        (int)Math.Max(4, Math.Ceiling(Math.Max(Math.Min(area.Width, area.Height) / 4.0, Math.Max(area.Width, area.Height) / 12.0)));

    static IMagickImage<byte> Pixelated(IMagickImage<byte> image, RedactBox area)
    {
        var part = image.CloneArea(new MagickGeometry(area.X, area.Y, (uint)area.Width, (uint)area.Height));
        var block = Block(area);
        // Each block takes the average of its pixels, then is drawn as one flat square.
        part.Scale(new MagickGeometry((uint)Math.Max(1, Math.Ceiling(area.Width / (double)block)), (uint)Math.Max(1, Math.Ceiling(area.Height / (double)block))) { IgnoreAspectRatio = true });
        part.Sample(new MagickGeometry((uint)area.Width, (uint)area.Height) { IgnoreAspectRatio = true });
        return part;
    }

    static IMagickImage<byte> Blurred(IMagickImage<byte> image, RedactBox area)
    {
        // A strong blur, done on a smaller copy so large areas stay quick, then brought back to size.
        var sigma = Block(area) * 1.5;
        var shrink = Math.Max(1, sigma / 3);
        var part = image.CloneArea(new MagickGeometry(area.X, area.Y, (uint)area.Width, (uint)area.Height));
        part.VirtualPixelMethod = VirtualPixelMethod.Edge;
        part.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(area.Width / shrink)), (uint)Math.Max(1, Math.Round(area.Height / shrink))) { IgnoreAspectRatio = true });
        part.Blur(0, sigma / shrink);
        part.Blur(0, sigma / shrink);
        part.Resize(new MagickGeometry((uint)area.Width, (uint)area.Height) { IgnoreAspectRatio = true });
        return part;
    }

    /// <summary>
    /// Removes what could show the hidden parts or where the picture was taken: the EXIF thumbnail (a small copy from
    /// before), the maker note (where cameras keep previews of their own), the location, XMP, IPTC and Photoshop's
    /// blocks, and text notes. The camera, the date and the colour profile stay.
    /// </summary>
    public static void RemoveRevealing(IMagickImage<byte> image)
    {
        if (image.GetExifProfile() is { } exif)
        {
            exif.RemoveThumbnail();
            exif.RemoveValue(ExifTag.MakerNote);
            foreach (var value in exif.Values.Where(v => v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal)).ToList())
                exif.RemoveValue(value.Tag);
            image.SetProfile(exif);
        }
        foreach (var name in new[] { "xmp", "iptc", "8bim" })
            image.RemoveProfile(name);
        foreach (var name in image.AttributeNames.Where(n => !n.StartsWith("png:", StringComparison.Ordinal)).ToList())
            image.RemoveAttribute(name);
    }

    /// <summary>Whether Windows has an OCR language to read text with.</summary>
    public static bool CanFindText => PdfText.CanOcr;

    public static bool CanFindFaces => YuNet.IsAvailable || WindowsFindsFaces;

    static bool WindowsFindsFaces
    {
        get
        {
            try { return FaceDetector.IsSupported; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        }
    }

    /// <summary>
    /// The faces in an upright picture, each box grown to take in the hair and chin: found by YuNet where Windows can run
    /// it (Windows 11), otherwise by Windows' own face detection, which finds only upright faces facing the camera.
    /// </summary>
    public static async Task<IReadOnlyList<RedactBox>> FindFacesAsync(IMagickImage<byte> picture)
    {
        if (YuNet.IsAvailable)
        {
            try
            {
                return YuNet.Find(picture);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException && WindowsFindsFaces)
            {
                // Should Windows' machine learning fail on this picture, its face detection still looks.
            }
        }
        if (!WindowsFindsFaces) throw new ConversionException("Windows can't look for faces on this PC.");
        using var copy = picture.Clone();
        // Faces up to about 2,500 pixels across are found, so very large photos are looked at smaller.
        var scale = Fit(copy, largest: 2560, smallest: 0);
        var (width, height) = ((int)copy.Width, (int)copy.Height);
        if (copy.HasAlpha) Flatten(copy);
        var gray = copy.ToByteArray(MagickFormat.Gray);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(gray.AsBuffer(), BitmapPixelFormat.Gray8, width, height);
        var detector = await FaceDetector.CreateAsync();
        try
        {
            // As small as Windows allows, so faces in a group photo are found too.
            detector.MinDetectableFaceSize = new BitmapSize { Width = 24, Height = 24 };
        }
        catch (ArgumentException)
        {
        }
        var faces = await detector.DetectFacesAsync(bitmap);
        return faces
            .Select(f => new RedactBox((int)f.FaceBox.X, (int)f.FaceBox.Y, (int)f.FaceBox.Width, (int)f.FaceBox.Height).Grown(0.2, 0.4, 0.25).Scaled(1 / scale))
            .Select(b => b.Within((int)picture.Width, (int)picture.Height))
            .OfType<RedactBox>()
            .ToList();
    }

    /// <summary>Every word Windows' OCR reads in an upright picture, each with a little room around it; null without an OCR language.</summary>
    public static async Task<IReadOnlyList<RedactBox>?> FindTextAsync(IMagickImage<byte> picture)
    {
        if (PdfText.CreateOcrEngine() is not { } engine) return null;
        using var copy = picture.Clone();
        // Small text, as in a screenshot, reads better enlarged.
        var scale = Fit(copy, (int)OcrEngine.MaxImageDimension, smallest: 1600);
        if (copy.ColorSpace == ColorSpace.CMYK) copy.TransformColorSpace(ColorProfiles.SRGB);
        if (copy.HasAlpha) Flatten(copy);
        using var bitmap = HeicEncoder.Bitmap(copy);
        var result = await engine.RecognizeAsync(bitmap);
        return result.Lines
            .SelectMany(line => line.Words)
            .Select(w => w.BoundingRect)
            .Select(r => new RedactBox((int)r.X, (int)r.Y, (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height)))
            .Select(b => b.Grown(b.Height * 0.15 / Math.Max(1, b.Width), 0.15, 0.15).Scaled(1 / scale))
            .Select(b => b.Within((int)picture.Width, (int)picture.Height))
            .OfType<RedactBox>()
            .ToList();
    }

    /// <summary>Shrinks a copy so its longer side is at most <paramref name="largest"/>, or enlarges it to <paramref name="smallest"/>; returns the scale.</summary>
    static double Fit(IMagickImage<byte> image, int largest, int smallest)
    {
        var longest = Math.Max(image.Width, image.Height);
        var scale = longest > largest ? (double)largest / longest : longest < smallest ? Math.Min(2, (double)smallest / longest) : 1;
        if (scale != 1)
            image.Resize(new MagickGeometry((uint)Math.Max(1, Math.Round(image.Width * scale)), (uint)Math.Max(1, Math.Round(image.Height * scale))) { IgnoreAspectRatio = true });
        return scale;
    }

    internal static void Flatten(IMagickImage<byte> image)
    {
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove);
    }

    /// <summary>Opens a picture upright, for the editor's own looking.</summary>
    public static MagickImage ReadUpright(string path)
    {
        using var frames = ImageConverter.Read(path);
        var image = (MagickImage)frames[0].Clone();
        image.AutoOrient();
        return image;
    }
}

/// <summary>
/// Hides areas, faces or text in a picture for good, as solid blocks, pixels or a blur. The copy also loses the metadata
/// that could give the hidden parts away or say where it was taken.
/// </summary>
public sealed class RedactTool : IConverter
{
    static readonly Target Entry = new Target("redact", "Redact…", "Faces, text or any area", Section.Image)
    {
        Kind = TargetKind.Tool,
        Running = "Redacting…",
        Done = "Redacted",
        Confirm = "Save",
        Editor = EditorKind.Redact,
    }.WithOptions(
        new Option("boxes", "Areas", OptionKind.Text, "") { Optional = true, Remember = false, Hint = "x,y,width,height in pixels, separated by semicolons." },
        new Option("faces", "Find faces", OptionKind.Toggle, "false") { Hint = "Found on this PC." },
        new Option("text", "Find text", OptionKind.Toggle, "false") { Hint = "Every word Windows' OCR reads." },
        new Option("style", "Style", OptionKind.Choice, "solid")
        {
            Choices = Redaction.Styles,
            Hint = "Solid is the safest. Pixelate and blur are strong here, but a light blur can sometimes be undone.",
        },
        new Option("colour", "Colour", OptionKind.Colour, "#000000") { ShownWhen = new("style", "solid") });

    public IEnumerable<Target> TargetsFor(string input) =>
        Formats.Images.Contains(Formats.Key(input)) && Formats.Key(input) is not ("svg" or "ico") ? [Entry] : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var boxes = RedactBox.ParseAll(target.Text("boxes")).ToList();
        using var frames = ImageConverter.Read(input);
        var image = frames[0];
        image.AutoOrient();
        var (width, height) = ((int)image.Width, (int)image.Height);
        if (boxes.FirstOrDefault(b => b.Within(width, height) is null) is { Width: > 0 } outside)
            throw new ConversionException($"The area {RedactBox.FormatAll([outside])} is outside the {Describe.Pixels(width, height)} picture.");

        if (target.Flag("faces"))
        {
            var faces = await Redaction.FindFacesAsync(image);
            boxes.AddRange(faces);
            ctx.Note(faces.Count == 0 ? "No faces were found" : Describe.Count(faces.Count, "face") + " found");
        }
        if (target.Flag("text"))
        {
            var words = await Redaction.FindTextAsync(image)
                ?? throw new ConversionException("Finding text needs an OCR language. " + PdfText.NoOcrHint[(PdfText.NoOcrHint.IndexOf('.') + 2)..]);
            boxes.AddRange(words);
            ctx.Note(words.Count == 0 ? "No text was found" : Describe.Count(words.Count, "word") + " found");
        }
        if (boxes.Count == 0)
            throw new ConversionException(target.Flag("faces") || target.Flag("text")
                ? "Nothing was found to redact, so no copy was saved."
                : "There's nothing to redact: give the areas, or find faces or text.");
        ctx.Cancellation.ThrowIfCancellationRequested();

        Redaction.Apply(image, boxes, target.Choice("style"), target.Colour("colour"));
        Redaction.RemoveRevealing(image);
        var key = ImageConverter.ChangedCopyKey(input, image.HasAlpha);
        var output = OutputPaths.Suffixed(input, "redacted", key);
        try
        {
            if (key == "jpg") image.Quality = (uint)ctx.Options.JpegQuality;
            image.Write(output, ImageConverter.FormatFor(key));
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        ctx.Progress.Report(1);
        return [output];
    }
}

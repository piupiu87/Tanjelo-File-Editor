using System.Globalization;
using System.Numerics;
using ImageMagick;

namespace Tanjelo.Core.Converters;

/// <summary>
/// The photo editor's settings, each from −1 to 1 (exposure in stops, −3 to 3; sharpness, noise reduction, dehaze and
/// grain from 0 to 1). All zero leaves the picture as it is.
/// </summary>
public sealed record PhotoSettings
{
    public double Exposure { get; init; }
    public double Contrast { get; init; }
    public double Highlights { get; init; }
    public double Shadows { get; init; }
    public double Whites { get; init; }
    public double Blacks { get; init; }
    public double Temperature { get; init; }
    public double Tint { get; init; }
    public double Vibrance { get; init; }
    public double Saturation { get; init; }
    public double Sharpness { get; init; }
    public double Clarity { get; init; }
    public double Noise { get; init; }
    public double Dehaze { get; init; }
    public double Grain { get; init; }
    public double Vignette { get; init; }

    public static readonly PhotoSettings None = new();

    public bool IsNone => this == None;

    /// <summary>The settings as tanj takes them: exposure in stops, the rest from −100 to 100 (or 0 to 100).</summary>
    public static PhotoSettings From(Target target)
    {
        double Share(string name) => target.Number(name) / 100;
        return new PhotoSettings
        {
            Exposure = target.Number("exposure"),
            Contrast = Share("contrast"),
            Highlights = Share("highlights"),
            Shadows = Share("shadows"),
            Whites = Share("whites"),
            Blacks = Share("blacks"),
            Temperature = Share("temperature"),
            Tint = Share("tint"),
            Vibrance = Share("vibrance"),
            Saturation = Share("saturation"),
            Sharpness = Share("sharpness"),
            Clarity = Share("clarity"),
            Noise = Share("noise"),
            Dehaze = Share("dehaze"),
            Grain = Share("grain"),
            Vignette = Share("vignette"),
        };
    }

    /// <summary>The parameters for these settings, the way <see cref="From"/> reads them.</summary>
    public IReadOnlyDictionary<string, string> ToParameters()
    {
        static string Whole(double share) => OptionValues.Format(Math.Round(share * 100));
        return new Dictionary<string, string>
        {
            ["exposure"] = OptionValues.Format(Math.Round(Exposure, 2)),
            ["contrast"] = Whole(Contrast),
            ["highlights"] = Whole(Highlights),
            ["shadows"] = Whole(Shadows),
            ["whites"] = Whole(Whites),
            ["blacks"] = Whole(Blacks),
            ["temperature"] = Whole(Temperature),
            ["tint"] = Whole(Tint),
            ["vibrance"] = Whole(Vibrance),
            ["saturation"] = Whole(Saturation),
            ["sharpness"] = Whole(Sharpness),
            ["clarity"] = Whole(Clarity),
            ["noise"] = Whole(Noise),
            ["dehaze"] = Whole(Dehaze),
            ["grain"] = Whole(Grain),
            ["vignette"] = Whole(Vignette),
        };
    }
}

/// <summary>
/// Light and colour in one floating-point pass, so an 8-bit picture is rounded once instead of after every step and
/// gradients keep their smoothness. Everything that works on one channel at a time (white balance and exposure in
/// linear light, then the tone curve) is worked out in double precision for each of the 256 input levels; what mixes
/// the channels (vibrance, saturation) and the vignette run per pixel, vectorised, rows in parallel.
/// </summary>
public sealed class Tone
{
    readonly float[][] _curves; // red, green, blue: output 0..1 for each input level
    readonly float _saturation, _vibrance, _vignette;

    /// <param name="haze">For dehaze: the grey haze lays over the darkest parts, 0..1 (see <see cref="Photo.Haze"/>).</param>
    public Tone(PhotoSettings s, double haze = 0)
    {
        // Dehaze takes the haze's grey away, then adds back a little contrast and colour, as haze takes them too.
        var floor = Math.Clamp(0.9 * s.Dehaze * haze, 0, 0.6);
        var contrast = s.Contrast + 0.15 * s.Dehaze;
        var blacks = s.Blacks;
        _saturation = (float)(s.Saturation + 0.12 * s.Dehaze);
        _vibrance = (float)s.Vibrance;
        _vignette = (float)s.Vignette;

        // White balance as gains in linear light: warmer is more red and less blue, more tint is more magenta.
        var temperature = Math.Exp(0.22 * s.Temperature);
        var tint = Math.Exp(0.15 * s.Tint);
        double[] gains = [temperature * tint, 1 / tint, tint / temperature];
        var keep = 0.2126 * gains[0] + 0.7152 * gains[1] + 0.0722 * gains[2];
        var exposure = Math.Pow(2, s.Exposure);
        _curves = gains.Select(gain =>
        {
            var curve = new float[256];
            var last = 0.0;
            for (var level = 0; level < 256; level++)
            {
                var linear = ToLinear(level / 255.0) * gain / keep * exposure;
                var lifted = Math.Clamp((ToGamma(linear) - floor) / (1 - floor), 0, 1);
                var value = Curve(lifted, contrast, s.Highlights, s.Shadows, s.Whites, blacks);
                // Never darker than the level below, so no setting can turn a gradient back on itself.
                last = level == 0 ? value : Math.Max(last, value);
                curve[level] = (float)Math.Clamp(last, 0, 1);
            }
            return curve;
        }).ToArray();
    }

    /// <summary>The tone curve on a 0..1 value: bumps that peak in the shadows and highlights, the ends, and an S for contrast.</summary>
    internal static double Curve(double p, double contrast, double highlights, double shadows, double whites, double blacks)
    {
        var lows = 27.0 / 4 * p * (1 - p) * (1 - p);   // peaks at a third
        var highs = 27.0 / 4 * p * p * (1 - p);        // peaks at two thirds
        p += 0.2 * shadows * lows + 0.2 * highlights * highs;
        p += 0.15 * whites * p * p * p + 0.15 * blacks * (1 - p) * (1 - p) * (1 - p);
        p -= contrast * 0.6 / (2 * Math.PI) * Math.Sin(2 * Math.PI * Math.Clamp(p, 0, 1));
        return p;
    }

    static double ToLinear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    static double ToGamma(double v) => v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

    /// <summary>Runs the pass over a picture's pixels in place; transparency and the colour profile stay.</summary>
    public void Apply(IMagickImage<byte> image)
    {
        if (image.ColorSpace == ColorSpace.CMYK) image.TransformColorSpace(ColorProfiles.SRGB);
        if (image.ChannelCount < 3 || image.ColorSpace == ColorSpace.Gray)
            image.ColorType = image.HasAlpha ? ColorType.TrueColorAlpha : ColorType.TrueColor;
        var (width, height) = ((int)image.Width, (int)image.Height);
        using var pixels = image.GetPixelsUnsafe();
        var data = pixels.GetArea(0, 0, (uint)width, (uint)height)!;
        var stride = (int)pixels.Channels;
        var (r, g, b) = ((int)pixels.GetChannelIndex(PixelChannel.Red)!.Value, (int)pixels.GetChannelIndex(PixelChannel.Green)!.Value, (int)pixels.GetChannelIndex(PixelChannel.Blue)!.Value);
        Parallel.For(0, height, () => (new float[width], new float[width], new float[width]), (y, _, rows) =>
        {
            Row(data, y, width, height, stride, r, g, b, rows.Item1, rows.Item2, rows.Item3);
            return rows;
        }, _ => { });
        pixels.SetArea(0, 0, (uint)width, (uint)height, data);
    }

    void Row(byte[] data, int y, int width, int height, int stride, int ri, int gi, int bi, float[] red, float[] green, float[] blue)
    {
        var start = y * width * stride;
        for (var i = 0; i < width; i++)
        {
            var at = start + i * stride;
            red[i] = _curves[0][data[at + ri]];
            green[i] = _curves[1][data[at + gi]];
            blue[i] = _curves[2][data[at + bi]];
        }

        var lanes = Vector<float>.Count;
        var zero = Vector<float>.Zero;
        var one = Vector<float>.One;
        var scale = new Vector<float>(255f);
        var half = new Vector<float>(0.5f);
        var mixed = _saturation != 0 || _vibrance != 0;
        var vignette = _vignette != 0;
        // Vignette: distance from the middle, as a share of the way to the corner along an ellipse the picture's shape.
        var dy = height > 1 ? (y - (height - 1) / 2f) / ((height - 1) / 2f) : 0f;
        var x0 = (width - 1) / 2f;
        Span<float> offsets = stackalloc float[lanes];
        for (var i = 0; i < lanes; i++) offsets[i] = i;
        var lane = new Vector<float>(offsets);
        var x = 0;
        for (; x <= width - lanes; x += lanes)
        {
            var rv = new Vector<float>(red, x);
            var gv = new Vector<float>(green, x);
            var bv = new Vector<float>(blue, x);
            if (mixed)
            {
                var luma = rv * 0.2126f + gv * 0.7152f + bv * 0.0722f;
                var chroma = Vector.Max(rv, Vector.Max(gv, bv)) - Vector.Min(rv, Vector.Min(gv, bv));
                // Vibrance favours what's still muted; saturation moves every colour alike.
                var muted = one - Vector.Min(one, chroma);
                var factor = (one + new Vector<float>(_saturation)) * (one + new Vector<float>(_vibrance) * muted * muted);
                factor = Vector.Max(zero, factor);
                rv = luma + (rv - luma) * factor;
                gv = luma + (gv - luma) * factor;
                bv = luma + (bv - luma) * factor;
            }
            if (vignette)
            {
                var dx = (lane + new Vector<float>(x) - new Vector<float>(x0)) / new Vector<float>(Math.Max(1, x0));
                var distance = Vector.SquareRoot(dx * dx + new Vector<float>(dy * dy)) / new Vector<float>(MathF.Sqrt(2));
                var fall = Vector.Max(zero, distance - new Vector<float>(0.25f)) / new Vector<float>(0.75f);
                var shade = one - new Vector<float>(_vignette * 0.75f) * fall * fall * (new Vector<float>(3) - fall * 2);
                rv *= shade;
                gv *= shade;
                bv *= shade;
            }
            (Vector.Min(scale, Vector.Max(zero, rv * scale + half))).CopyTo(red, x);
            (Vector.Min(scale, Vector.Max(zero, gv * scale + half))).CopyTo(green, x);
            (Vector.Min(scale, Vector.Max(zero, bv * scale + half))).CopyTo(blue, x);
        }
        for (; x < width; x++)
        {
            float rv = red[x], gv = green[x], bv = blue[x];
            if (mixed)
            {
                var luma = rv * 0.2126f + gv * 0.7152f + bv * 0.0722f;
                var chroma = MathF.Max(rv, MathF.Max(gv, bv)) - MathF.Min(rv, MathF.Min(gv, bv));
                var muted = 1 - MathF.Min(1, chroma);
                var factor = MathF.Max(0, (1 + _saturation) * (1 + _vibrance * muted * muted));
                (rv, gv, bv) = (luma + (rv - luma) * factor, luma + (gv - luma) * factor, luma + (bv - luma) * factor);
            }
            if (vignette)
            {
                var dx = (x - x0) / MathF.Max(1, x0);
                var fall = MathF.Max(0, MathF.Sqrt(dx * dx + dy * dy) / MathF.Sqrt(2) - 0.25f) / 0.75f;
                var shade = 1 - _vignette * 0.75f * fall * fall * (3 - 2 * fall);
                (rv, gv, bv) = (rv * shade, gv * shade, bv * shade);
            }
            red[x] = Math.Clamp(rv * 255 + 0.5f, 0, 255);
            green[x] = Math.Clamp(gv * 255 + 0.5f, 0, 255);
            blue[x] = Math.Clamp(bv * 255 + 0.5f, 0, 255);
        }
        for (x = 0; x < width; x++)
        {
            var at = start + x * stride;
            data[at + ri] = (byte)red[x];
            data[at + gi] = (byte)green[x];
            data[at + bi] = (byte)blue[x];
        }
    }
}

/// <summary>
/// Develops a photo: noise reduction and dehaze, then light and colour in one 32-bit pass (<see cref="Tone"/>), then
/// clarity, sharpening and grain. Sizes are worked out from the picture's own, so a smaller copy for the preview looks
/// like the full picture.
/// </summary>
public static class Photo
{
    /// <summary>The steps before light and colour, which the editor keeps while only those change.</summary>
    public static void Prepare(IMagickImage<byte> image, PhotoSettings s, double scale)
    {
        if (s.Noise > 0)
            image.WaveletDenoise(new Percentage(12 * s.Noise * s.Noise + 1), 0.6);
        if (s.Dehaze > 0)
        {
            // Local contrast in tiles brings back what haze flattened; blended in by the amount.
            using var clear = image.Clone();
            clear.Clahe(new Percentage(12.5), new Percentage(12.5), 128, 1.5 + s.Dehaze);
            image.Composite(clear, CompositeOperator.Blend, OptionValues.Format(Math.Round(50 * s.Dehaze)));
        }
    }

    /// <summary>Light and colour, then the detail and the grain.</summary>
    public static void Finish(IMagickImage<byte> image, PhotoSettings s, double scale)
    {
        var tone = s with { Sharpness = 0, Clarity = 0, Noise = 0, Grain = 0 };
        if (!tone.IsNone) new Tone(s, s.Dehaze > 0 ? Haze(image) : 0).Apply(image);
        var longest = Math.Max(image.Width, image.Height) / Math.Max(scale, 1e-6);
        if (s.Clarity > 0)
            image.LocalContrast(Math.Max(2, longest / 60 * scale), new Percentage(60 * s.Clarity));
        if (s.Sharpness > 0)
            image.UnsharpMask(0, Math.Max(0.4, 1.2 * Math.Max(1, longest / 3000) * scale), 1.8 * s.Sharpness, 0.01);
        if (s.Grain > 0) AddGrain(image, s.Grain);
    }

    /// <summary>
    /// Film-like grain: the same speck of light or dark on all three colours, strongest in the middle tones, from a hash
    /// of each pixel's place so the same photo always gets the same grain.
    /// </summary>
    static void AddGrain(IMagickImage<byte> image, double amount)
    {
        var (width, height) = ((int)image.Width, (int)image.Height);
        using var pixels = image.GetPixelsUnsafe();
        var data = pixels.GetArea(0, 0, (uint)width, (uint)height)!;
        var stride = (int)pixels.Channels;
        var (r, g, b) = ((int)pixels.GetChannelIndex(PixelChannel.Red)!.Value, (int)pixels.GetChannelIndex(PixelChannel.Green)!.Value, (int)pixels.GetChannelIndex(PixelChannel.Blue)!.Value);
        var strength = (float)(46 * amount);
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * stride;
                var luma = (0.2126f * data[at + r] + 0.7152f * data[at + g] + 0.0722f * data[at + b]) / 255;
                var speck = strength * (Hash(x, y, 0) + Hash(x, y, 1) - 1) * (0.35f + 2.6f * luma * (1 - luma));
                foreach (var c in (ReadOnlySpan<int>)[r, g, b])
                    data[at + c] = (byte)Math.Clamp(data[at + c] + speck + 0.5f, 0, 255);
            }
        });
        pixels.SetArea(0, 0, (uint)width, (uint)height, data);
    }

    /// <summary>A number from 0 to 1 that looks random but is always the same for the same place.</summary>
    static float Hash(int x, int y, int salt)
    {
        var h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(salt * 83492791);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (h & 0xFFFFFF) / 16777216f;
    }

    /// <summary>
    /// How much grey haze lies over a picture, 0..1: what even its darkest parts have of all three colours (the darkest
    /// channel, nearly at its lowest). A clear photo has some true darks, so this is near 0.
    /// </summary>
    public static double Haze(IMagickImage<byte> image)
    {
        using var small = image.Clone();
        small.Sample(new MagickGeometry(160, 160));
        using var pixels = small.GetPixelsUnsafe();
        var rgb = pixels.ToByteArray("RGB")!;
        var darkest = new byte[rgb.Length / 3];
        for (var i = 0; i < darkest.Length; i++) darkest[i] = Math.Min(rgb[3 * i], Math.Min(rgb[3 * i + 1], rgb[3 * i + 2]));
        Array.Sort(darkest);
        return darkest[darkest.Length / 50] / 255.0;
    }

    /// <summary>
    /// A starting point for a photo: exposure that brings its middle tones to the middle, whites and blacks that use
    /// the whole range, and a little vibrance.
    /// </summary>
    public static PhotoSettings Auto(IMagickImage<byte> image)
    {
        using var small = image.Clone();
        small.Sample(new MagickGeometry(200, 200));
        using var pixels = small.GetPixelsUnsafe();
        var rgb = pixels.ToByteArray("RGB")!;
        var luma = new double[rgb.Length / 3];
        for (var i = 0; i < luma.Length; i++) luma[i] = (0.2126 * rgb[3 * i] + 0.7152 * rgb[3 * i + 1] + 0.0722 * rgb[3 * i + 2]) / 255;
        Array.Sort(luma);
        double At(double share) => luma[(int)Math.Clamp(share * (luma.Length - 1), 0, luma.Length - 1)];
        static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        var exposure = Math.Clamp(0.7 * Math.Log2(Linear(0.46) / Math.Max(1e-4, Linear(At(0.5)))), -1.5, 1.5);
        return new PhotoSettings
        {
            Exposure = Math.Round(exposure, 2),
            Whites = Math.Round(Math.Clamp((0.97 - At(0.99)) * 3, 0, 0.6), 2),
            Blacks = Math.Round(-Math.Clamp((At(0.01) - 0.02) * 5, 0, 0.6), 2),
            Vibrance = 0.15,
        };
    }

    public static void Develop(IMagickImage<byte> image, PhotoSettings s, double scale = 1)
    {
        Prepare(image, s, scale);
        Finish(image, s, scale);
    }
}

/// <summary>Edit photo: light, colour, detail and effects, saved as a copy that keeps the metadata and colour profile.</summary>
public sealed class AdjustTool : IConverter
{
    static Option Slider(string name, string label, double min = -100, double max = 100, string? hint = null) =>
        new(name, label, OptionKind.Number, "0") { Min = min, Max = max, Slider = true, Remember = false, Hint = hint };

    static readonly Target Entry = new Target("adjust", "Edit photo…", "Light, colour and detail", Section.Image)
    {
        Kind = TargetKind.Tool,
        Running = "Developing…",
        Done = "Photo edited",
        Confirm = "Save",
        Editor = EditorKind.Photo,
    }.WithOptions(
        new Option("exposure", "Exposure", OptionKind.Number, "0") { Min = -3, Max = 3, Unit = "stops", Slider = true, Remember = false },
        Slider("contrast", "Contrast"),
        Slider("highlights", "Highlights"),
        Slider("shadows", "Shadows"),
        Slider("whites", "Whites"),
        Slider("blacks", "Blacks"),
        Slider("temperature", "Temperature", hint: "Warmer above 0, cooler below."),
        Slider("tint", "Tint", hint: "Magenta above 0, green below."),
        Slider("vibrance", "Vibrance", hint: "Strengthens muted colours more than strong ones."),
        Slider("saturation", "Saturation"),
        Slider("sharpness", "Sharpness", 0),
        Slider("clarity", "Clarity", 0),
        Slider("noise", "Noise reduction", 0),
        Slider("dehaze", "Dehaze", 0),
        Slider("grain", "Grain", 0),
        Slider("vignette", "Vignette", hint: "Darker corners above 0, lighter below."));

    public IEnumerable<Target> TargetsFor(string input) =>
        Formats.Images.Contains(Formats.Key(input)) && Formats.Key(input) is not ("svg" or "ico" or "gif") ? [Entry] : [];

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var settings = PhotoSettings.From(target);
        if (settings.IsNone) throw new ConversionException("Every setting is at 0, so the photo would come out the same.");
        using var frames = ImageConverter.Read(input);
        var image = frames[0];
        image.AutoOrient();
        Photo.Develop(image, settings);
        ctx.Cancellation.ThrowIfCancellationRequested();
        var key = ImageConverter.ChangedCopyKey(input, image.HasAlpha);
        var output = OutputPaths.Suffixed(input, "edited", key);
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
    }, ctx.Cancellation);
}

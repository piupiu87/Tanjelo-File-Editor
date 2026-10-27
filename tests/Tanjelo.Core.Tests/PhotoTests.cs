using ImageMagick;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class PhotoTests
{
    /// <summary>
    /// Every hue left to right, in stripes from grey to full colour, from near black at the top to near white at the
    /// bottom: darks, lights, muted and strong colours, the same on every run.
    /// </summary>
    static MagickImage Picture(int width = 480, int height = 360)
    {
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var hue = x * 6.0 / width;
                var saturation = x / 12 % 8 / 7.0;
                var value = 0.04 + 0.92 * y / (height - 1);
                double Channel(double offset)
                {
                    var k = (hue + offset) % 6;
                    return value * (1 - saturation * Math.Max(0, Math.Min(Math.Min(k, 4 - k), 1)));
                }
                var i = (y * width + x) * 3;
                (rgb[i], rgb[i + 1], rgb[i + 2]) = ((byte)Math.Round(255 * Channel(5)), (byte)Math.Round(255 * Channel(3)), (byte)Math.Round(255 * Channel(1)));
            }
        var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height);
        image.ReadPixels(rgb, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGB"));
        return image;
    }

    static byte[] Rgb(IMagickImage<byte> image)
    {
        using var pixels = image.GetPixelsUnsafe();
        return pixels.ToByteArray("RGB")!;
    }

    static double Luma(byte[] rgb, int i) => (0.2126 * rgb[i] + 0.7152 * rgb[i + 1] + 0.0722 * rgb[i + 2]) / 255;

    static IEnumerable<int> Pixels(byte[] rgb) => Enumerable.Range(0, rgb.Length / 3).Select(p => p * 3);

    static (byte[] Before, byte[] After) Develop(PhotoSettings settings, IMagickImage<byte>? source = null)
    {
        using var image = source ?? Picture();
        var before = Rgb(image);
        using var copy = image.Clone();
        Photo.Develop(copy, settings);
        return (before, Rgb(copy));
    }

    [Fact]
    public void All_zero_leaves_every_pixel_as_it_was()
    {
        var (before, after) = Develop(PhotoSettings.None);
        Assert.Equal(before, after);
        // The pass itself, run anyway, rounds every level back to where it was.
        using var ramp = new MagickImage("gradient:black-white", 256, 8);
        var levels = Rgb(ramp);
        new Tone(PhotoSettings.None).Apply(ramp);
        Assert.Equal(levels, Rgb(ramp));
    }

    /// <summary>The mean of a measure over pixels whose original brightness is in a range.</summary>
    static double Mean(byte[] before, byte[] pixels, Func<byte[], int, double> measure, double from = 0, double to = 1.01) =>
        Pixels(before).Where(i => Luma(before, i) >= from && Luma(before, i) < to).Select(i => measure(pixels, i)).DefaultIfEmpty(0).Average();

    static double Spread(byte[] pixels)
    {
        var lumas = Pixels(pixels).Select(i => Luma(pixels, i)).ToList();
        var mean = lumas.Average();
        return Math.Sqrt(lumas.Sum(l => (l - mean) * (l - mean)) / lumas.Count);
    }

    static double Chroma(byte[] rgb, int i) => (Math.Max(rgb[i], Math.Max(rgb[i + 1], rgb[i + 2])) - Math.Min(rgb[i], Math.Min(rgb[i + 1], rgb[i + 2]))) / 255.0;

    [Fact]
    public void Light_moves_the_way_each_slider_says()
    {
        void Check(PhotoSettings settings, Func<byte[], byte[], double> measure, bool up, string what)
        {
            var (before, after) = Develop(settings);
            var (b, a) = (measure(before, before), measure(before, after));
            Assert.True(up ? a > b + 0.005 : a < b - 0.005, $"{what}: {b:0.000} → {a:0.000}");
        }
        Func<byte[], byte[], double> mean = (o, p) => Mean(o, p, Luma);
        Check(new() { Exposure = 1 }, mean, up: true, "exposure +1 brightens");
        Check(new() { Exposure = -1 }, mean, up: false, "exposure −1 darkens");
        Check(new() { Contrast = 0.6 }, (_, p) => Spread(p), up: true, "contrast spreads the tones");
        Check(new() { Contrast = -0.6 }, (_, p) => Spread(p), up: false, "less contrast draws them in");
        Check(new() { Highlights = -0.8 }, (o, p) => Mean(o, p, Luma, 0.6), up: false, "highlights down darken the lights");
        Check(new() { Highlights = 0.8 }, (o, p) => Mean(o, p, Luma, 0.6), up: true, "highlights up brighten them");
        Check(new() { Shadows = 0.8 }, (o, p) => Mean(o, p, Luma, 0, 0.35), up: true, "shadows up lift the darks");
        Check(new() { Shadows = -0.8 }, (o, p) => Mean(o, p, Luma, 0, 0.35), up: false, "shadows down deepen them");
        Check(new() { Whites = 0.8 }, (o, p) => Mean(o, p, Luma, 0.7), up: true, "whites up");
        Check(new() { Blacks = -0.8 }, (o, p) => Mean(o, p, Luma, 0, 0.25), up: false, "blacks down");
        // A hazy picture: every tone squeezed towards a light grey.
        using var hazy = Picture();
        hazy.Evaluate(Channels.RGB, EvaluateOperator.Multiply, 0.5);
        hazy.Evaluate(Channels.RGB, EvaluateOperator.Add, 100);
        var (flat, clear) = Develop(new() { Dehaze = 0.8 }, hazy.Clone());
        Assert.True(Spread(clear) > Spread(flat) * 1.3, $"dehaze adds contrast: {Spread(flat):0.000} → {Spread(clear):0.000}");
        Assert.True(Mean(flat, clear, Luma) < Mean(flat, flat, Luma), "and takes the haze's grey away");
    }

    [Fact]
    public void Colour_moves_the_way_each_slider_says()
    {
        static double Warmth(byte[] rgb, int i) => (rgb[i] - rgb[i + 2]) / 255.0;
        static double Magenta(byte[] rgb, int i) => ((rgb[i] + rgb[i + 2]) / 2.0 - rgb[i + 1]) / 255.0;
        var (before, warm) = Develop(new() { Temperature = 0.8 });
        Assert.True(Mean(before, warm, Warmth) > Mean(before, before, Warmth) + 0.02, "warmer is redder and less blue");
        var (_, cool) = Develop(new() { Temperature = -0.8 });
        Assert.True(Mean(before, cool, Warmth) < Mean(before, before, Warmth) - 0.02, "cooler is bluer");
        var (_, magenta) = Develop(new() { Tint = 0.8 });
        Assert.True(Mean(before, magenta, Magenta) > Mean(before, before, Magenta) + 0.02, "more tint is more magenta");

        var (_, grey) = Develop(new() { Saturation = -1 });
        Assert.True(Mean(before, grey, Chroma) < 0.01, "no saturation is grey");
        var (_, vivid) = Develop(new() { Saturation = 0.6 });
        Assert.True(Mean(before, vivid, Chroma) > Mean(before, before, Chroma) * 1.3, "more saturation, more colour");

        // Vibrance lifts muted colours by a larger share than strong ones.
        var (_, vibrant) = Develop(new() { Vibrance = 0.8 });
        var muted = Pixels(before).Where(i => Chroma(before, i) is > 0.05 and < 0.2).ToList();
        var strong = Pixels(before).Where(i => Chroma(before, i) > 0.5).ToList();
        Assert.NotEmpty(muted);
        Assert.NotEmpty(strong);
        var mutedGain = muted.Average(i => Chroma(vibrant, i)) / muted.Average(i => Chroma(before, i));
        var strongGain = strong.Average(i => Chroma(vibrant, i)) / strong.Average(i => Chroma(before, i));
        Assert.True(mutedGain > strongGain + 0.1, $"muted ×{mutedGain:0.00}, strong ×{strongGain:0.00}");
    }

    /// <summary>How busy a picture is up close: the mean difference between neighbouring pixels.</summary>
    static double Detail(byte[] rgb, int width)
    {
        double sum = 0;
        var count = 0;
        for (var i = 3; i < rgb.Length; i += 3)
        {
            if (i / 3 % width == 0) continue;
            sum += Math.Abs(Luma(rgb, i) - Luma(rgb, i - 3));
            count++;
        }
        return sum / count;
    }

    [Fact]
    public void Detail_and_effects_move_the_way_each_slider_says()
    {
        var (before, sharp) = Develop(new() { Sharpness = 0.8 });
        Assert.True(Detail(sharp, 480) > Detail(before, 480) * 1.15, "sharpening adds edge contrast");
        var (_, clear) = Develop(new() { Clarity = 0.8 });
        Assert.True(Spread(clear) > Spread(before) || Detail(clear, 480) > Detail(before, 480), "clarity adds local contrast");

        using var noisy = new MagickImage(new MagickColor(120, 120, 120), 300, 200);
        noisy.AddNoise(NoiseType.Gaussian, 1.5);
        var (rough, smooth) = Develop(new() { Noise = 0.8 }, noisy);
        Assert.True(Detail(smooth, 300) < Detail(rough, 300) * 0.8, $"noise reduction smooths: {Detail(rough, 300):0.000} → {Detail(smooth, 300):0.000}");

        var (flat, grainy) = Develop(new() { Grain = 0.8 }, new MagickImage(new MagickColor(120, 120, 120), 300, 200));
        Assert.True(Detail(grainy, 300) > Detail(flat, 300) + 0.01, "grain adds texture");

        using var plain = new MagickImage(new MagickColor(150, 150, 150), 400, 300);
        var (even, shaded) = Develop(new() { Vignette = 0.8 }, plain.Clone());
        Assert.True(Luma(shaded, 0) < Luma(even, 0) - 0.1, "a vignette darkens the corners");
        var middle = (150 * 400 + 200) * 3;
        Assert.Equal(Luma(even, middle), Luma(shaded, middle), 2);
        var (_, light) = Develop(new() { Vignette = -0.8 }, plain.Clone());
        Assert.True(Luma(light, 0) > Luma(even, 0) + 0.1, "below 0 it lightens them");
    }

    /// <summary>The distinct levels across a ramp, and the largest jump between neighbouring ones: banding shows as big jumps.</summary>
    static (int Levels, int Jump) Smoothness(IMagickImage<byte> ramp)
    {
        var row = Rgb(ramp).Take((int)ramp.Width * 3).Where((_, i) => i % 3 == 1).Distinct().Order().ToList();
        return (row.Count, row.Zip(row.Skip(1), (a, b) => b - a).DefaultIfEmpty(0).Max());
    }

    /// <summary>A grey ramp from black to white, left to right, every level 4 pixels wide.</summary>
    static MagickImage Ramp(int width, int height)
    {
        var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height);
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                rgb[(y * width + x) * 3] = rgb[(y * width + x) * 3 + 1] = rgb[(y * width + x) * 3 + 2] = (byte)(x * 256 / width);
        image.ReadPixels(rgb, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "RGB"));
        return image;
    }

    [Fact]
    public void A_gradient_keeps_its_smoothness_in_one_pass()
    {
        using var source = Ramp(1024, 4);
        var settings = new PhotoSettings { Exposure = 0.4, Contrast = 0.4, Shadows = 0.5, Highlights = -0.4 };
        using var once = source.Clone();
        new Tone(settings).Apply(once);
        var (levels, jump) = Smoothness(once);

        // The same kind of changes made one 8-bit step after another, rounding each time.
        using var chained = source.Clone();
        chained.Evaluate(Channels.RGB, EvaluateOperator.Multiply, Math.Pow(2, 0.4));
        chained.SigmoidalContrast(3);
        chained.GammaCorrect(1.25);
        chained.Level(new Percentage(0), new Percentage(92));
        var (chainedLevels, chainedJump) = Smoothness(chained);

        Assert.True(jump <= 3, $"the largest step between levels is {jump}");
        Assert.True(levels >= chainedLevels, $"{levels} levels in one pass, {chainedLevels} chained");
        Assert.True(jump < chainedJump, $"a step of {jump} in one pass, {chainedJump} chained");
    }

    [Fact]
    public async Task The_copy_keeps_its_metadata_and_colour_profile()
    {
        using var scratch = new Scratch();
        var path = scratch.PathOf("photo.jpg");
        using (var image = Picture(300, 200))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.Make, "Tanjelo Camera Co");
            image.SetProfile(exif);
            image.SetProfile(ColorProfiles.AdobeRGB1998);
            image.Write(path);
        }
        var engine = Env.Engine();
        var target = Env.Offered(engine, "adjust", [path]).WithParameters(new Dictionary<string, string> { ["exposure"] = "0.5", ["vibrance"] = "30" });
        var result = await engine.RunAsync([path], target, ct: TestContext.Current.CancellationToken);
        var output = Assert.Single(result.Outputs);
        Assert.Equal("photo-edited.jpg", Path.GetFileName(output));
        using var edited = new MagickImage(output);
        Assert.Equal("Tanjelo Camera Co", edited.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
        Assert.Equal(ColorProfiles.AdobeRGB1998.ToByteArray(), edited.GetColorProfile()?.ToByteArray());

        var unchanged = await engine.RunAsync([path], Env.Offered(engine, "adjust", [path]), ct: TestContext.Current.CancellationToken);
        Assert.Equal("Every setting is at 0, so the photo would come out the same.", Assert.Single(unchanged.Failures).Message);
    }

    [Fact]
    public void Settings_go_to_parameters_and_back()
    {
        var settings = new PhotoSettings { Exposure = 0.35, Contrast = 0.2, Vignette = -0.5, Grain = 0.1 };
        var target = Env.Offered(Env.Engine(), "adjust", [Path.Combine(Path.GetTempPath(), "x.jpg")]).WithParameters(settings.ToParameters());
        Assert.Equal(settings, PhotoSettings.From(target));
    }

    [Fact]
    public void The_light_and_colour_pass_is_quick_enough_for_a_live_preview()
    {
        using var image = Picture(1600, 1067);
        var settings = new PhotoSettings { Exposure = 0.3, Contrast = 0.2, Shadows = 0.3, Vibrance = 0.3, Vignette = 0.3 };
        new Tone(settings).Apply(image.Clone()); // warm up
        var watch = System.Diagnostics.Stopwatch.StartNew();
        new Tone(settings).Apply(image);
        TestContext.Current.TestOutputHelper?.WriteLine($"1600 × 1067: {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.ElapsedMilliseconds < 400, $"took {watch.ElapsedMilliseconds} ms");
    }
}

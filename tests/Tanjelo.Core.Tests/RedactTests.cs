using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class RedactTests
{
    [Fact]
    public void Areas_are_read_and_written_as_x_y_width_height()
    {
        var boxes = RedactBox.ParseAll(" 10,20,30,40 ; -5,0,8,9;");
        Assert.Equal([new RedactBox(10, 20, 30, 40), new RedactBox(-5, 0, 8, 9)], boxes);
        Assert.Equal("10,20,30,40;-5,0,8,9", RedactBox.FormatAll(boxes));
        Assert.Empty(RedactBox.ParseAll(""));
        Assert.Equal("Couldn't read '1,2,3' as an area. Write each as x,y,width,height in pixels, such as 10,20,200,80.",
            Assert.Throws<ConversionException>(() => RedactBox.ParseAll("1,2,3")).Message);
        Assert.Throws<ConversionException>(() => RedactBox.ParseAll("1,2,0,4"));
        Assert.Equal(new RedactBox(0, 0, 5, 9), new RedactBox(-5, 0, 10, 9).Within(100, 100));
        Assert.Null(new RedactBox(200, 0, 10, 9).Within(100, 100));
    }

    static async Task<(string Output, BatchResult Result)> Redact(string input, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "redact", [input]).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        var result = await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return (Assert.Single(result.Outputs), result);
    }

    static bool Same(IPixelCollection<byte> a, IPixelCollection<byte> b, int x, int y) =>
        a.GetPixel(x, y).ToColor()!.Equals(b.GetPixel(x, y).ToColor());

    [Theory]
    [InlineData("solid")]
    [InlineData("pixelate")]
    [InlineData("blur")]
    public async Task Only_the_pixels_inside_each_area_change(string style)
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.png", 400, 300);
        var (output, _) = await Redact(photo, ("boxes", "50,40,120,80;300,200,60,60"), ("style", style), ("colour", "#2050E0"));
        Assert.Equal("photo-redacted.png", Path.GetFileName(output));
        using var before = new MagickImage(photo);
        using var after = new MagickImage(output);
        using var b = before.GetPixels();
        using var a = after.GetPixels();
        int inside = 0, changed = 0;
        for (var y = 0; y < 300; y += 3)
            for (var x = 0; x < 400; x += 3)
            {
                var within = (x is >= 50 and < 170 && y is >= 40 and < 120) || (x >= 300 && y >= 200 && y < 260 && x < 360);
                if (!within)
                {
                    Assert.True(Same(a, b, x, y), $"({x}, {y}) is outside the areas but changed");
                    continue;
                }
                inside++;
                if (!Same(a, b, x, y)) changed++;
                if (style == "solid") Assert.Equal(new MagickColor("#2050E0"), a.GetPixel(x, y).ToColor());
            }
        Assert.True(changed > inside * 0.95, $"only {changed} of {inside} pixels inside the areas changed");
        if (style == "pixelate")
        {
            // Flat blocks: the area holds only a handful of colours.
            var colours = Enumerable.Range(50, 120).SelectMany(x => Enumerable.Range(40, 80).Select(y => a.GetPixel(x, y).ToColor()!.ToString())).Distinct().Count();
            Assert.InRange(colours, 2, 40);
        }
    }

    /// <summary>A JPEG with what a phone puts in one: an EXIF thumbnail, the location, XMP and the camera.</summary>
    static string Tagged(Scratch scratch)
    {
        var path = scratch.PathOf("tagged.jpg");
        static BitmapSource Solid(int width, int height, byte r, byte g, byte b)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2]) = (b, g, r);
            return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        }
        var metadata = new BitmapMetadata("jpg") { CameraManufacturer = "Tanjelo Camera Co", Title = "Where we met" };
        metadata.SetQuery("/app1/ifd/gps/{ushort=1}", "N");
        metadata.SetQuery("/app1/ifd/gps/{ushort=2}", new ulong[] { 1UL << 32 | 51, 1UL << 32 | 30, 1UL << 32 | 0 });
        metadata.SetQuery("/xmp/{wstr=http://ns.adobe.com/exif/1.0/}:GPSLatitude", "51,30.0N");
        var frame = BitmapFrame.Create(Solid(320, 240, 200, 60, 40), Solid(160, 120, 10, 240, 10), metadata, null);
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(frame);
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
    }

    static int Starts(byte[] file) => Enumerable.Range(0, file.Length - 2).Count(i => file[i] == 0xFF && file[i + 1] == 0xD8 && file[i + 2] == 0xFF);

    [Fact]
    public async Task The_copy_loses_its_thumbnail_location_and_XMP_but_keeps_the_camera()
    {
        using var scratch = new Scratch();
        var photo = Tagged(scratch);
        using (var original = new MagickImage(photo))
        {
            var exif = original.GetExifProfile()!;
            Assert.NotNull(exif.CreateThumbnail());
            Assert.Contains(exif.Values, v => v.Tag == ExifTag.GPSLatitude);
            Assert.NotNull(original.GetXmpProfile());
        }
        Assert.Equal(2, Starts(File.ReadAllBytes(photo))); // the picture and its thumbnail

        var (output, _) = await Redact(photo, ("boxes", "0,0,40,40"));
        using var redacted = new MagickImage(output);
        var kept = redacted.GetExifProfile();
        Assert.NotNull(kept);
        Assert.Null(kept.CreateThumbnail());
        Assert.DoesNotContain(kept.Values, v => v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal));
        Assert.Equal("Tanjelo Camera Co", kept.GetValue(ExifTag.Make)?.Value);
        Assert.Null(redacted.GetXmpProfile());
        Assert.Null(redacted.GetIptcProfile());
        var bytes = File.ReadAllBytes(output);
        Assert.Equal(1, Starts(bytes)); // no second picture hiding in the file
    }

    [Fact]
    public async Task Text_found_by_OCR_is_hidden()
    {
        Assert.SkipUnless(Env.Tools.Ocr, "No OCR language is installed");
        using var scratch = new Scratch();
        var picture = scratch.TextPicture("note.png", 900, 300, "Account 4417 Password swordfish");
        var (output, result) = await Redact(picture, ("text", "true"));
        Assert.Contains(result.Notes, n => n.EndsWith(" words found", StringComparison.Ordinal));
        // Where the words were is now solid black, with white left between the lines' ends.
        using var before = new MagickImage(picture);
        using var after = new MagickImage(output);
        using var b = before.GetPixels();
        using var a = after.GetPixels();
        var ink = 0;
        var hidden = 0;
        for (var y = 0; y < 300; y += 2)
            for (var x = 0; x < 900; x += 2)
                if (b.GetPixel(x, y).GetChannel(0) < 100)
                {
                    ink++;
                    if (a.GetPixel(x, y).ToColor()!.Equals(MagickColors.Black)) hidden++;
                }
        Assert.True(ink > 50, "the fixture should have text");
        Assert.True(hidden > ink * 0.97, $"{ink - hidden} of {ink} text pixels are still showing");
        Assert.Equal(new MagickColor("#FFFFFF"), a.GetPixel(880, 290).ToColor()); // the margin is untouched
    }

    [Fact]
    public async Task Faces_are_found_and_hidden()
    {
        Assert.SkipUnless(Redaction.CanFindFaces, "Windows can't look for faces on this PC");
        using var scratch = new Scratch();
        var portrait = scratch.PathOf("portrait.jpg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portrait.jpg"), portrait);
        var (output, result) = await Redact(portrait, ("faces", "true"), ("colour", "#00FF00"));
        Assert.Equal(["1 face found"], result.Notes);
        using var after = new MagickImage(output);
        using var pixels = after.GetPixels();
        // The middle of the face (between the eyes and the mouth) is covered.
        var green = pixels.GetPixel(230, 170).ToColor()!;
        Assert.True(green.G > 230 && green.R < 30 && green.B < 30, $"the face's middle is {green}");
    }

    [Fact]
    public async Task Nothing_to_redact_and_areas_off_the_picture_are_errors()
    {
        using var scratch = new Scratch();
        var photo = scratch.Image("plain.png", 100, 80);
        var engine = Env.Engine();
        var target = Env.Offered(engine, "redact", [photo]);
        var nothing = await engine.RunAsync([photo], target, ct: TestContext.Current.CancellationToken);
        Assert.Equal("There's nothing to redact: give the areas, or find faces or text.", Assert.Single(nothing.Failures).Message);
        var outside = await engine.RunAsync([photo], target.WithParameters(new Dictionary<string, string> { ["boxes"] = "10,10,5,5;500,10,5,5" }),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal("The area 500,10,5,5 is outside the 100 × 80 picture.", Assert.Single(outside.Failures).Message);
        Assert.Equal(["plain.png"], scratch.Files());
    }
}

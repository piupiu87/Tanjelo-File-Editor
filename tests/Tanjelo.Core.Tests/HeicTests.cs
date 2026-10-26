using ImageMagick;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class HeicTests
{
    const string NoHeic = "Windows can't write HEIC here (the HEIF and HEVC extensions aren't installed)";

    [Fact]
    public void Heic_is_offered_only_when_windows_can_write_it()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.jpg", 64, 48);
        var without = new ConverterEngine(new Toolbox { Heic = false }).GetMenu([photo]);
        var with = new ConverterEngine(new Toolbox { Heic = true }).GetMenu([photo]);

        Assert.DoesNotContain(without.Targets, t => t.Key == "heic");
        Assert.Contains(HeicEncoder.Missing, without.Hints);
        Assert.Contains(with.Targets, t => t.Key == "heic");
        Assert.DoesNotContain(HeicEncoder.Missing, with.Hints);
        Assert.Equal(Env.Tools.Heic, Env.Keys(photo).Contains("heic"));
    }

    [Fact]
    public void An_animated_gif_says_only_its_first_frame_is_kept()
    {
        using var scratch = new Scratch();
        var gif = scratch.AnimatedGif("spin.gif");

        var heic = new ConverterEngine(new Toolbox { Heic = true }).GetMenu([gif]).Targets.Single(t => t.Key == "heic");

        Assert.Equal("First frame only, for iPhone and Mac", heic.Description);
    }

    [Fact]
    public async Task A_photo_becomes_a_heic_that_reads_back_at_its_size()
    {
        Assert.SkipUnless(Env.Tools.Heic, NoHeic);
        using var scratch = new Scratch();
        var output = await Env.RunOne("heic", scratch.Photo("photo.jpg", 640, 360));

        Assert.Equal(scratch.PathOf("photo.heic"), output);
        var info = Inspect.Image(output);
        Assert.Equal(MagickFormat.Heic, info.Format);
        Assert.Equal((640u, 360u), (info.Width, info.Height));
    }

    [Fact]
    public async Task A_sideways_photo_is_turned_upright()
    {
        Assert.SkipUnless(Env.Tools.Heic, NoHeic);
        using var scratch = new Scratch();
        var output = await Env.RunOne("heic", scratch.Sideways("portrait.jpg", 120, 80));

        var info = Inspect.Image(output);
        Assert.Equal((80u, 120u), (info.Width, info.Height));
    }

    [Fact]
    public async Task The_date_taken_the_camera_and_the_location_are_kept()
    {
        Assert.SkipUnless(Env.Tools.Heic, NoHeic);
        using var scratch = new Scratch();
        var jpg = scratch.PathOf("trip.jpg");
        using (var image = new MagickImage("plasma:", 200, 150))
        {
            var exif = new ExifProfile();
            exif.SetValue(ExifTag.DateTimeOriginal, "2024:05:06 07:08:09");
            exif.SetValue(ExifTag.Make, "Contoso");
            exif.SetValue(ExifTag.Model, "Snapper 3");
            exif.SetValue(ExifTag.GPSLatitude, [new Rational(52, 1), new Rational(22, 1), new Rational(3036, 100)]);
            exif.SetValue(ExifTag.GPSLatitudeRef, "S");
            exif.SetValue(ExifTag.GPSLongitude, [new Rational(4, 1), new Rational(54, 1), new Rational(1476, 100)]);
            exif.SetValue(ExifTag.GPSLongitudeRef, "W");
            exif.SetValue(ExifTag.GPSAltitude, new Rational(125, 10));
            image.SetProfile(exif);
            image.Write(jpg);
        }

        var output = await Env.RunOne("heic", jpg);

        // Read back the way File Explorer and Photos do, through the shell's properties.
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(output);
        var properties = await file.Properties.GetImagePropertiesAsync();
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), properties.DateTaken.LocalDateTime);
        Assert.Equal("Contoso", properties.CameraManufacturer);
        Assert.Equal("Snapper 3", properties.CameraModel);
        Assert.Equal(-(52 + 22 / 60.0 + 30.36 / 3600), properties.Latitude!.Value, 5);
        Assert.Equal(-(4 + 54 / 60.0 + 14.76 / 3600), properties.Longitude!.Value, 5);
        // The EXIF itself, as ImageMagick (and so most other apps) reads it.
        using var back = new MagickImage(output);
        var exifBack = back.GetExifProfile()!;
        Assert.Equal("S", exifBack.GetValue(ExifTag.GPSLatitudeRef)!.Value);
        Assert.Equal(12.5, exifBack.GetValue(ExifTag.GPSAltitude)!.Value.ToDouble(), 2);
    }

    [Fact]
    public async Task A_photo_without_a_location_gets_none()
    {
        Assert.SkipUnless(Env.Tools.Heic, NoHeic);
        using var scratch = new Scratch();
        var output = await Env.RunOne("heic", scratch.Photo("plain.jpg", 120, 90));

        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(output);
        var properties = await file.Properties.GetImagePropertiesAsync();
        Assert.Null(properties.Latitude);
    }

    [Fact]
    public async Task Transparency_is_filled_with_white()
    {
        Assert.SkipUnless(Env.Tools.Heic, NoHeic);
        using var scratch = new Scratch();
        var output = await Env.RunOne("heic", scratch.TransparentPng("logo.png", 64));

        using var image = new MagickImage(output);
        var corner = image.GetPixels().GetPixel(0, 0).ToColor()!;
        Assert.True(corner.R > 240 && corner.G > 240 && corner.B > 240, $"corner is {corner}");
    }

    [Fact]
    public async Task An_animated_gif_keeps_its_first_frame()
    {
        Assert.SkipUnless(Env.Tools.Heic, NoHeic);
        using var scratch = new Scratch();
        var output = await Env.RunOne("heic", scratch.AnimatedGif("spin.gif"));

        Assert.Equal((32u, 32u), (Inspect.Image(output).Width, Inspect.Image(output).Height));
    }
}

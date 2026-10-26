using System.Text;
using ImageMagick;
using PdfSharp.Pdf.IO;

namespace Tanjelo.Core.Tests;

public class MetadataToolTests
{
    const string XmpPacket = """
        <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
        <rdf:Description rdf:about="" xmlns:exif="http://ns.adobe.com/exif/1.0/" xmlns:tiff="http://ns.adobe.com/tiff/1.0/"
            xmlns:dc="http://purl.org/dc/elements/1.1/" exif:GPSLatitude="52,22.0N" tiff:Make="Canon">
          <exif:GPSLongitude>4,54.0E</exif:GPSLongitude>
          <dc:title><rdf:Alt><rdf:li xml:lang="x-default">Old title</rdf:li></rdf:Alt></dc:title>
        </rdf:Description></rdf:RDF></x:xmpmeta>
        """;

    /// <summary>A photo as a phone would save it: camera, time, GPS position, an XMP copy, and turned sideways.</summary>
    static string PhonePhoto(Scratch scratch, string name, bool xmp = true)
    {
        var path = scratch.PathOf(name);
        using var image = new MagickImage("plasma:", 120, 80);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Canon");
        exif.SetValue(ExifTag.Model, "EOS R5");
        exif.SetValue(ExifTag.DateTimeOriginal, "2024:07:01 14:03:00");
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(52, 1), new Rational(22, 1), new Rational(3, 1)]);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(4, 1), new Rational(54, 1), new Rational(0, 1)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        image.SetProfile(exif);
        image.Orientation = Orientation.RightTop; // ImageMagick writes this into the EXIF over the value set above
        if (xmp) image.SetProfile(new XmpProfile(Encoding.UTF8.GetBytes(XmpPacket)));
        image.Write(path);
        return path;
    }

    static async Task<string> Edit(string input, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "metadata", [input]).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        var result = await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    static IExifProfile Exif(string path)
    {
        using var image = new MagickImage();
        image.Read(path); // a ping misses WebP metadata
        return image.GetExifProfile() ?? new ExifProfile();
    }

    static string? Xmp(string path)
    {
        using var image = new MagickImage();
        image.Read(path); // a ping misses WebP metadata
        return image.GetXmpProfile() is { } xmp ? Encoding.UTF8.GetString(xmp.ToByteArray()) : null;
    }

    /// <summary>A JPEG's compressed picture: everything from the start-of-scan marker on.</summary>
    static byte[] Scan(string jpeg)
    {
        var bytes = File.ReadAllBytes(jpeg);
        for (var i = 2; i < bytes.Length - 1; i++)
            if (bytes[i] == 0xFF && bytes[i + 1] == 0xDA) return bytes[i..];
        throw new InvalidDataException("no scan");
    }

    [Fact]
    public async Task Removing_the_location_keeps_the_camera_and_the_exact_picture()
    {
        using var scratch = new Scratch();
        var photo = PhonePhoto(scratch, "photo.jpg");

        var output = await Edit(photo);

        Assert.Equal(scratch.PathOf("photo-edited.jpg"), output);
        var exif = Exif(output);
        Assert.Null(exif.GetValue(ExifTag.GPSLatitude));
        Assert.Null(exif.GetValue(ExifTag.GPSLongitudeRef));
        Assert.Equal("Canon", exif.GetValue(ExifTag.Make)?.Value);
        Assert.Equal((ushort)6, exif.GetValue(ExifTag.Orientation)?.Value);
        var xmp = Xmp(output)!;
        Assert.DoesNotContain("GPS", xmp);
        Assert.Contains("Old title", xmp);
        Assert.Equal(Scan(photo), Scan(output));
        Assert.Contains("GPSLatitude", Encoding.Latin1.GetString(File.ReadAllBytes(photo))); // the original is untouched
    }

    [Fact]
    public async Task Removing_camera_details_takes_them_out_of_exif_and_xmp()
    {
        using var scratch = new Scratch();
        var output = await Edit(PhonePhoto(scratch, "photo.jpg"), ("location", "false"), ("camera", "true"));

        Assert.Null(Exif(output).GetValue(ExifTag.Make));
        Assert.Null(Exif(output).GetValue(ExifTag.Model));
        Assert.NotNull(Exif(output).GetValue(ExifTag.GPSLatitude));
        Assert.DoesNotContain("Canon", Xmp(output)!);
    }

    [Fact]
    public async Task A_new_title_and_author_go_into_exif_and_replace_the_xmp_title()
    {
        using var scratch = new Scratch();
        var output = await Edit(PhonePhoto(scratch, "photo.jpg"), ("title", "Beach day ☀"), ("author", "Jo"), ("location", "false"));

        var exif = Exif(output);
        Assert.Equal("Beach day ☀", Encoding.Unicode.GetString(exif.GetValue(ExifTag.XPTitle)!.Value).TrimEnd('\0'));
        Assert.Equal("Jo", exif.GetValue(ExifTag.Artist)?.Value);
        Assert.Contains("Beach day ☀", Xmp(output)!);
        Assert.DoesNotContain("Old title", Xmp(output)!);
    }

    [Fact]
    public async Task Removing_everything_keeps_only_the_orientation()
    {
        using var scratch = new Scratch();
        var output = await Edit(PhonePhoto(scratch, "photo.jpg"), ("all", "true"), ("author", "Jo"));

        var exif = Exif(output);
        Assert.Equal(["Artist", "Orientation", "XPAuthor"], exif.Values.Select(v => v.Tag.ToString()).Order());
        Assert.Null(Xmp(output));
    }

    [Fact]
    public async Task Png_text_notes_and_exif_are_edited_without_touching_the_pixels()
    {
        using var scratch = new Scratch();
        var photo = PhonePhoto(scratch, "photo.png", xmp: false);

        var output = await Edit(photo, ("title", "Map"), ("author", "Jo"));

        Assert.Null(Exif(output).GetValue(ExifTag.GPSLatitude));
        using var image = new MagickImage(output);
        Assert.Equal("Map", image.GetAttribute("Title"));
        Assert.Equal("Jo", image.GetAttribute("Author"));
        using var original = new MagickImage(photo);
        Assert.Equal(0, original.Compare(image, ErrorMetric.Absolute));
    }

    [Fact]
    public async Task A_simple_webp_gets_the_extended_header_it_needs_for_metadata()
    {
        using var scratch = new Scratch();
        var path = scratch.PathOf("sticker.webp");
        using (var image = new MagickImage("plasma:", 90, 60)) image.Write(path);
        Assert.DoesNotContain("VP8X", Encoding.ASCII.GetString(File.ReadAllBytes(path)));

        var output = await Edit(path, ("title", "Sticker"));

        Assert.Contains("VP8X", Encoding.ASCII.GetString(File.ReadAllBytes(output)));
        Assert.Equal("Sticker", Exif(output).GetValue(ExifTag.ImageDescription)?.Value);
        var info = Inspect.Image(output);
        Assert.Equal((90u, 60u), (info.Width, info.Height));
    }

    [Fact]
    public async Task Nothing_to_remove_and_nothing_new_is_reported_rather_than_copied()
    {
        using var scratch = new Scratch();
        var plain = scratch.Image("plain.png");
        var engine = Env.Engine();

        var result = await engine.RunAsync([plain], Env.Offered(engine, "metadata", [plain]), ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("There's nothing to change", Assert.Single(result.Failures).Message);
        Assert.Equal(["plain.png"], scratch.Files());
    }

    [Fact]
    public async Task The_preview_lists_the_metadata_and_what_will_change()
    {
        using var scratch = new Scratch();
        var photo = PhonePhoto(scratch, "photo.jpg");
        var engine = Env.Engine();
        var target = Env.Offered(engine, "metadata", [photo]).WithParameters(new Dictionary<string, string> { ["title"] = "Beach" });

        var preview = await engine.PreviewAsync([photo], target, new PreviewRequest(100), TestContext.Current.CancellationToken);

        Assert.NotNull(preview!.Image);
        Assert.Contains(new Fact("Title", "Old title → Beach"), preview.Facts);
        Assert.Contains(new Fact("Camera", "Canon EOS R5"), preview.Facts);
        Assert.Contains(new Fact("Location", "52.3675° N, 4.9° E → removed"), preview.Facts);
        Assert.Contains(new Fact("Taken", "2024:07:01 14:03:00"), preview.Facts);
    }

    [Fact]
    public async Task Pdf_information_is_replaced_or_cleared()
    {
        using var scratch = new Scratch();
        var pdf = scratch.Pdf("report.pdf");
        using (var document = PdfReader.Open(pdf, PdfDocumentOpenMode.Modify))
        {
            document.Info.Subject = "Secret";
            document.Save(pdf);
        }

        var titled = await Edit(pdf, ("title", "Report"), ("author", "Jo"));
        var cleared = await Edit(pdf, ("all", "true"));

        using (var document = PdfReader.Open(titled, PdfDocumentOpenMode.Import))
            Assert.Equal(("Report", "Jo", "Secret"), (document.Info.Title, document.Info.Author, document.Info.Subject));
        using (var document = PdfReader.Open(cleared, PdfDocumentOpenMode.Import))
            Assert.Equal(("", "", "Tanjelo"), (document.Info.Title, document.Info.Subject, document.Info.Creator));
        Assert.Equal(1, Inspect.PdfPages(cleared));
    }

    [Fact]
    public async Task Audio_tags_are_set_and_removed_without_re_encoding()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var song = scratch.Ffmpeg("song.mp3", ["-f", "lavfi", "-i", "sine=duration=2", "-metadata", "title=Draft", "-metadata", "album=Demos", "-c:a", "libmp3lame"]);

        var titled = await Edit(song, ("title", "Final"), ("author", "Jo"));
        var cleared = await Edit(song, ("all", "true"));

        Assert.Equal(("Final", "Jo", "Demos"), (Inspect.Tag(titled, "title"), Inspect.Tag(titled, "artist"), Inspect.Tag(titled, "album")));
        Assert.Null(Inspect.Tag(cleared, "title"));
        Assert.Null(Inspect.Tag(cleared, "album"));
        Assert.Equal([("audio", "mp3")], Inspect.Media(cleared).Streams);
    }

    [Fact]
    public async Task A_videos_location_is_removed_and_its_streams_copied()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("clip.mp4");
        var located = scratch.Ffmpeg("trip.mp4", ["-i", clip, "-c", "copy", "-metadata", "location=+52.3676+004.9041/", "-metadata", "title=Trip"]);
        Assert.NotNull(Inspect.Tag(located, "location"));

        var engine = Env.Engine();
        var preview = await engine.PreviewAsync([located], Env.Offered(engine, "metadata", [located]), new PreviewRequest(100), TestContext.Current.CancellationToken);
        var output = await Edit(located);

        Assert.Contains(new Fact("Location", "52.3676° N, 4.9041° E → removed"), preview!.Facts);
        Assert.Null(Inspect.Tag(output, "location"));
        Assert.Equal("Trip", Inspect.Tag(output, "title"));
        Assert.Equal(Inspect.Media(located).Streams, Inspect.Media(output).Streams);
    }
}

using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using ImageMagick;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Writes HEIC with the HEIF encoder built into Windows, which works once the HEIF Image Extensions and the HEVC Video
/// Extensions are installed from the Microsoft Store. ImageMagick reads the picture and turns it upright; Windows
/// encodes it, with the date taken, the camera and the location carried over.
/// </summary>
internal static class HeicEncoder
{
    public static string Missing => Strings.HEICOutputNeedsTheHEIFImageExtensions;

    // One tiny trial encode, the first time it's asked, tells whether the extensions are there.
    static readonly Lazy<bool> Works = new(() => Task.Run(TrialAsync).GetAwaiter().GetResult());

    public static bool Available => Works.Value;

    static async Task<bool> TrialAsync()
    {
        try
        {
            using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, 16, 16, BitmapAlphaMode.Ignore);
            using var stream = new InMemoryRandomAccessStream();
            await EncodeAsync(bitmap, stream, 0.5, null);
            return stream.Size > 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // usually "no imaging component suitable"
        }
    }

    /// <param name="quality">1 to 100, like JPEG.</param>
    public static async Task WriteAsync(IMagickImage<byte> image, string output, int quality)
    {
        var exif = image.GetExifProfile();
        image.AutoOrient();
        if (image.ColorSpace == ColorSpace.CMYK) image.TransformColorSpace(ColorProfiles.SRGB);
        else if (image.GetColorProfile() is { } profile && !(profile.Description ?? "").Contains("sRGB", StringComparison.OrdinalIgnoreCase))
            image.TransformColorSpace(profile, ColorProfiles.SRGB); // HEIC is read as sRGB, so a wide-gamut photo is converted
        if (image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }

        using var bitmap = Bitmap(image);
        using var encoded = new InMemoryRandomAccessStream();
        await EncodeAsync(bitmap, encoded, quality / 100.0, exif);
        encoded.Seek(0);
        await using var file = File.Create(output);
        await encoded.AsStreamForRead().CopyToAsync(file);
    }

    static async Task EncodeAsync(SoftwareBitmap bitmap, IRandomAccessStream stream, double quality, IExifProfile? exif)
    {
        var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue((float)quality, PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.HeifEncoderId, stream, options);
        encoder.SetSoftwareBitmap(bitmap);
        if (exif is not null && Properties(exif) is { Count: > 0 } properties)
            await encoder.BitmapProperties.SetPropertiesAsync(properties);
        await encoder.FlushAsync();
    }

    /// <summary>An opaque picture as a Windows bitmap, 8-bit BGRA, for Windows' own encoders and OCR.</summary>
    internal static SoftwareBitmap Bitmap(IMagickImage<byte> image)
    {
        var pixels = image.ToByteArray(MagickFormat.Bgra);
        return SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, (int)image.Width, (int)image.Height, BitmapAlphaMode.Ignore);
    }

    /// <summary>The date taken, the camera and the location, as Windows' photo properties.</summary>
    internal static BitmapPropertySet Properties(IExifProfile exif)
    {
        var properties = new BitmapPropertySet();
        if ((exif.GetValue(ExifTag.DateTimeOriginal)?.Value ?? exif.GetValue(ExifTag.DateTime)?.Value) is { } text
            && DateTime.TryParseExact(text.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var taken))
            properties["System.Photo.DateTaken"] = new BitmapTypedValue(new DateTimeOffset(taken), PropertyType.DateTime);
        if (exif.GetValue(ExifTag.Make)?.Value?.Trim() is { Length: > 0 } make)
            properties["System.Photo.CameraManufacturer"] = new BitmapTypedValue(make, PropertyType.String);
        if (exif.GetValue(ExifTag.Model)?.Value?.Trim() is { Length: > 0 } model)
            properties["System.Photo.CameraModel"] = new BitmapTypedValue(model, PropertyType.String);

        // The HEIF encoder refuses System.GPS.Latitude, but takes the EXIF GPS tags themselves, copied exactly. Explorer
        // and Photos read them back like a JPEG's (WIC's own System.GPS.Latitude doesn't, once the N/S tag is there).
        if (exif.GetValue(ExifTag.GPSLatitude)?.Value is { Length: 3 } latitude && exif.GetValue(ExifTag.GPSLatitudeRef)?.Value is { } north
            && exif.GetValue(ExifTag.GPSLongitude)?.Value is { Length: 3 } longitude && exif.GetValue(ExifTag.GPSLongitudeRef)?.Value is { } east)
        {
            properties["/ifd/gps/{ushort=0}"] = new BitmapTypedValue(new byte[] { 2, 3, 0, 0 }, PropertyType.UInt8Array); // GPSVersionID
            properties["/ifd/gps/{ushort=1}"] = new BitmapTypedValue(north, PropertyType.String);
            properties["/ifd/gps/{ushort=2}"] = new BitmapTypedValue(latitude.Select(Rational).ToArray(), PropertyType.UInt64Array);
            properties["/ifd/gps/{ushort=3}"] = new BitmapTypedValue(east, PropertyType.String);
            properties["/ifd/gps/{ushort=4}"] = new BitmapTypedValue(longitude.Select(Rational).ToArray(), PropertyType.UInt64Array);
            if (exif.GetValue(ExifTag.GPSAltitude)?.Value is { } altitude)
            {
                properties["/ifd/gps/{ushort=5}"] = new BitmapTypedValue(exif.GetValue(ExifTag.GPSAltitudeRef)?.Value ?? 0, PropertyType.UInt8);
                properties["/ifd/gps/{ushort=6}"] = new BitmapTypedValue(Rational(altitude), PropertyType.UInt64);
            }
        }
        return properties;
    }

    /// <summary>WIC keeps an EXIF rational in 64 bits: the numerator in the low half, the denominator in the high half.</summary>
    static ulong Rational(ImageMagick.Rational value) => ((ulong)value.Denominator << 32) | value.Numerator;
}

using System.Buffers.Binary;
using System.Numerics;
using ImageMagick;

namespace Tanjelo.Core;

/// <summary>
/// What was on the clipboard, copied out of it at once so it's held open as briefly as possible. Only what Tanjelo can
/// use is copied: the files, a PNG, or the bitmap the app put there itself.
/// </summary>
public sealed record ClipboardData
{
    public const uint CF_BITMAP = 2, CF_DIB = 8, CF_PALETTE = 9, CF_HDROP = 15, CF_DIBV5 = 17;

    public static ClipboardData Empty { get; } = new();

    /// <summary>Copied files and folders (CF_HDROP), as Explorer's Copy puts them there.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>The registered "PNG" format, which holds a PNG file as it is: Snipping Tool, browsers and Office put one there.</summary>
    public byte[]? Png { get; init; }

    /// <summary>A packed bitmap, CF_DIBV5 or CF_DIB: whichever the app put there itself (see <see cref="OwnBitmap"/>).</summary>
    public byte[]? Bitmap { get; init; }

    /// <summary>
    /// Of CF_DIB and CF_DIBV5, the one listed first, given the formats in the order the clipboard lists them; 0 when
    /// there's neither. Windows makes up the other and lists it after the app's own, and its DIB-to-DIBV5 conversion
    /// has been seen to spoil pictures, so the one it made isn't trusted.
    /// </summary>
    public static uint OwnBitmap(IEnumerable<uint> formats) => formats.FirstOrDefault(f => f is CF_DIB or CF_DIBV5);
}

/// <summary>What the clipboard offers: copied files and folders, a picture, or neither, with the reason when there is one.</summary>
public sealed record ClipboardContent(IReadOnlyList<string> Files, ClipboardPicture? Picture, string? Problem = null)
{
    public static ClipboardContent Nothing { get; } = new([], null);

    public bool IsEmpty => Files.Count == 0 && Picture is null;
}

/// <summary>
/// A picture from the clipboard, as the PNG file it's saved as. A PNG on the clipboard is kept byte for byte; a bitmap
/// is decoded here, transparency and all, rather than by WPF, whose Clipboard.GetImage drops the alpha channel and
/// misreads bitmaps with colour masks.
/// </summary>
public sealed class ClipboardPicture
{
    /// <summary>Larger pictures are refused: 250 megapixels is a gigabyte once decoded.</summary>
    public const long MaxPixels = 250_000_000;

    ClipboardPicture(byte[] png, int width, int height) => (Png, Width, Height) = (png, width, height);

    /// <summary>The PNG file's bytes.</summary>
    public byte[] Png { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Copied files and folders that still exist come first, then a PNG, then the app's own bitmap.</summary>
    public static ClipboardContent Choose(ClipboardData data)
    {
        var files = data.Files.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (files.Count > 0) return new ClipboardContent(files, null);
        try
        {
            var picture = (data.Png is { } png ? FromPng(png) : null) ?? (data.Bitmap is { } dib ? FromBitmap(dib) : null);
            return picture is null ? ClipboardContent.Nothing : new ClipboardContent([], picture);
        }
        catch (ConversionException ex)
        {
            return ClipboardContent.Nothing with { Problem = ex.Message };
        }
    }

    static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Whether the bytes start as a PNG file does: the signature, then the IHDR chunk with the size.</summary>
    public static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 33 && bytes[..8].SequenceEqual(Signature) && bytes.Slice(12, 4).SequenceEqual("IHDR"u8);

    /// <summary>
    /// A PNG file's bytes as they are, with the size from its header; null when they aren't a PNG. Throws
    /// <see cref="ConversionException"/> when it's larger than <see cref="MaxPixels"/>.
    /// </summary>
    public static ClipboardPicture? FromPng(byte[] bytes)
    {
        if (!IsPng(bytes)) return null;
        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20));
        if (width <= 0 || height <= 0) return null;
        CheckSize(width, height);
        return new ClipboardPicture(bytes, width, height);
    }

    /// <summary>
    /// A packed bitmap (CF_DIB or CF_DIBV5) as a PNG; null when it's damaged or in a form clipboards don't carry. Throws
    /// <see cref="ConversionException"/> when it's larger than <see cref="MaxPixels"/>.
    /// </summary>
    public static ClipboardPicture? FromBitmap(byte[] dib)
    {
        if (Decode(dib) is not var (pixels, width, height, opaque)) return null;
        return new ClipboardPicture(EncodePng(pixels, width, height, opaque), width, height);
    }

    static void CheckSize(int width, int height)
    {
        if ((long)width * height > MaxPixels)
            throw new ConversionException(string.Format(Strings.ThePictureOnTheClipboardIsTooLarge, width, height));
    }

    const int BI_RGB = 0, BI_BITFIELDS = 3, BI_ALPHABITFIELDS = 6;

    /// <summary>
    /// A bitmap's pixels as straight BGRA, top row first, and whether all of them are opaque; null when the bitmap is
    /// damaged or compressed. Rows run bottom-up unless the height is negative, and each is padded to four bytes.
    /// </summary>
    internal static (byte[] Pixels, int Width, int Height, bool Opaque)? Decode(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length) return null;
        var width = BinaryPrimitives.ReadInt32LittleEndian(dib[4..]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dib[8..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        var colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);
        if (width <= 0 || rawHeight is 0 or int.MinValue || colorsUsed < 0) return null;
        var height = Math.Abs(rawHeight);
        CheckSize(width, height);

        long offset = headerSize;
        uint red, green, blue, alpha;
        if (compression is BI_BITFIELDS or BI_ALPHABITFIELDS)
        {
            if (bits is not (16 or 32)) return null;
            if (headerSize >= 52)
            {
                // The masks are in the header from its second version on, the alpha mask from its third.
                (red, green, blue) = (Mask(dib, 40), Mask(dib, 44), Mask(dib, 48));
                alpha = headerSize >= 56 ? Mask(dib, 52) : 0;
            }
            else
            {
                // After a plain BITMAPINFOHEADER come three DWORD masks, or four with alpha.
                var count = compression == BI_ALPHABITFIELDS ? 4 : 3;
                if (dib.Length < offset + count * 4) return null;
                (red, green, blue) = (Mask(dib, offset), Mask(dib, offset + 4), Mask(dib, offset + 8));
                alpha = count == 4 ? Mask(dib, offset + 12) : 0;
                offset += count * 4;
            }
        }
        else if (compression == BI_RGB)
        {
            (red, green, blue, alpha) = bits switch
            {
                16 => (0x7C00u, 0x03E0u, 0x001Fu, 0u),
                32 => (0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u),
                _ => (0u, 0u, 0u, 0u),
            };
        }
        else
        {
            return null; // run-length, JPEG and PNG bitmaps, which clipboards don't carry
        }

        // A palette for 8 bits and fewer; deeper bitmaps may carry one for 256-colour screens, which is skipped.
        var palette = Array.Empty<uint>();
        if (bits is 1 or 2 or 4 or 8)
        {
            var entries = colorsUsed > 0 && colorsUsed <= 1 << bits ? colorsUsed : 1 << bits;
            if (dib.Length < offset + entries * 4L) return null;
            palette = new uint[entries];
            for (var i = 0; i < entries; i++) palette[i] = BinaryPrimitives.ReadUInt32LittleEndian(dib[(int)(offset + i * 4)..]);
            offset += entries * 4L;
        }
        else if (bits is 16 or 24 or 32)
        {
            offset += colorsUsed * 4L;
        }
        else
        {
            return null;
        }

        var stride = ((long)width * bits + 31) / 32 * 4;
        if (dib.Length < offset + stride * height) return null;

        var pixels = new byte[(long)width * height * 4];
        var (r, g, b, a) = (new Channel(red), new Channel(green), new Channel(blue), new Channel(alpha));
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            var row = dib.Slice((int)(offset + stride * (rawHeight < 0 ? y : height - 1 - y)), (int)stride);
            var o = (long)y * width * 4;
            for (var x = 0; x < width; x++, o += 4)
            {
                switch (bits)
                {
                    case 24:
                        (pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]) = (row[x * 3], row[x * 3 + 1], row[x * 3 + 2], 255);
                        break;
                    case 16 or 32:
                        var value = bits == 32 ? BinaryPrimitives.ReadUInt32LittleEndian(row[(x * 4)..]) : BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]);
                        (pixels[o], pixels[o + 1], pixels[o + 2]) = (b.Of(value), g.Of(value), r.Of(value));
                        pixels[o + 3] = a.Of(value);
                        anyAlpha |= pixels[o + 3] != 0;
                        break;
                    default:
                        var bit = x * bits;
                        var index = (row[bit / 8] >> (8 - bits - bit % 8)) & ((1 << bits) - 1);
                        var colour = index < palette.Length ? palette[index] : 0;
                        (pixels[o], pixels[o + 1], pixels[o + 2], pixels[o + 3]) = ((byte)colour, (byte)(colour >> 8), (byte)(colour >> 16), 255);
                        break;
                }
            }
        }

        // Most apps leave a 32-bit bitmap's fourth byte at 0 and mean it to be opaque; otherwise it's real transparency.
        var opaque = true;
        if (bits is 16 or 32)
        {
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (!anyAlpha) pixels[i] = 255;
                else opaque &= pixels[i] == 255;
            }
        }
        return (pixels, width, height, opaque);
    }

    static uint Mask(ReadOnlySpan<byte> dib, long at) => BinaryPrimitives.ReadUInt32LittleEndian(dib[(int)at..]);

    /// <summary>One colour's bits in a pixel, scaled to 0–255.</summary>
    readonly struct Channel
    {
        readonly uint _mask;
        readonly int _shift;
        readonly uint _max;

        public Channel(uint mask)
        {
            _mask = mask;
            _shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
            _max = mask >> _shift;
        }

        public byte Of(uint pixel)
        {
            if (_max == 0) return 0;
            var value = (pixel & _mask) >> _shift;
            return _max == 255 ? (byte)value : (byte)(((ulong)value * 255 + _max / 2) / _max);
        }
    }

    static byte[] EncodePng(byte[] bgra, int width, int height, bool opaque)
    {
        using var image = new MagickImage();
        image.ReadPixels(bgra, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "BGRA"));
        if (opaque) image.Alpha(AlphaAction.Off);
        // No dates in the file, so the same picture saves the same way.
        image.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time");
        return image.ToByteArray(MagickFormat.Png);
    }

    /// <summary>A picture file's pixels as straight BGRA, top row first, turned the way its EXIF says.</summary>
    public static (byte[] Pixels, int Width, int Height) Pixels(string file)
    {
        using var image = new MagickImage(file);
        image.AutoOrient();
        using var pixels = image.GetPixelsUnsafe();
        return (pixels.ToByteArray("BGRA")!, (int)image.Width, (int)image.Height);
    }

    /// <summary>
    /// A picture file as the clipboard carries it: a PNG (the file itself when it is one), a CF_DIBV5 with its
    /// transparency, and a CF_DIB laid on white for the apps that ignore transparency. Throws
    /// <see cref="ConversionException"/> when it's larger than <see cref="MaxPixels"/>.
    /// </summary>
    public static (byte[] Png, byte[] DibV5, byte[] Dib) ClipboardFormats(string file)
    {
        var bytes = File.ReadAllBytes(file);
        using var image = new MagickImage(bytes);
        var turned = image.Orientation is not (Orientation.Undefined or Orientation.TopLeft);
        image.AutoOrient();
        var (width, height) = ((int)image.Width, (int)image.Height);
        CheckSize(width, height);
        var png = !turned && FromPng(bytes) is not null ? bytes : EncodePng(image);
        using var collection = image.GetPixelsUnsafe();
        var bgra = collection.ToByteArray("BGRA")!;
        return (png, DibV5(bgra, width, height), Dib(bgra, width, height));
    }

    static byte[] EncodePng(MagickImage image)
    {
        image.Settings.SetDefine(MagickFormat.Png, "exclude-chunks", "date,time");
        return image.ToByteArray(MagickFormat.Png);
    }

    const int PixelsPerMetre = 3780; // 96 DPI

    /// <summary>A BITMAPV5HEADER with colour masks and sRGB, then the rows bottom-up, as most apps read CF_DIBV5.</summary>
    static byte[] DibV5(byte[] bgra, int width, int height)
    {
        const int header = 124;
        var stride = width * 4;
        var dib = new byte[header + (long)stride * height];
        var span = dib.AsSpan();
        WriteInfoHeader(span, header, width, height, 32, BI_BITFIELDS, stride * height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], 0x00FF0000); // red
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], 0x0000FF00); // green
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], 0x000000FF); // blue
        BinaryPrimitives.WriteUInt32LittleEndian(span[52..], 0xFF000000); // alpha
        BinaryPrimitives.WriteUInt32LittleEndian(span[56..], 0x73524742); // LCS_sRGB
        BinaryPrimitives.WriteUInt32LittleEndian(span[108..], 4);         // LCS_GM_IMAGES
        for (var y = 0; y < height; y++)
            bgra.AsSpan(y * stride, stride).CopyTo(span[(int)(header + (long)(height - 1 - y) * stride)..]);
        return dib;
    }

    /// <summary>A BITMAPINFOHEADER and 24-bit rows bottom-up, laid on white, for apps that don't know transparency.</summary>
    static byte[] Dib(byte[] bgra, int width, int height)
    {
        const int header = 40;
        var stride = (width * 3 + 3) / 4 * 4;
        var dib = new byte[header + (long)stride * height];
        var span = dib.AsSpan();
        WriteInfoHeader(span, header, width, height, 24, BI_RGB, stride * height);
        for (var y = 0; y < height; y++)
        {
            var row = span[(int)(header + (long)(height - 1 - y) * stride)..];
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var alpha = bgra[i + 3];
                for (var c = 0; c < 3; c++)
                    row[x * 3 + c] = (byte)((bgra[i + c] * alpha + 255 * (255 - alpha) + 127) / 255);
            }
        }
        return dib;
    }

    static void WriteInfoHeader(Span<byte> dib, int size, int width, int height, int bits, int compression, int imageSize)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dib, size);
        BinaryPrimitives.WriteInt32LittleEndian(dib[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(dib[8..], height); // positive: bottom-up, which every app reads
        BinaryPrimitives.WriteUInt16LittleEndian(dib[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib[14..], (ushort)bits);
        BinaryPrimitives.WriteInt32LittleEndian(dib[16..], compression);
        BinaryPrimitives.WriteInt32LittleEndian(dib[20..], imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(dib[24..], PixelsPerMetre);
        BinaryPrimitives.WriteInt32LittleEndian(dib[28..], PixelsPerMetre);
    }
}

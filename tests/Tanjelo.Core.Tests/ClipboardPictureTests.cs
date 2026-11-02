using System.Buffers.Binary;
using System.Windows;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

/// <summary>
/// Pictures from the clipboard, made from bytes built here the way apps put them there, and the formats Tanjelo puts on
/// it. The real clipboard is left alone, except by the one test that asks for it.
/// </summary>
public class ClipboardPictureTests
{
    /// <summary>Three by two pixels as (B, G, R, A), top row first, with every kind of transparency.</summary>
    static readonly byte[][] Sample =
    [
        [0, 0, 255, 255], [0, 255, 0, 128], [255, 0, 0, 64],
        [255, 255, 255, 255], [0, 0, 0, 200], [128, 128, 128, 10],
    ];

    const int BI_RGB = 0, BI_RLE8 = 1, BI_BITFIELDS = 3;

    [Fact]
    public void A_PNG_is_kept_byte_for_byte_with_its_size()
    {
        using var scratch = new Scratch();
        var bytes = File.ReadAllBytes(scratch.TransparentPng("shot.png", 40));

        var picture = ClipboardPicture.Choose(new ClipboardData { Png = bytes }).Picture;

        Assert.NotNull(picture);
        Assert.Same(bytes, picture.Png);
        Assert.Equal((40, 40), (picture.Width, picture.Height));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_DIBV5_with_colour_masks_keeps_its_transparency_pixel_by_pixel(bool bottomUp)
    {
        using var scratch = new Scratch();
        var masks = new uint[] { 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000 };
        var dib = Dib(124, 3, bottomUp ? 2 : -2, 32, BI_BITFIELDS, Rows(3, 32, Sample, bottomUp), headerMasks: masks);

        var picture = ClipboardPicture.FromBitmap(dib)!;

        Assert.Equal((3, 2), (picture.Width, picture.Height));
        Assert.Equal(Sample, PixelsOf(scratch, picture));
    }

    [Fact]
    public void A_32_bit_bitmap_whose_fourth_bytes_are_all_0_is_opaque()
    {
        using var scratch = new Scratch();
        var noAlpha = Sample.Select(p => new byte[] { p[0], p[1], p[2], 0 }).ToArray();
        var dib = Dib(40, 3, 2, 32, BI_RGB, Rows(3, 32, noAlpha, bottomUp: true));

        var picture = ClipboardPicture.FromBitmap(dib)!;

        Assert.Equal(Sample.Select(p => new byte[] { p[0], p[1], p[2], 255 }), PixelsOf(scratch, picture));
    }

    [Fact]
    public void A_32_bit_bitmap_with_its_fourth_bytes_set_keeps_them_as_transparency()
    {
        using var scratch = new Scratch();
        var dib = Dib(40, 3, -2, 32, BI_RGB, Rows(3, 32, Sample, bottomUp: false));
        Assert.Equal(Sample, PixelsOf(scratch, ClipboardPicture.FromBitmap(dib)!));
    }

    [Fact]
    public void A_24_bit_bitmap_with_an_odd_width_skips_the_padding_at_the_end_of_each_row()
    {
        using var scratch = new Scratch();
        var opaque = Sample.Select(p => new byte[] { p[0], p[1], p[2], 255 }).ToArray();
        var rows = Rows(3, 24, opaque, bottomUp: true);
        Assert.Equal(2 * 12, rows.Length); // 9 bytes of pixels and 3 of padding a row

        var picture = ClipboardPicture.FromBitmap(Dib(40, 3, 2, 24, BI_RGB, rows))!;

        Assert.Equal(opaque, PixelsOf(scratch, picture));
    }

    [Fact]
    public void Bitmaps_of_8_bits_and_fewer_take_their_colours_from_the_palette()
    {
        using var scratch = new Scratch();
        uint[] palette = [0x000000FF, 0x0000FF00, 0x00FF0000]; // blue, green and red, as 0x00RRGGBB
        var eight = Dib(40, 5, 1, 8, BI_RGB, [2, 1, 0, 2, 1, 0, 0, 0], after: palette, colorsUsed: 3);
        Assert.Equal(new byte[][] { [0, 0, 255, 255], [0, 255, 0, 255], [255, 0, 0, 255], [0, 0, 255, 255], [0, 255, 0, 255] },
            PixelsOf(scratch, ClipboardPicture.FromBitmap(eight)!));

        // One bit a pixel, the first pixel in the highest bit; the palette is as long as the bits allow.
        var one = Dib(40, 3, 1, 1, BI_RGB, [0b1010_0000, 0, 0, 0], after: [0x00000000, 0x00FFFFFF]);
        Assert.Equal(new byte[][] { [255, 255, 255, 255], [0, 0, 0, 255], [255, 255, 255, 255] },
            PixelsOf(scratch, ClipboardPicture.FromBitmap(one)!));
    }

    [Fact]
    public void Colour_masks_after_a_plain_header_are_read()
    {
        using var scratch = new Scratch();
        // Red in the lowest byte, as some apps lay it out; with no alpha mask, it's opaque.
        var rgbx = Sample.Select(p => new byte[] { p[2], p[1], p[0], 0 }).ToArray();
        var dib = Dib(40, 3, 2, 32, BI_BITFIELDS, Rows(3, 32, rgbx, bottomUp: true), after: [0x000000FF, 0x0000FF00, 0x00FF0000]);
        Assert.Equal(Sample.Select(p => new byte[] { p[0], p[1], p[2], 255 }), PixelsOf(scratch, ClipboardPicture.FromBitmap(dib)!));

        // 16 bits in 5-6-5: pure red, green and blue, and white.
        ushort[] pixels = [0xF800, 0x07E0, 0x001F, 0xFFFF];
        var sixteen = Dib(40, 4, 1, 16, BI_BITFIELDS, pixels.SelectMany(BitConverter.GetBytes).ToArray(), after: [0xF800, 0x07E0, 0x001F]);
        Assert.Equal(new byte[][] { [0, 0, 255, 255], [0, 255, 0, 255], [255, 0, 0, 255], [255, 255, 255, 255] },
            PixelsOf(scratch, ClipboardPicture.FromBitmap(sixteen)!));
    }

    [Fact]
    public void A_16_bit_bitmap_without_masks_is_5_5_5()
    {
        using var scratch = new Scratch();
        ushort[] pixels = [0x7C00, 0x03E0, 0x001F, 0x7FFF];
        var dib = Dib(40, 4, 1, 16, BI_RGB, pixels.SelectMany(BitConverter.GetBytes).ToArray());
        Assert.Equal(new byte[][] { [0, 0, 255, 255], [0, 255, 0, 255], [255, 0, 0, 255], [255, 255, 255, 255] },
            PixelsOf(scratch, ClipboardPicture.FromBitmap(dib)!));
    }

    public static TheoryData<string, byte[]> Damaged()
    {
        var good = Dib(40, 3, 2, 24, BI_RGB, new byte[24]);
        return new TheoryData<string, byte[]>
        {
            { "rows cut short", good[..^5] },
            { "only part of a header", good[..30] },
            { "no width", Dib(40, 0, 2, 24, BI_RGB, new byte[24]) },
            { "a negative width", Dib(40, -3, 2, 24, BI_RGB, new byte[24]) },
            { "no height", Dib(40, 3, 0, 24, BI_RGB, new byte[24]) },
            { "an old OS/2 header", [.. BitConverter.GetBytes(12), .. good[4..]] },
            { "a header longer than the data", [.. BitConverter.GetBytes(4000), .. good[4..]] },
            { "run-length compression", Dib(40, 3, 2, 8, BI_RLE8, new byte[1100]) },
            { "masks cut off", Dib(40, 3, 2, 32, BI_BITFIELDS, [], after: [0xFF0000, 0xFF00]) },
            { "no bits a pixel", Dib(40, 3, 2, 0, BI_RGB, new byte[24]) },
        };
    }

    [Theory]
    [MemberData(nameof(Damaged))]
    public void A_damaged_bitmap_is_no_picture(string what, byte[] dib)
    {
        var content = ClipboardPicture.Choose(new ClipboardData { Bitmap = dib });
        Assert.True(content.IsEmpty, what);
        Assert.Null(content.Problem);
    }

    [Fact]
    public void A_picture_over_250_megapixels_is_refused_with_the_reason()
    {
        // Only the header: the size is checked before anything else.
        var dib = Dib(40, 20_000, 15_000, 32, BI_RGB, []);
        var content = ClipboardPicture.Choose(new ClipboardData { Bitmap = dib });
        Assert.True(content.IsEmpty);
        Assert.Contains("250 megapixels", content.Problem);

        byte[] png = [0x89, .. "PNG\r\n\x1A\n"u8, 0, 0, 0, 13, .. "IHDR"u8, 0, 0, 0x4E, 0x20, 0, 0, 0x3A, 0x98, 8, 6, 0, 0, 0, 0, 0, 0, 0];
        Assert.Contains("20000 × 15000", ClipboardPicture.Choose(new ClipboardData { Png = png }).Problem);
    }

    [Fact]
    public void Copied_files_come_first_then_a_PNG_then_a_bitmap()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("notes.txt", "x");
        var folder = scratch.Folder("photos");
        var png = File.ReadAllBytes(scratch.Image("shot.png", 4, 3));
        var dib = Dib(40, 2, 1, 24, BI_RGB, new byte[8]);

        var all = ClipboardPicture.Choose(new ClipboardData { Files = [file, scratch.PathOf("gone.txt"), folder], Png = png, Bitmap = dib });
        Assert.Equal([file, folder], all.Files);   // folders are kept, files that are gone are dropped
        Assert.Null(all.Picture);

        var gone = ClipboardPicture.Choose(new ClipboardData { Files = [scratch.PathOf("gone.txt")], Png = png, Bitmap = dib });
        Assert.Empty(gone.Files);
        Assert.Equal((4, 3), (gone.Picture!.Width, gone.Picture.Height));

        var notPng = ClipboardPicture.Choose(new ClipboardData { Png = [1, 2, 3], Bitmap = dib });
        Assert.Equal((2, 1), (notPng.Picture!.Width, notPng.Picture.Height));

        // Text, a metafile or an Outlook attachment is none of these.
        Assert.True(ClipboardPicture.Choose(ClipboardData.Empty).IsEmpty);
    }

    [Fact]
    public void The_bitmap_the_app_put_there_is_used_rather_than_the_one_Windows_made()
    {
        const uint text = 13;
        Assert.Equal(ClipboardData.CF_DIBV5, ClipboardData.OwnBitmap([text, ClipboardData.CF_DIBV5, ClipboardData.CF_DIB]));
        Assert.Equal(ClipboardData.CF_DIB, ClipboardData.OwnBitmap([ClipboardData.CF_DIB, ClipboardData.CF_DIBV5]));
        // Print Screen's bitmap: Windows makes both from it.
        Assert.Equal(ClipboardData.CF_DIB, ClipboardData.OwnBitmap([ClipboardData.CF_BITMAP, ClipboardData.CF_DIB, ClipboardData.CF_DIBV5]));
        Assert.Equal(0u, ClipboardData.OwnBitmap([text]));
    }

    [Fact]
    public void A_picture_goes_on_the_clipboard_as_PNG_bitmaps_and_the_file_and_reads_back_the_same()
    {
        using var scratch = new Scratch();
        var file = scratch.Image("shot.png", 31, 17, System.Drawing.Color.SteelBlue);

        var data = WindowsClipboard.PictureData(file);

        Assert.Equal(new[] { "PNG", "Format17", DataFormats.Dib, DataFormats.FileDrop, "Preferred DropEffect" }.Order(), data.GetFormats(autoConvert: false).Order());
        Assert.Equal(File.ReadAllBytes(file), Stream(data, "PNG"));
        Assert.Equal([file], (string[])data.GetData(DataFormats.FileDrop)!);
        Assert.Equal(1, BitConverter.ToInt32(Stream(data, "Preferred DropEffect")));   // pasted as a copy

        // As OLE offers them to the clipboard: WPF's "Format17" is CF_DIBV5 itself, not a format registered under that name.
        var offered = new List<short>();
        var formats = ((System.Runtime.InteropServices.ComTypes.IDataObject)data).EnumFormatEtc(System.Runtime.InteropServices.ComTypes.DATADIR.DATADIR_GET);
        var one = new System.Runtime.InteropServices.ComTypes.FORMATETC[1];
        while (formats.Next(1, one, null!) == 0) offered.Add(one[0].cfFormat);
        Assert.Contains((short)ClipboardData.CF_DIBV5, offered);
        Assert.Contains((short)ClipboardData.CF_DIB, offered);
        Assert.Contains((short)ClipboardData.CF_HDROP, offered);

        var (pixels, _, _) = ClipboardPicture.Pixels(file);
        foreach (var format in new[] { "Format17", DataFormats.Dib })
        {
            var (decoded, width, height, opaque) = ClipboardPicture.Decode(Stream(data, format))!.Value;
            Assert.Equal((31, 17, true), (width, height, opaque));
            Assert.Equal(pixels, decoded);
        }
    }

    [Fact]
    public void Transparency_goes_on_the_clipboard_in_the_DIBV5_and_on_white_in_the_DIB()
    {
        using var scratch = new Scratch();
        var file = scratch.TransparentPng("logo.png", 40);
        var data = WindowsClipboard.PictureData(file);
        var (pixels, _, _) = ClipboardPicture.Pixels(file);

        var v5 = ClipboardPicture.Decode(Stream(data, "Format17"))!.Value;
        Assert.False(v5.Opaque);
        Assert.Equal(pixels, v5.Pixels);

        var dib = ClipboardPicture.Decode(Stream(data, DataFormats.Dib))!.Value;
        Assert.True(dib.Opaque);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, dib.Pixels[..4]);                       // a see-through corner, on white
        Assert.Equal(pixels.AsSpan(20 * 40 * 4 + 20 * 4, 4).ToArray(), dib.Pixels.AsSpan(20 * 40 * 4 + 20 * 4, 4).ToArray());
    }

    [Fact]
    public void A_photo_saved_on_its_side_goes_on_the_clipboard_turned_upright()
    {
        using var scratch = new Scratch();
        var data = WindowsClipboard.PictureData(scratch.Sideways("portrait.jpg", 120, 80));
        var png = ClipboardPicture.FromPng(Stream(data, "PNG"))!;
        Assert.Equal((80, 120), (png.Width, png.Height));
        var (_, width, height, _) = ClipboardPicture.Decode(Stream(data, "Format17"))!.Value;
        Assert.Equal((80, 120), (width, height));
    }

    /// <summary>Overwrites the real clipboard, so it runs only when TANJELO_REAL_CLIPBOARD is 1, on a PC where that's fine.</summary>
    [Fact]
    public void On_the_real_clipboard_a_picture_goes_on_and_reads_back()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TANJELO_REAL_CLIPBOARD") == "1", "Set TANJELO_REAL_CLIPBOARD=1 to let this test overwrite the clipboard.");
        using var scratch = new Scratch();
        var file = scratch.TransparentPng("logo.png", 40);
        var clipboard = new WindowsClipboard();
        var before = clipboard.Sequence;

        clipboard.SetPicture(file);

        Assert.NotEqual(before, clipboard.Sequence);
        Assert.Equal([file], ClipboardPicture.Choose(clipboard.Read()).Files);   // the file comes first while it's there
        File.Delete(file);
        var picture = ClipboardPicture.Choose(clipboard.Read()).Picture!;
        Assert.Equal((40, 40), (picture.Width, picture.Height));

        // A bitmap alone, as apps that know no PNG copy one.
        var onlyBitmap = new DataObject();
        onlyBitmap.SetData(DataFormats.Dib, new MemoryStream(Dib(40, 3, -2, 32, BI_RGB, Rows(3, 32, Sample, bottomUp: false))), autoConvert: false);
        var thread = new Thread(() => Clipboard.SetDataObject(onlyBitmap, copy: true));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        var read = clipboard.Read();
        Assert.Null(read.Png);
        Assert.Equal(Sample, PixelsOf(scratch, ClipboardPicture.Choose(read).Picture!));
    }

    static byte[] Stream(DataObject data, string format) => ((MemoryStream)data.GetData(format, autoConvert: false)!).ToArray();

    /// <summary>The picture's PNG, read back as (B, G, R, A) a pixel, top row first.</summary>
    static byte[][] PixelsOf(Scratch scratch, ClipboardPicture picture)
    {
        var file = scratch.PathOf(Guid.NewGuid().ToString("N")[..8] + ".png");
        File.WriteAllBytes(file, picture.Png);
        var (pixels, _, _) = ClipboardPicture.Pixels(file);
        return pixels.Chunk(4).ToArray();
    }

    /// <summary>Rows of pixels given top row first, packed at <paramref name="bits"/> a pixel and padded to four bytes.</summary>
    static byte[] Rows(int width, int bits, byte[][] pixels, bool bottomUp)
    {
        var stride = (width * bits + 31) / 32 * 4;
        var rows = pixels.Chunk(width).Select(row =>
        {
            var bytes = row.SelectMany(p => bits == 24 ? p[..3] : p).ToList();
            bytes.AddRange(new byte[stride - bytes.Count]);
            return bytes;
        }).ToList();
        if (bottomUp) rows.Reverse();
        return rows.SelectMany(r => r).ToArray();
    }

    /// <summary>A packed bitmap: its header, what follows the header (masks or a palette), then the rows.</summary>
    static byte[] Dib(int headerSize, int width, int height, int bits, int compression, byte[] rows,
        uint[]? headerMasks = null, uint[]? after = null, int colorsUsed = 0)
    {
        var header = new byte[headerSize];
        var span = header.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)bits);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], compression);
        BinaryPrimitives.WriteInt32LittleEndian(span[32..], colorsUsed);
        for (var i = 0; i < (headerMasks?.Length ?? 0); i++)
            BinaryPrimitives.WriteUInt32LittleEndian(span[(40 + i * 4)..], headerMasks![i]);
        return [.. header, .. (after ?? []).SelectMany(BitConverter.GetBytes), .. rows];
    }
}

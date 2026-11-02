using System.Buffers.Binary;
using Color = System.Drawing.Color;

namespace Tanjelo.Core.Tests;

/// <summary>Tanjelo's folder for pictures from the clipboard, made in a scratch folder rather than %LOCALAPPDATA%.</summary>
public class ClipboardFolderTests
{
    internal static readonly DateTime Taken = new(2026, 11, 2, 14, 30, 5);

    /// <summary>A picture as the clipboard would hold it, from a solid-colour PNG.</summary>
    internal static ClipboardPicture Picture(Scratch scratch, string name, Color colour, int width = 20, int height = 10) =>
        ClipboardPicture.FromPng(File.ReadAllBytes(scratch.Image(name, width, height, colour)))!;

    [Fact]
    public void Pictures_are_named_as_Windows_names_screenshots_and_never_overwrite()
    {
        using var scratch = new Scratch();
        var folder = new ClipboardFolder(scratch.PathOf("Clipboard"), () => Taken);

        var (first, isNew) = folder.Save(Picture(scratch, "red.png", Color.Red), 1);
        var (second, _) = folder.Save(Picture(scratch, "blue.png", Color.Blue), 2);

        Assert.Equal(scratch.PathOf(@"Clipboard\Screenshot 2026-11-02 143005.png"), first);
        Assert.True(isNew);
        Assert.Equal(scratch.PathOf(@"Clipboard\Screenshot 2026-11-02 143005 (2).png"), second);
        Assert.Equal(File.ReadAllBytes(scratch.PathOf("red.png")), File.ReadAllBytes(first));   // as it was on the clipboard
    }

    [Fact]
    public void The_same_copy_or_the_same_pixels_is_the_file_saved_before()
    {
        using var scratch = new Scratch();
        var folder = new ClipboardFolder(scratch.PathOf("Clipboard"), () => Taken);
        var picture = Picture(scratch, "red.png", Color.Red);
        var (first, _) = folder.Save(picture, 7);

        Assert.Equal((first, false), folder.Save(picture, 7));   // the same copy, pasted again

        // The same pixels as a bitmap, as another app might copy the same screenshot: a different file, the same picture.
        var (pixels, width, height) = ClipboardPicture.Pixels(scratch.PathOf("red.png"));
        var again = ClipboardPicture.FromBitmap(TopDownBitmap(pixels, width, height))!;
        Assert.NotEqual(picture.Png, again.Png);
        Assert.Equal((first, false), folder.Save(again, 8));

        // Other pixels of the same size are another picture.
        var (other, isNew) = folder.Save(Picture(scratch, "blue.png", Color.Blue), 9);
        Assert.NotEqual(first, other);
        Assert.True(isNew);
        Assert.Equal(2, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public void What_the_shelf_doesn_t_list_is_left_behind_and_what_it_lists_is_kept()
    {
        using var scratch = new Scratch();
        var folder = new ClipboardFolder(scratch.PathOf("Clipboard"), () => Taken);
        Assert.Empty(folder.Orphans([]));   // no folder yet
        var kept = Path.Combine(scratch.Folder("Clipboard", "kept.png", "orphan.png"), "kept.png");
        var pages = Directory.CreateDirectory(Path.Combine(folder.Path, "pages")).FullName;

        Assert.Equal([Path.Combine(folder.Path, "orphan.png"), pages], folder.Orphans([kept.ToUpperInvariant(), scratch.Text("elsewhere.txt", "x")]).Order());
    }

    [Fact]
    public void Only_what_is_inside_the_folder_is_held()
    {
        using var scratch = new Scratch();
        var folder = new ClipboardFolder(scratch.PathOf("Clipboard") + "\\", () => Taken);
        Assert.True(folder.Holds(scratch.PathOf(@"Clipboard\shot.png")));
        Assert.True(folder.Holds(scratch.PathOf(@"CLIPBOARD\pages\1.png")));
        Assert.False(folder.Holds(scratch.PathOf("Clipboard")));
        Assert.False(folder.Holds(scratch.PathOf(@"Clipboard2\shot.png")));
        Assert.False(folder.Holds(""));
    }

    /// <summary>A 32-bit bitmap with its rows top-down, as CF_DIB holds it.</summary>
    internal static byte[] TopDownBitmap(byte[] bgra, int width, int height)
    {
        var dib = new byte[40 + bgra.Length];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), -height);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), 32);
        bgra.CopyTo(dib, 40);
        return dib;
    }
}

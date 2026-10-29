using System.Runtime.InteropServices;
using System.Windows;

namespace Tanjelo.Core.Tests;

public class ShelfTests
{
    [Fact]
    public void Files_and_folders_are_added_once_in_order()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var folder = scratch.Folder("pics", "1.png");
        var shelf = new Shelf();

        var first = shelf.Add([a, folder]);
        var second = shelf.Add([a.ToUpperInvariant(), b, folder + "\\", scratch.PathOf("missing.txt")]);

        Assert.Equal([a, folder], first);
        Assert.Equal([b], second);
        Assert.Equal([a, folder, b], shelf.Items);
    }

    [Fact]
    public void Items_whose_files_are_gone_drop_off()
    {
        using var scratch = new Scratch();
        var keep = scratch.Text("keep.txt", "k");
        var moved = scratch.Text("moved.txt", "m");
        var shelf = new Shelf();
        shelf.Add([keep, moved]);
        var changes = 0;
        shelf.Changed += () => changes++;

        File.Move(moved, scratch.PathOf("elsewhere.txt"));
        var pruned = shelf.Prune();

        Assert.Equal([moved], pruned);
        Assert.Equal([keep], shelf.Items);
        Assert.Equal(1, changes);
        Assert.Empty(shelf.Prune());
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Items_can_be_removed_or_cleared()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var shelf = new Shelf();
        shelf.Add([a, b]);

        Assert.True(shelf.Remove([a.ToUpperInvariant()]));
        Assert.False(shelf.Remove([a]));
        Assert.Equal([b], shelf.Items);
        shelf.Clear();
        Assert.Empty(shelf.Items);
    }

    [Fact]
    public void The_shelf_survives_a_restart_without_what_was_deleted()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var file = scratch.PathOf(@"settings\float.json");
        var shelf = new Shelf { Open = true };
        shelf.Add([a, b]);
        shelf.Save(file);

        File.Delete(b);
        var loaded = Shelf.Load(file);

        Assert.Equal([a], loaded.Items);
        Assert.True(loaded.Open);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json at all")]
    [InlineData("{\"items\": null}")]
    public void A_missing_or_damaged_file_is_an_empty_shelf(string? content)
    {
        using var scratch = new Scratch();
        var file = scratch.PathOf("float.json");
        if (content is not null) File.WriteAllText(file, content);

        var shelf = Shelf.Load(file);

        Assert.Empty(shelf.Items);
        Assert.False(shelf.Open);
    }

    [Fact]
    public void An_empty_shelf_doesn_t_reopen()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var file = scratch.PathOf("float.json");
        var shelf = new Shelf { Open = true };
        shelf.Add([a]);
        shelf.Save(file);
        File.Delete(a);

        Assert.False(Shelf.Load(file).Open);
    }

    [Fact]
    public async Task Dragging_out_copies_even_within_a_drive_unless_Shift_moves_or_Alt_makes_shortcuts()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var copies = scratch.Folder("copies");
        var shortcuts = scratch.Folder("shortcuts");
        await Ui.Run(async () =>
        {
            // Explorer's own rule for plain files, which the shelf used to leave to it: move within a drive.
            Assert.Equal(DragDropEffects.Move, Offer(copies, new DataObject(DataFormats.FileDrop, new[] { a }), 0));

            var data = FloatWindow.DragData([a, b]);
            Assert.Equal(DragDropEffects.Copy, Offer(copies, data, 0));
            Assert.Equal(DragDropEffects.Copy, Offer(copies, data, Control));
            Assert.Equal(DragDropEffects.Move, Offer(copies, data, Shift));
            Assert.Equal(DragDropEffects.Link, Offer(copies, data, Alt));

            // Dropped for real, so the files named to Explorer are seen to be the right ones.
            Offer(copies, FloatWindow.DragData([a, b]), 0, drop: true);
            Offer(shortcuts, FloatWindow.DragData([a, b]), Alt, drop: true);
            for (var waited = 0; waited < 10_000 && (Directory.GetFiles(copies).Length < 2 || Directory.GetFiles(shortcuts).Length < 2); waited += 50)
                await Ui.Settle(50);
        });
        Assert.Equal(["a.txt", "b.txt"], Directory.GetFiles(copies).Select(f => Path.GetFileName(f)).Order());
        Assert.Equal("b", File.ReadAllText(Path.Combine(copies, "b.txt")));
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Collection(Directory.GetFiles(shortcuts).Select(f => Path.GetFileName(f)).Order(),
            name => Assert.Matches(@"^a\.txt.*\.lnk$", name),
            name => Assert.Matches(@"^b\.txt.*\.lnk$", name));
    }

    const int LeftButton = 0x1, Shift = 0x4, Control = 0x8, Alt = 0x20; // MK_*

    /// <summary>
    /// What Explorer's drop target for <paramref name="folder"/> does with the drag and the keys held, as the pointer
    /// comes over it; then the drag leaves or, with <paramref name="drop"/>, is dropped there.
    /// </summary>
    static DragDropEffects Offer(string folder, DataObject data, int keys, bool drop = false)
    {
        SHCreateItemFromParsingName(folder, IntPtr.Zero, typeof(IShellItem).GUID, out var item);
        item.BindToHandler(IntPtr.Zero, SFViewObject, typeof(IDropTarget).GUID, out var handler);
        var target = (IDropTarget)handler;
        const int offered = (int)(DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        var effect = offered;
        target.DragEnter(data, LeftButton | keys, 0, ref effect);
        var dropped = offered;
        if (drop) target.Drop(data, keys, 0, ref dropped); // the button is up by then
        else target.DragLeave();
        return (DragDropEffects)effect;
    }

    static readonly Guid SFViewObject = new("3981E226-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFViewObject

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid handler, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTarget
    {
        void DragEnter(System.Runtime.InteropServices.ComTypes.IDataObject data, int keys, long point, ref int effect);
        void DragOver(int keys, long point, ref int effect);
        void DragLeave();
        void Drop(System.Runtime.InteropServices.ComTypes.IDataObject data, int keys, long point, ref int effect);
    }
}

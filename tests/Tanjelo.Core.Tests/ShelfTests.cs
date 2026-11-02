using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

    /// <summary>A shelf holding pictures in a scratch clipboard folder, which records what it would send to the Recycle Bin.</summary>
    static (Shelf Shelf, ClipboardFolder Folder, List<string> Recycled) Holding(Scratch scratch)
    {
        var folder = new ClipboardFolder(scratch.PathOf("Clipboard"), () => ClipboardFolderTests.Taken);
        Directory.CreateDirectory(folder.Path);
        var recycled = new List<string>();
        return (new Shelf(folder, recycled.AddRange), folder, recycled);
    }

    static string Held(ClipboardFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.WriteAllText(path, name);
        return path;
    }

    [Fact]
    public void Pictures_the_shelf_holds_go_to_the_Recycle_Bin_when_taken_off_and_other_files_stay_where_they_are()
    {
        using var scratch = new Scratch();
        var (shelf, folder, recycled) = Holding(scratch);
        var (one, two, three) = (Held(folder, "1.png"), Held(folder, "2.png"), Held(folder, "3.png"));
        var mine = scratch.Text("mine.txt", "x");
        shelf.Add([one, mine, two, three]);

        Assert.True(shelf.Holds(one));
        Assert.False(shelf.Holds(mine));
        shelf.Remove([one, mine]);
        Assert.Equal([one], recycled);
        shelf.Clear();
        Assert.Equal([one, two, three], recycled);
        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void A_picture_copied_again_takes_the_place_of_the_one_before()
    {
        using var scratch = new Scratch();
        var (shelf, folder, recycled) = Holding(scratch);
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var (before, after) = (Held(folder, "shot.png"), Held(folder, "shot (2).png"));
        shelf.Add([a, before, b]);
        var changes = 0;
        shelf.Changed += () => changes++;

        Assert.True(shelf.Replace(before, after));
        Assert.Equal([a, after, b], shelf.Items);
        Assert.Equal([before], recycled);
        Assert.Equal(1, changes);
        Assert.False(shelf.Replace(before, a));   // no longer there
    }

    [Fact]
    public void Used_marks_last_while_the_item_is_on_the_shelf()
    {
        using var scratch = new Scratch();
        var (shelf, folder, _) = Holding(scratch);
        var (one, two) = (Held(folder, "1.png"), Held(folder, "2.png"));
        shelf.Add([one, two]);

        shelf.MarkUsed([one.ToUpperInvariant()]);
        Assert.True(shelf.IsUsed(one));
        Assert.False(shelf.IsUsed(two));
        shelf.Remove([one]);
        shelf.Add([one]);
        Assert.False(shelf.IsUsed(one));
    }

    [Fact]
    public void After_a_restart_held_pictures_come_back_and_only_those_no_shelf_lists_are_left_over()
    {
        using var scratch = new Scratch();
        var (shelf, folder, _) = Holding(scratch);
        var listed = Held(folder, "listed.png");
        var orphan = Held(folder, "orphan.png");
        shelf.Add([listed]);
        var file = scratch.PathOf("float.json");
        shelf.Save(file);

        var recycled = new List<string>();
        var loaded = Shelf.Load(file, folder, recycled.AddRange);

        Assert.Equal([listed], loaded.Items);
        Assert.Empty(recycled);
        Assert.Equal([orphan], folder.Orphans(loaded.Items));
    }

    [Fact]
    public async Task Pictures_not_saved_yet_say_so_and_their_menu_saves_copies_and_sends_them_to_the_Recycle_Bin()
    {
        using var scratch = new Scratch();
        var (shelf, folder, recycled) = Holding(scratch);
        var (held, _) = folder.Save(ClipboardFolderTests.Picture(scratch, "shot.png", System.Drawing.Color.SteelBlue, 40, 30), 1);
        var mine = scratch.Image("mine.png");
        var saved = scratch.Folder("Saved");
        await Ui.Run(async () =>
        {
            shelf.Add([held, mine]);
            var revealed = new List<IReadOnlyList<string>>();
            var window = new FloatWindow(shelf, "Ctrl+Shift+Space", new FakeClipboard(), revealed.Add)
            {
                AskForFileName = _ => Path.Combine(saved, "renamed.png"),
                AskForFolder = () => saved,
            };
            var host = Ui.Host(window, 248, 320);
            await Ui.Settle();
            var list = window.List;

            var item = (FloatWindow.Item)list.Items[0];
            Assert.Equal("Not saved yet", item.Folder);
            Assert.Equal("Screenshot 2026-11-02 143005.png, not saved yet", item.Spoken);
            Assert.Equal(Path.GetDirectoryName(mine), ((FloatWindow.Item)list.Items[1]).Folder);
            Assert.Contains("Recycle Bin", window.KeysText.Text);
            Assert.Contains("Recycle Bin", (string)window.ClearButton.ToolTip);
            Ui.Render(host, "shelf-held-light");
            Ui.UseTheme("dark", host);
            Ui.Render(host, "shelf-held-dark");
            AccessibilityTests.RenderInContrast(host, "shelf-held-contrast", Theme.Contrast.Aquatic);

            // One picture not saved yet: Save as…, and no Show in folder, which would open Tanjelo's own folder.
            list.SelectedItems.Add(list.Items[0]);
            window.ShowMenuItems();
            Assert.Equal([Tanjelo.Strings.Copy, Tanjelo.Strings.SaveAs, Tanjelo.Strings.TakeOffTheShelf], Visible(window.ItemMenu));
            Click(window.SaveAsItem);
            Assert.Equal(File.ReadAllBytes(held), File.ReadAllBytes(Path.Combine(saved, "renamed.png")));
            Assert.Equal([held, mine], shelf.Items);   // the shelf keeps its own
            Assert.True(shelf.IsUsed(held));
            Assert.Equal("Saved as renamed.png", window.Notice.Text);

            // With a file of the user's too: Save to… a folder, and Show in folder for that one only.
            list.SelectedItems.Add(list.Items[1]);
            window.ShowMenuItems();
            Assert.Equal([Tanjelo.Strings.ShowInFolder, Tanjelo.Strings.Copy, Tanjelo.Strings.SaveTo, Tanjelo.Strings.TakeOffTheShelf], Visible(window.ItemMenu));
            Click(window.SaveToItem);
            Assert.Equal(["mine.png", "renamed.png", "Screenshot 2026-11-02 143005.png"], Directory.GetFiles(saved).Select(Path.GetFileName).Order());
            Click(window.ShowInFolderItem);
            Assert.Equal([[mine]], revealed.Select(r => r.ToArray()));
            var menu = window.ItemMenu;
            (menu.Width, menu.Height) = (240, 190);
            Ui.Render(menu, "shelf-held-menu-light");

            Click(window.TakeOffItem);
            Assert.Empty(shelf.Items);
            Assert.Equal([held], recycled);   // the picture, not the user's file
            Assert.True(File.Exists(mine));
        });
    }

    [Fact]
    public async Task Ctrl_V_parks_a_picture_from_the_clipboard_and_copied_files_where_they_are()
    {
        using var scratch = new Scratch();
        var (shelf, folder, _) = Holding(scratch);
        var png = File.ReadAllBytes(scratch.TransparentPng("logo.png", 40));
        var mine = scratch.Image("mine.png");
        await Ui.Run(async () =>
        {
            var clipboard = new FakeClipboard { Data = new ClipboardData { Png = png }, Sequence = 4 };
            var window = new FloatWindow(shelf, null, clipboard);
            var host = Ui.Host(window, 248, 260);
            await Ui.Settle();
            Ui.Render(host, "shelf-empty-light");   // where to drop, or what to press

            await window.PasteAsync();
            var parked = Assert.Single(shelf.Items);
            Assert.Equal(folder.Path, Path.GetDirectoryName(parked));
            Assert.Equal(png, File.ReadAllBytes(parked));
            Assert.Equal("Parked on the shelf", window.Notice.Text);

            await window.PasteAsync();   // the same copy again
            Assert.Single(shelf.Items);

            clipboard.Data = new ClipboardData { Files = [mine], Png = png };
            await window.PasteAsync();
            Assert.Equal([parked, mine], shelf.Items);

            clipboard.Data = ClipboardData.Empty;
            await window.PasteAsync();
            Assert.Equal("There's no picture or files on the clipboard.", window.Notice.Text);
            Assert.Equal(2, shelf.Items.Count);
        });
    }

    static List<string> Visible(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().Where(i => i.Visibility == Visibility.Visible).Select(i => (string)i.Header).ToList();

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
        // Named "a.txt - Shortcut.lnk", or "a - Shortcut.lnk" where Explorer hides extensions, so look where they point.
        Assert.All(Directory.GetFiles(shortcuts), f => Assert.Equal(".lnk", Path.GetExtension(f)));
        Assert.Equal(["a.txt", "b.txt"], Directory.GetFiles(shortcuts).Select(f => Path.GetFileName(ShortcutTarget(f))).Order());
    }

    [Fact]
    public async Task A_press_on_one_of_several_selected_files_keeps_them_all_to_drag_until_it_s_let_go()
    {
        using var scratch = new Scratch();
        var files = new[] { scratch.Text("a.txt", "a"), scratch.Text("b.txt", "b"), scratch.Text("c.txt", "c") };
        await Ui.Run(async () =>
        {
            var shelf = new Shelf();
            shelf.Add(files);
            var window = new FloatWindow(shelf, null);
            var host = Ui.Host(window, 248, 300);
            using var attached = A11y.Attach(host); // so the files can take the focus, as a click gives it
            await Ui.Settle();
            var list = window.List;
            list.SelectedItems.Add(list.Items[0]);
            list.SelectedItems.Add(list.Items[2]);

            Press(Container(list, 2));
            Assert.Equal([list.Items[0], list.Items[2]], list.SelectedItems.Cast<object>());
            Release(Container(list, 2));
            Assert.Equal([list.Items[2]], list.SelectedItems.Cast<object>());

            // Any other file is selected alone as it's pressed.
            Press(Container(list, 1));
            Assert.Equal([list.Items[1]], list.SelectedItems.Cast<object>());
            Release(Container(list, 1));
        });
    }

    [Fact]
    public async Task The_right_click_menu_copies_shows_and_takes_off_the_selected_files()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var c = Path.Combine(scratch.Folder("other", "c.txt"), "c.txt");
        await Ui.Run(async () =>
        {
            var shelf = new Shelf();
            shelf.Add([a, b, c]);
            var clipboard = new FakeClipboard();
            var revealed = new List<IReadOnlyList<string>>();
            var window = new FloatWindow(shelf, null, clipboard, revealed.Add);
            var host = Ui.Host(window, 248, 300);
            await Ui.Settle();
            var list = window.List;
            list.SelectedItems.Add(list.Items[2]);
            list.SelectedItems.Add(list.Items[0]);

            Click(window.CopyItem);
            Assert.Equal([a, c], clipboard.Files);           // in the shelf's order
            Click(window.ShowInFolderItem);
            Assert.Equal([[a], [c]], revealed.Select(r => r.ToArray()));   // a window for each folder

            // The menu, as it shows: the window's words, the keys, and the theme's colours.
            window.ShowMenuItems();
            Assert.Equal([Tanjelo.Strings.ShowInFolder, Tanjelo.Strings.Copy, Tanjelo.Strings.SaveTo, Tanjelo.Strings.TakeOffTheShelf], Visible(window.ItemMenu));
            Assert.Equal("Ctrl+C", window.CopyItem.InputGestureText);
            // Laid out on its own, as in its popup: a menu can't go inside anything else.
            var menu = window.ItemMenu;
            (menu.Width, menu.Height) = (240, 190);
            typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!.SetValue(window.CopyItem, true); // as under the pointer
            var light = Ui.Render(menu, "shelf-menu-light");
            Ui.UseTheme("dark", menu);
            Ui.Render(menu, "shelf-menu-dark");
            Ui.UseTheme("light", menu);
            Assert.True(Ui.Near(Ui.Colour("Surface"), Ui.Pixel(light, 120, 6)));

            Click(window.TakeOffItem);
            Assert.Equal([b], shelf.Items);
            Assert.True(File.Exists(a) && File.Exists(c));   // only off the shelf
        });
    }

    static ListBoxItem Container(ListBox list, int index) => (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);

    static void Press(UIElement element) => LeftMouseButton(element, Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent);

    static void Release(UIElement element) => LeftMouseButton(element, Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent);

    /// <summary>
    /// The left button going down or up on an element, as the input system raises it: the tunnelling event, then the
    /// bubbling one unless that was handled. Each element on the way raises its own ...LeftButton... event from them.
    /// </summary>
    static void LeftMouseButton(UIElement element, RoutedEvent preview, RoutedEvent main)
    {
        var first = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = preview };
        element.RaiseEvent(first);
        if (!first.Handled)
            element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = main });
    }

    static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    static string ShortcutTarget(string shortcut)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            return shell.CreateShortcut(shortcut).TargetPath;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
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

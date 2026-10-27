using System.Collections.Immutable;
using System.Windows;
using System.Windows.Input;
using Tanjelo.Editors;
using Point = System.Windows.Point;

namespace Tanjelo.Core.Tests;

/// <summary>The page editor: where dropped pages land, and the window driven off-screen.</summary>
public class PageEditorTests
{
    /// <summary>Two rows of 100 × 120 tiles, three to a row, 10 apart.</summary>
    static readonly Rect[] Grid = Enumerable.Range(0, 5).Select(i => new Rect(i % 3 * 110, i / 3 * 130, 100, 120)).ToArray();

    [Fact]
    public void A_drop_goes_before_the_tile_whose_middle_is_right_of_the_pointer_in_the_nearest_row()
    {
        Assert.Equal(0, PageMoves.InsertionAt(Grid, new Point(20, 50)));
        Assert.Equal(1, PageMoves.InsertionAt(Grid, new Point(70, 50)));    // right half of the first
        Assert.Equal(3, PageMoves.InsertionAt(Grid, new Point(400, 60)));   // past the end of the first row
        Assert.Equal(3, PageMoves.InsertionAt(Grid, new Point(10, 140)));   // the start of the second
        Assert.Equal(5, PageMoves.InsertionAt(Grid, new Point(300, 500)));  // below everything: the end
        Assert.Equal(4, PageMoves.InsertionAt(Grid, new Point(150, 126)));  // in the gap, nearer the second row
        Assert.Equal((3, Grid[2], true), PageMoves.Drop(Grid, new Point(400, 60)));
    }

    [Fact]
    public void Moving_keeps_the_chosen_items_in_their_order()
    {
        ImmutableList<char> items = [.. "abcdefg"];
        Assert.Equal("cfabdeg", string.Concat(PageMoves.Move(items, [2, 5], 0)));
        Assert.Equal("abdecfg", string.Concat(PageMoves.Move(items, [2, 5], 5)));
        Assert.Equal("abdegcf", string.Concat(PageMoves.Move(items, [2, 5], 7)));
        Assert.Equal("abcdefg", string.Concat(PageMoves.Move(items, [2, 3], 3)));
    }

    static Point Middle(Rect tile) => new(tile.Left + tile.Width / 2, tile.Top + tile.Height / 2);

    static string Order(PageEditor editor) =>
        string.Join(",", editor.Pages.Select(p => (p.Source == 0 ? "" : "b") + (p.Index + 1) + (p.Rotation == 0 ? "" : $"r{p.Rotation}")));

    [Fact]
    public async Task Pages_are_picked_dragged_turned_copied_and_deleted_with_undo()
    {
        using var scratch = new Scratch();
        var pdf = OrganizePdfTests.Numbered(scratch, pages: 7);
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "organize-pdf", [pdf]);
            var editor = new PageEditor(pdf, target);
            var window = new EditorWindow(pdf, target, editor);
            var host = Ui.Host(window, 1100, 760);
            await window.LoadAsync();
            await Ui.Settle();
            Ui.Layout(host);
            await editor.SettleThumbnailsAsync();
            Assert.EndsWith(" · 7 pages", window.Subheading.Text);
            Assert.Equal("1,2,3,4,5,6,7", Order(editor));
            Assert.False(window.SaveButton.IsEnabled);

            // Click page 3, Ctrl+click page 5, then drag them both to the front.
            var tiles = editor.TileRects();
            editor.PointerDown(Middle(tiles[2]), ModifierKeys.None);
            editor.PointerUp(Middle(tiles[2]));
            editor.PointerDown(Middle(tiles[4]), ModifierKeys.Control);
            editor.PointerUp(Middle(tiles[4]));
            Assert.Equal(2, editor.Selected.Count);
            Assert.StartsWith("7 pages · 2 selected", window.StatusText.Text);
            editor.PointerDown(Middle(tiles[2]), ModifierKeys.None);
            editor.PointerMove(new Point(tiles[0].Left + 8, tiles[0].Top + 40));
            Ui.Layout(host);
            Ui.Render(host, "pages-dragging-light");
            editor.PointerUp(new Point(tiles[0].Left + 8, tiles[0].Top + 40));
            Assert.Equal("3,5,1,2,4,6,7", Order(editor));
            Assert.Equal(2, editor.Selected.Count); // still picked, to be turned next

            editor.Turn(90);
            editor.Duplicate();
            Assert.Equal("3r90,3r90,5r90,5r90,1,2,4,6,7", Order(editor));
            editor.Delete(); // the two copies, selected after copying
            Assert.Equal("3r90,5r90,1,2,4,6,7", Order(editor));
            Assert.True(window.SaveButton.IsEnabled);
            Assert.Equal("3r90,5r90,1-2,4,6-7", editor.Result().Parameters["pages"]);

            // Shift+click picks a run; the keyboard moves the selection along.
            tiles = editor.TileRects();
            editor.PointerDown(Middle(tiles[2]), ModifierKeys.None);
            editor.PointerUp(Middle(tiles[2]));
            editor.PointerDown(Middle(tiles[3]), ModifierKeys.Shift);
            editor.PointerUp(Middle(tiles[3]));
            editor.Step(Key.Right, move: true, extend: false);
            Assert.Equal("3r90,5r90,4,1,2,6,7", Order(editor));

            editor.Undo();
            editor.Undo();
            editor.Undo();
            Assert.Equal("3r90,5r90,1,2,4,6,7", Order(editor)); // the move, the delete and the copy undone
            while (editor.CanUndo) editor.Undo();
            Assert.Equal("1,2,3,4,5,6,7", Order(editor));
            Assert.False(window.SaveButton.IsEnabled);

            await editor.SettleThumbnailsAsync();
            Ui.Layout(host);
            var light = Ui.Render(host, "pages-light");
            Ui.UseTheme("dark", host);
            await Ui.Settle();
            Ui.Render(host, "pages-dark");
            Assert.True(editor.PictureShown(0), "the first page should have its picture");
        });
    }

    [Fact]
    public async Task Saving_only_the_selected_pages_and_pages_from_another_pdf()
    {
        using var scratch = new Scratch();
        var pdf = OrganizePdfTests.Numbered(scratch, pages: 4);
        var more = OrganizePdfTests.Numbered(scratch, "more.pdf", pages: 2, bookmarks: false, prefix: "More");
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "organize-pdf", [pdf]);
            var editor = new PageEditor(pdf, target);
            var window = new EditorWindow(pdf, target, editor);
            var host = Ui.Host(window, 1000, 700);
            await window.LoadAsync();
            await Ui.Settle();
            Ui.Layout(host);

            var tiles = editor.TileRects();
            editor.PointerDown(Middle(tiles[1]), ModifierKeys.None);
            editor.PointerUp(Middle(tiles[1]));
            await editor.InsertAsync(more);
            Assert.Equal("1,2,b1,b2,3,4", Order(editor));
            Assert.Equal(2, editor.Selected.Count); // the added pages
            var saved = editor.Result();
            Assert.Equal("1-2,b1-2,3-4", saved.Parameters["pages"]);
            Assert.Equal(more, saved.Parameters["with"]);

            editor.OnlySelectedBox.IsChecked = true;
            saved = editor.Result();
            Assert.Equal("b1-2", saved.Parameters["pages"]);
            Ui.Layout(host);
            await editor.SettleThumbnailsAsync();
            Ui.Render(host, "pages-added-light");

            // What the window hands over is what the engine saves.
            var result = await Env.Engine().RunAsync([pdf], saved, ct: TestContext.Current.CancellationToken);
            Assert.Empty(result.Failures);
            using var output = UglyToad.PdfPig.PdfDocument.Open(Assert.Single(result.Outputs));
            Assert.Equal(["More 1", "More 2"], output.GetPages().Select(p => p.Text.Trim()));
        });
    }

    [Fact]
    public async Task A_locked_pdf_says_so_instead_of_showing_pages()
    {
        using var scratch = new Scratch();
        var locked = scratch.TextPdf("locked.pdf", "secret", g => g.DrawString("Hidden", new PdfSharp.Drawing.XFont("Arial", 20), PdfSharp.Drawing.XBrushes.Black, 50, 50));
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "organize-pdf", [locked]);
            var window = new EditorWindow(locked, target, new PageEditor(locked, target));
            var host = Ui.Host(window, 900, 600);
            await window.LoadAsync();
            Assert.Equal("This PDF is password-protected.", window.Message.Text);
            Assert.False(window.SaveButton.IsEnabled);
            Ui.Render(host, "pages-locked-light");
        });
    }
}

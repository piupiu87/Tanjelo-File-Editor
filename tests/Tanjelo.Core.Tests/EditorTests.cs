using Tanjelo.Editors;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Vector = System.Windows.Vector;

namespace Tanjelo.Core.Tests;

/// <summary>The editor window's undo history and zoom, without a screen.</summary>
public class EditorTests
{
    [Fact]
    public void Undo_and_redo_step_through_the_states_and_a_new_edit_drops_the_redo()
    {
        var history = new UndoStack<string>("a");
        Assert.False(history.CanUndo);
        Assert.False(history.Changed);
        history.Push("b");
        history.Push("c");
        Assert.Equal("b", history.Undo());
        Assert.Equal("a", history.Undo());
        Assert.Equal("a", history.Undo()); // nothing further back
        Assert.False(history.Changed);
        Assert.Equal("b", history.Redo());
        history.Push("d");
        Assert.False(history.CanRedo);
        Assert.Equal("d", history.Current);
        Assert.Equal("b", history.Undo());
    }

    [Fact]
    public void A_gesture_amends_its_one_step()
    {
        var history = new UndoStack<int>(0);
        history.Push(10);
        history.Amend(20);
        history.Amend(30);
        Assert.Equal(30, history.Current);
        Assert.Equal(0, history.Undo());
        Assert.Equal(30, history.Redo());
    }

    [Fact]
    public void Undoing_everything_counts_as_unchanged_until_steps_have_been_let_go()
    {
        var history = new UndoStack<int>(0);
        history.Push(1);
        history.Undo();
        Assert.False(history.Changed);

        for (var i = 1; i <= UndoStack<int>.Depth + 5; i++) history.Push(i);
        while (history.CanUndo) history.Undo();
        Assert.Equal(5, history.Current); // the oldest steps are gone
        Assert.True(history.Changed);     // so this isn't the original any more
    }

    static ZoomView View(double pictureWidth = 4000, double pictureHeight = 3000, double width = 800, double height = 600, double pixelsPerDip = 1)
    {
        var view = new ZoomView();
        view.Show(new Size(pictureWidth, pictureHeight), new Size(width, height), pixelsPerDip);
        return view;
    }

    [Fact]
    public void A_picture_opens_fitted_and_centred_and_a_small_one_at_100_percent()
    {
        var big = View(4000, 2000, 800, 600);
        Assert.Equal(0.2, big.Zoom, 9);
        Assert.Equal(new Point(0, 100), big.Offset);
        Assert.True(big.Fitted);

        var small = View(200, 100, 800, 600);
        Assert.Equal(1, small.Zoom);
        Assert.Equal(new Point(300, 250), small.Offset);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Zooming_keeps_the_pixel_under_the_pointer_at_any_zoom(double pixelsPerDip)
    {
        var view = View(pixelsPerDip: pixelsPerDip);
        var pointer = new Point(530, 210);
        var pixel = view.ToPicture(pointer);
        foreach (var factor in new[] { 1.2, 1.2, 2, 5, 0.5, 1.2, 0.8, 3 })
        {
            view.ZoomAt(pointer, factor);
            var now = view.ToPicture(pointer);
            Assert.Equal(pixel.X, now.X, 6);
            Assert.Equal(pixel.Y, now.Y, 6);
        }
        Assert.Equal(pointer.X, view.ToCanvas(pixel).X, 6);
    }

    [Fact]
    public void One_hundred_percent_is_a_picture_pixel_per_screen_pixel()
    {
        var view = View(pixelsPerDip: 1.5);
        view.ZoomTo(1);
        Assert.Equal(1, view.Zoom, 9);
        Assert.Equal(1 / 1.5, view.Scale, 9); // two thirds of a canvas unit, which the screen shows as one pixel
        Assert.Equal(32, View().Also(v => v.ZoomTo(1000)).Zoom);
        Assert.Equal(ZoomView.Least, View().Also(v => v.ZoomTo(0)).Zoom);
    }

    [Fact]
    public void Panning_can_not_lose_the_picture()
    {
        var view = View();
        view.ZoomTo(1, new Point(400, 300));
        view.PanBy(new Vector(10_000, 10_000));
        Assert.Equal(new Point(0, 0), view.Offset); // the top-left corner, no further
        view.PanBy(new Vector(-100_000, -100_000));
        Assert.Equal(new Point(800 - 4000, 600 - 3000), view.Offset);

        // Smaller than the canvas, it stays inside it.
        var small = View(200, 100);
        small.PanBy(new Vector(-1000, 5000));
        Assert.Equal(new Point(0, 500), small.Offset);
    }

    [Fact]
    public void A_fitted_picture_follows_the_canvas_and_a_zoomed_one_keeps_its_zoom()
    {
        var view = View();
        view.Resize(new Size(400, 300), 1);
        Assert.Equal(0.1, view.Zoom, 9);
        view.ZoomTo(0.5);
        view.Resize(new Size(1000, 1000), 1);
        Assert.Equal(0.5, view.Zoom, 9);
        Assert.False(view.Fitted);
        view.Fit();
        Assert.Equal(0.25, view.Zoom, 9);
    }

    [Fact]
    public void Points_and_boxes_map_both_ways()
    {
        var view = View();
        var box = view.ToCanvas(new System.Windows.Rect(1000, 500, 2000, 1000));
        Assert.Equal([200, 100, 400, 200], new[] { box.X, box.Y, box.Width, box.Height }.Select(v => Math.Round(v, 9)));
        var back = view.ToPicture(box.TopLeft);
        Assert.Equal([1000, 500], new[] { back.X, back.Y }.Select(v => Math.Round(v, 9)));
    }
}

static class TestExtensions
{
    /// <summary>Does something to a value and returns it, for one-line setups.</summary>
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}

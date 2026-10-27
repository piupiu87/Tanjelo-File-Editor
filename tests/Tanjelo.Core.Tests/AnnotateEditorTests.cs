using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Tanjelo.Core.Converters;
using Tanjelo.Editors;
using Point = System.Windows.Point;
using Tool = Tanjelo.Editors.AnnotateEditor.Tool;

namespace Tanjelo.Core.Tests;

/// <summary>The annotate editor driven off-screen through the canvas's pointer methods.</summary>
public class AnnotateEditorTests
{
    static void Drag(ZoomCanvas canvas, Point from, Point to, int steps = 5)
    {
        canvas.PointerDown(canvas.View.ToCanvas(from), MouseButton.Left, ModifierKeys.None);
        for (var i = 1; i <= steps; i++)
            canvas.PointerMove(canvas.View.ToCanvas(new Point(from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps)));
        canvas.PointerUp(canvas.View.ToCanvas(to));
    }

    static void Click(ZoomCanvas canvas, Point at, int clicks = 1)
    {
        canvas.PointerDown(canvas.View.ToCanvas(at), MouseButton.Left, ModifierKeys.None, clicks);
        canvas.PointerUp(canvas.View.ToCanvas(at));
    }

    static void Close(Point expected, MarkPoint actual, double within = 3) =>
        Assert.True(Math.Abs(expected.X - actual.X) <= within && Math.Abs(expected.Y - actual.Y) <= within, $"expected about {expected}, got {actual}");

    [Fact]
    public async Task Marks_are_drawn_picked_moved_restyled_and_typed()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.png", 1200, 800);
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "annotate", [photo]);
            var editor = new AnnotateEditor(photo, target);
            var window = new EditorWindow(photo, target, editor);
            var host = Ui.Host(window, 1200, 800);
            await window.LoadAsync();
            await Ui.Settle();
            Ui.Layout(host);
            var canvas = editor.Canvas;

            Drag(canvas, new Point(200, 200), new Point(500, 350));             // the arrow is the first tool
            var arrow = Assert.Single(editor.Marks);
            Assert.Equal(MarkKind.Arrow, arrow.Kind);
            Close(new Point(200, 200), arrow.Points[0]);
            Close(new Point(500, 350), arrow.Points[1]);
            Assert.True(window.SaveButton.IsEnabled);

            editor.Use(Tool.Rectangle);
            Drag(canvas, new Point(650, 150), new Point(900, 300));
            editor.Use(Tool.Callout);
            Click(canvas, new Point(640, 500));
            Click(canvas, new Point(760, 500));
            editor.Use(Tool.Magnifier);
            Click(canvas, new Point(1000, 600));
            editor.Use(Tool.Pen);
            Drag(canvas, new Point(100, 650), new Point(400, 700), steps: 12);
            editor.Use(Tool.Ellipse);
            Click(canvas, new Point(50, 50));                                    // a click isn't a shape
            Assert.Equal([MarkKind.Arrow, MarkKind.Rectangle, MarkKind.Callout, MarkKind.Callout, MarkKind.Magnifier, MarkKind.Pen],
                editor.Marks.Select(m => m.Kind));
            Assert.True(editor.Marks[5].Points.Length > 5, "the pen keeps every point it passed");

            // Text: click, type, Enter; double-click it later to change it.
            editor.Use(Tool.Text);
            Click(canvas, new Point(100, 80));
            Assert.NotNull(editor.Typing);
            editor.Typing!.Text = "Look here";
            Ui.Layout(host);
            Ui.Render(host, "annotate-typing-light");
            editor.EndText(keep: true);
            Assert.Equal("Look here", editor.Marks[^1].Text);
            editor.Use(Tool.Select);
            Click(canvas, new Point(130, 100), clicks: 2);
            editor.Typing!.Text = "Look over here";
            editor.EndText(keep: true);
            Assert.Equal("Look over here", editor.Marks[^1].Text);

            // Pick the arrow by its middle and drag it; then pull its point.
            Click(canvas, new Point(350, 275));
            Assert.Equal(0, editor.Selected);
            Drag(canvas, new Point(350, 275), new Point(400, 295));
            Close(new Point(250, 220), editor.Marks[0].Points[0]);
            Drag(canvas, new Point(550, 370), new Point(600, 450));
            Close(new Point(600, 450), editor.Marks[0].Points[1]);
            var widths = (WrapPanel)((StackPanel)editor.Panel).Children[3];
            ((RadioButton)widths.Children[2]).IsChecked = true;                  // Thick
            Assert.True(editor.Marks[0].Width > arrow.Width, "the selected arrow is thicker now");

            // The magnifier's handle sits on its ring and resizes it.
            Click(canvas, new Point(1000, 600));
            Assert.Equal(4, editor.Selected);
            var ring = editor.Marks[4];
            Drag(canvas, new Point(1000 + ring.Size / 2, 600), new Point(1000 + ring.Size / 2 + 40, 600));
            Assert.Equal(ring.Size + 80, editor.Marks[4].Size, 0);

            Ui.Layout(host);
            Ui.Render(host, "annotate-light");
            Ui.UseTheme("dark", host);
            await Ui.Settle();
            Ui.Render(host, "annotate-dark");

            editor.DeleteSelected();
            Assert.Equal(6, editor.Marks.Count);
            editor.Undo();
            Assert.Equal(7, editor.Marks.Count);

            // What's saved is what the editor drew: the same pixels where marks cover the picture.
            var layer = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32);
            var marks = (MarksLayer)canvas.World.Children[0];
            marks.Measure(new System.Windows.Size(1200, 800));
            marks.Arrange(new System.Windows.Rect(0, 0, 1200, 800));
            layer.Render(marks);
            window.Save();
            var saved = await window.EditResult;
            var result = await Env.Engine().RunAsync([photo], saved!, ct: TestContext.Current.CancellationToken);
            using var output = new MagickImage(Assert.Single(result.Outputs));
            using var pixels = output.GetPixels();
            var shown = new byte[4];
            var compared = 0;
            foreach (var at in new[] { new Point(300, 250), new Point(330, 270), new Point(640, 515), new Point(780, 500), new Point(650, 220), new Point(250, 676) })
            {
                layer.CopyPixels(new System.Windows.Int32Rect((int)at.X, (int)at.Y, 1, 1), shown, 4, 0);
                if (shown[3] != 255) continue;
                compared++;
                var pixel = pixels.GetPixel((int)at.X, (int)at.Y);
                Assert.Equal((shown[2], shown[1], shown[0]), (pixel.GetChannel(0), pixel.GetChannel(1), pixel.GetChannel(2)));
            }
            Assert.True(compared >= 3, $"only {compared} sample points were on solid marks");
        });
    }
}

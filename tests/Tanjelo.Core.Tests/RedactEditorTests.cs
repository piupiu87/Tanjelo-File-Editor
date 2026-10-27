using System.Windows.Input;
using System.Windows.Media.Imaging;
using ImageMagick;
using Tanjelo.Core.Converters;
using Tanjelo.Editors;
using Point = System.Windows.Point;

namespace Tanjelo.Core.Tests;

/// <summary>The redaction editor driven off-screen through the canvas's pointer methods.</summary>
public class RedactEditorTests
{
    static async Task<(EditorWindow Window, RedactEditor Editor, System.Windows.FrameworkElement Host)> Open(string file)
    {
        var target = Env.Offered(Env.Engine(), "redact", [file]);
        var editor = new RedactEditor(file, target);
        var window = new EditorWindow(file, target, editor);
        var host = Ui.Host(window, 1100, 760);
        await window.LoadAsync();
        await Ui.Settle();
        Ui.Layout(host);
        return (window, editor, host);
    }

    /// <summary>A drag on the canvas from one picture pixel to another, as the mouse would make it.</summary>
    static void Drag(ZoomCanvas canvas, Point from, Point to)
    {
        canvas.PointerDown(canvas.View.ToCanvas(from), MouseButton.Left, ModifierKeys.None);
        for (var i = 1; i <= 5; i++)
            canvas.PointerMove(canvas.View.ToCanvas(new Point(from.X + (to.X - from.X) * i / 5, from.Y + (to.Y - from.Y) * i / 5)));
        canvas.PointerUp(canvas.View.ToCanvas(to));
    }

    /// <summary>Checks a box drawn by dragging, which can be a pixel out from rounding the pointer's place on screen.</summary>
    static void Near(RedactBox expected, RedactBox actual) =>
        Assert.True(Math.Abs(expected.X - actual.X) <= 1 && Math.Abs(expected.Y - actual.Y) <= 1 && Math.Abs(expected.Width - actual.Width) <= 2 && Math.Abs(expected.Height - actual.Height) <= 2,
            $"expected about {expected}, got {actual}");

    [Fact]
    public async Task Boxes_are_drawn_moved_resized_and_removed_with_undo()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.png", 800, 600);
        await Ui.Run(async () =>
        {
            var (window, editor, host) = await Open(photo);
            var canvas = editor.Canvas;
            Assert.EndsWith("800 × 600", window.Subheading.Text);

            Drag(canvas, new Point(100, 100), new Point(300, 220));
            Near(new RedactBox(100, 100, 200, 120), Assert.Single(editor.Current.Boxes));
            Assert.Equal(0, editor.Selected);
            Assert.Equal("1 area · one selected", window.StatusText.Text);
            Assert.True(window.SaveButton.IsEnabled);

            Drag(canvas, new Point(200, 160), new Point(260, 200));        // inside: moves it
            Near(new RedactBox(160, 140, 200, 120), editor.Current.Boxes[0]);
            Drag(canvas, new Point(360, 200), new Point(420, 200));        // its right edge: wider
            Near(new RedactBox(160, 140, 260, 120), editor.Current.Boxes[0]);
            canvas.PointerDown(canvas.View.ToCanvas(new Point(600, 500)), MouseButton.Left, ModifierKeys.None);
            canvas.PointerUp(canvas.View.ToCanvas(new Point(600, 500)));   // a click away from it: no new box
            Assert.Single(editor.Current.Boxes);
            Assert.Null(editor.Selected);

            Drag(canvas, new Point(500, 400), new Point(700, 560));
            Assert.Equal(2, editor.Current.Boxes.Count);
            editor.Nudge(1, 10, -5);
            Near(new RedactBox(510, 395, 200, 160), editor.Current.Boxes[1]);
            editor.RemoveSelected();
            Assert.Single(editor.Current.Boxes);

            editor.Undo();
            editor.Undo();
            editor.Undo();
            Assert.Single(editor.Current.Boxes); // back before the second box
            editor.Undo();
            editor.Undo();
            Near(new RedactBox(100, 100, 200, 120), editor.Current.Boxes[0]);
            editor.Undo();
            Assert.Empty(editor.Current.Boxes);
            Assert.False(window.SaveButton.IsEnabled);
        });
    }

    [Fact]
    public async Task The_saved_copy_has_exactly_what_the_editor_shows()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.png", 800, 600);
        await Ui.Run(async () =>
        {
            var (window, editor, host) = await Open(photo);
            var canvas = editor.Canvas;
            Drag(canvas, new Point(120, 80), new Point(420, 330));
            Drag(canvas, new Point(500, 300), new Point(760, 560));
            var chips = (System.Windows.Controls.WrapPanel)((System.Windows.Controls.StackPanel)editor.Panel).Children[4];
            ((System.Windows.Controls.RadioButton)chips.Children[1]).IsChecked = true; // Pixelate
            Assert.Equal("pixelate", editor.Current.Style);
            Ui.Layout(host);
            Ui.Render(host, "redact-pixelate-light");
            Ui.UseTheme("dark", host);
            await Ui.Settle();
            Ui.Render(host, "redact-pixelate-dark");

            window.Save();
            var saved = await window.EditResult;
            Assert.Equal("pixelate", saved!.Parameters["style"]);
            var result = await Env.Engine().RunAsync([photo], saved, ct: TestContext.Current.CancellationToken);
            using var output = new MagickImage(Assert.Single(result.Outputs));
            using var pixels = output.GetPixels();
            foreach (var (box, cover) in editor.Current.Boxes.Zip(canvas.World.Children.OfType<System.Windows.Controls.Image>()))
            {
                var source = (BitmapSource)cover.Source;
                var shown = new byte[source.PixelWidth * source.PixelHeight * 4];
                source.CopyPixels(shown, source.PixelWidth * 4, 0);
                for (var y = 0; y < box.Height; y += 7)
                    for (var x = 0; x < box.Width; x += 7)
                    {
                        var i = (y * source.PixelWidth + x) * 4;
                        var pixel = pixels.GetPixel(box.X + x, box.Y + y);
                        Assert.Equal((shown[i + 2], shown[i + 1], shown[i]), (pixel.GetChannel(0), pixel.GetChannel(1), pixel.GetChannel(2)));
                    }
            }
        });
    }

    [Fact]
    public async Task Found_faces_become_boxes_to_check()
    {
        Assert.SkipUnless(Redaction.CanFindFaces, "Windows can't look for faces on this PC");
        using var scratch = new Scratch();
        var portrait = scratch.PathOf("portrait.jpg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "portrait.jpg"), portrait);
        await Ui.Run(async () =>
        {
            var (window, editor, host) = await Open(portrait);
            await editor.FindAsync(faces: true);
            var face = Assert.Single(editor.Current.Boxes);
            Assert.True(face.Width > 150 && face.Height > 150, $"the face's box is {face}");
            await editor.FindAsync(faces: true); // the same face again isn't added twice
            Assert.Single(editor.Current.Boxes);
            Ui.Layout(host);
            Ui.Render(host, "redact-face-light");
        });
    }
}

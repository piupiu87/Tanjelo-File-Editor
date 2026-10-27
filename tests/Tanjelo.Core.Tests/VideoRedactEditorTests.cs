using System.Windows.Input;
using Tanjelo.Core.Converters;
using Tanjelo.Editors;
using Point = System.Windows.Point;

namespace Tanjelo.Core.Tests;

/// <summary>The video redaction editor driven off-screen: boxes on the picture, their bars on the timeline, keys, faces.</summary>
[Collection("Windows player")]
public class VideoRedactEditorTests
{
    /// <summary>A drag on the picture from one pixel to another, as the mouse would make it.</summary>
    static void Drag(ZoomCanvas canvas, Point from, Point to)
    {
        canvas.PointerDown(canvas.View.ToCanvas(from), MouseButton.Left, ModifierKeys.None);
        for (var i = 1; i <= 5; i++)
            canvas.PointerMove(canvas.View.ToCanvas(new Point(from.X + (to.X - from.X) * i / 5, from.Y + (to.Y - from.Y) * i / 5)));
        canvas.PointerUp(canvas.View.ToCanvas(to));
    }

    [Fact]
    public async Task Boxes_are_drawn_on_the_picture_and_timed_on_their_bars()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 4);       // 25 frames a second
        await Ui.Run(async () =>
        {
            var (window, editor, host) = await TrimEditorTests.Open(clip, "redact", (file, target) => new VideoRedactEditor(file, target, Env.Tools));
            try
            {
                var canvas = editor.Canvas!;
                TimelineTests.Enter(editor.TimeBox, "1");
                await editor.Seek();
                Drag(canvas, new Point(40, 60), new Point(140, 140));
                var box = Assert.Single(editor.Current.Boxes);
                Assert.Equal((40, 60, 100, 80, 1.0, (double?)null), (box.X, box.Y, box.Width, box.Height, box.From, box.To));
                Assert.Equal(0, editor.Selected);
                Assert.Single(canvas.World.Children);                  // hidden on this frame
                Assert.Equal("1 area · one selected", window.StatusText.Text);

                // Before its start it isn't hidden.
                editor.Press(Key.Home);
                Assert.Empty(canvas.World.Children);

                // Its bar's end, dragged to 2 s: hidden up to frame 49.
                TrimEditorTests.Drag(editor.View, editor.View.Duration, 2.0, TimelineLane.Rows);
                Assert.Equal(2.0, editor.Current.Boxes[0].To!.Value, 6);
                Assert.Equal(49, editor.Frame);                        // the last frame it hides is showing

                // I and O at the playhead.
                TimelineTests.Enter(editor.TimeBox, "1.52");
                editor.Press(Key.I);
                Assert.Equal(1.52, editor.Current.Boxes[0].From, 6);
                TimelineTests.Enter(editor.TimeBox, "2.4");
                editor.Press(Key.O);
                Assert.Equal(2.44, editor.Current.Boxes[0].To!.Value, 6);   // after frame 60, which shows at 2.4
                editor.Undo();
                Assert.Equal(2.0, editor.Current.Boxes[0].To!.Value, 6);

                // Pixelated, the box shows exactly what's saved, made from the frame showing.
                TimelineTests.Enter(editor.TimeBox, "1.8");
                await editor.Seek();
                editor.Restyle("pixelate");
                Assert.IsType<System.Windows.Controls.Image>(Assert.Single(canvas.World.Children));
                Ui.Render(host, "redact-video-light");
                Ui.UseTheme("dark", host);
                await Ui.Settle();
                Ui.Render(host, "redact-video-dark");
                Ui.UseTheme("light", host);

                var saved = editor.Result();
                Assert.Equal("pixelate", saved.Parameters["style"]);
                var boxes = VideoBoxSpec.Read(saved.Parameters["spec"], clip);
                Assert.Equal((1.52, (double?)2.0), (boxes[0].From, boxes[0].To));
                var result = await Task.Run(() => Env.Engine().RunAsync([clip], saved));
                Assert.Empty(result.Failures);
                Assert.Equal(["1 area hidden"], result.Notes);

                editor.Press(Key.Tab);
                editor.Press(Key.Delete);
                Assert.Empty(editor.Current.Boxes);
                Assert.False(window.SaveButton.IsEnabled);
            }
            finally
            {
                editor.Close();
            }
        });
    }

    [Fact]
    public async Task Find_faces_adds_a_box_over_each_face_on_the_frame_showing()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        Assert.SkipUnless(Redaction.CanFindFaces, "Windows can't look for faces on this PC");
        using var scratch = new Scratch();
        var portrait = Path.Combine(AppContext.BaseDirectory, "Fixtures", "portrait.jpg");
        var clip = scratch.Ffmpeg("face.mp4", ["-loop", "1", "-i", portrait, "-vf", "scale=-2:480,pad=ceil(iw/2)*2:ih", "-t", "2", "-r", "25", "-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        await Ui.Run(async () =>
        {
            var (_, editor, host) = await TrimEditorTests.Open(clip, "redact", (file, target) => new VideoRedactEditor(file, target, Env.Tools));
            try
            {
                TimelineTests.Enter(editor.TimeBox, "0:01");
                await editor.FindFacesAsync();
                var box = Assert.Single(editor.Current.Boxes);
                Assert.Equal(1.0, box.From, 6);
                Assert.StartsWith("1 face found", editor.Found);
                await editor.Seek();
                await Ui.Settle();
                Ui.Render(host, "redact-video-face-light");
            }
            finally
            {
                editor.Close();
            }
        });
    }
}

using System.Windows.Input;
using System.Windows.Media.Imaging;
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

                // Before its start it isn't hidden, once that frame shows.
                editor.Press(Key.Home);
                await editor.Seek();
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

                editor.Press(Key.Escape);
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

    /// <summary>Every frame of a video, as ffmpeg decodes it, in BGRA.</summary>
    static List<byte[]> AllFrames(string video, int width, int height)
    {
        using var temp = new Scratch();
        var raw = File.ReadAllBytes(temp.Ffmpeg("all.bgra", ["-i", video, "-map", "0:V:0", "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "bgra"]));
        var size = width * height * 4;
        return Enumerable.Range(0, raw.Length / size).Select(i => raw.AsSpan(i * size, size).ToArray()).ToList();
    }

    /// <summary>A part of a BGRA picture.</summary>
    static byte[] Crop(byte[] pixels, int width, System.Windows.Int32Rect area)
    {
        var part = new byte[area.Width * area.Height * 4];
        for (var y = 0; y < area.Height; y++)
            Array.Copy(pixels, ((area.Y + y) * width + area.X) * 4, part, y * area.Width * 4, area.Width * 4);
        return part;
    }

    [Fact]
    public async Task While_playing_each_box_is_drawn_on_the_frame_showing_and_made_from_it()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 3);       // 25 frames a second, 320 by 240
        await Ui.Run(async () =>
        {
            var (_, editor, _) = await TrimEditorTests.Open(clip, "redact", (file, target) => new VideoRedactEditor(file, target, Env.Tools));
            try
            {
                if (editor.Preview is not FramePlayer player) { Assert.Skip("Windows' player can't play video on this PC"); return; }
                var canvas = editor.Canvas!;
                TimelineTests.Enter(editor.TimeBox, "1");
                await editor.Seek();
                Drag(canvas, new Point(100, 60), new Point(260, 200));  // hidden from frame 25 on
                editor.Restyle("pixelate");
                TimelineTests.Enter(editor.TimeBox, "0.4");
                await editor.Seek();

                // Each picture as it shows, with the cover on it and what's in the cover; and whether there's a cover
                // or not changes before the next picture.
                System.Windows.UIElement? Cover() => canvas.World.Children.Count > 0 ? canvas.World.Children[0] : null;
                var shown = new List<(byte[] Pixels, System.Windows.UIElement? Cover, byte[]? Blocks)>();
                var between = 0;
                void Picture() => shown.Add((TimelineTests.Pixels(player.Picture!), Cover(),
                    Cover() is System.Windows.Controls.Image { Source: BitmapSource made } ? TimelineTests.Pixels(made) : null));
                void Rendering(object? sender, EventArgs e)
                {
                    if (shown.Count > 0 && shown[^1].Cover is null != Cover() is null) between++;
                }
                player.PictureChanged += Picture;
                System.Windows.Media.CompositionTarget.Rendering += Rendering;
                editor.Press(Key.Space);
                await Task.Delay(1200);
                player.PictureChanged -= Picture;
                System.Windows.Media.CompositionTarget.Rendering -= Rendering;
                editor.Press(Key.Space);

                // The clock moves on a moment before each frame shows; the boxes wait for the frame.
                Assert.Equal(0, between);
                Assert.True(shown.Count > 15, $"only {shown.Count} pictures showed");
                var frames = await Task.Run(() => AllFrames(clip, 320, 240));
                var area = new System.Windows.Int32Rect(100, 60, 160, 140);
                var covered = 0;
                foreach (var (pixels, cover, blocks) in shown)
                {
                    var n = Enumerable.Range(5, 45).MinBy(i => Inspect.Difference(pixels, frames[i]));
                    Assert.True(n >= 25 == cover is not null, $"frame {n} is {(cover is null ? "not " : "")}covered");
                    if (cover is null) continue;
                    // Pixelated from that frame, as the saved copy is, not shown as a plain stand-in while playing.
                    var expected = Crop(frames[n], 320, area);
                    VideoRedactTool.PreviewCover(expected, area.Width, area.Height, area.Width, area.Height, "pixelate");
                    Assert.NotNull(blocks);
                    Assert.True(Inspect.Difference(blocks, expected) < 4, $"the blocks on frame {n} differ by {Inspect.Difference(blocks, expected):0.0}");
                    covered++;
                }
                Assert.True(covered > 5, $"only {covered} covered pictures showed");
                // The same cover throughout, changed in place: one put on anew for every frame sometimes didn't show.
                Assert.Single(shown.Where(s => s.Cover is not null).Select(s => s.Cover).Distinct());
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

    [Fact]
    public async Task Follow_face_is_there_only_for_a_box_with_a_face_in_it()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        Assert.SkipUnless(Redaction.CanFindFaces && FaceFollow.CanFollow, "Windows can't find or follow faces on this PC");
        using var scratch = new Scratch();
        var portrait = Path.Combine(AppContext.BaseDirectory, "Fixtures", "portrait.jpg");
        var clip = scratch.Ffmpeg("face.mp4", ["-loop", "1", "-i", portrait, "-vf", "scale=-2:480,pad=ceil(iw/2)*2:ih", "-t", "2", "-r", "25", "-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        await Ui.Run(async () =>
        {
            var (_, editor, _) = await TrimEditorTests.Open(clip, "redact", (file, target) => new VideoRedactEditor(file, target, Env.Tools));
            try
            {
                Assert.False(editor.CanFollowNow);                   // nothing selected
                TimelineTests.Enter(editor.TimeBox, "0:01");
                await editor.FindFacesAsync();
                Assert.Equal(0, editor.Selected);
                await editor.FaceLook;
                Assert.True(editor.CanFollowNow);

                // A box drawn over the shirt, at the bottom left: no face to follow there.
                Drag(editor.Canvas!, new Point(8, 390), new Point(90, 470));
                Assert.Equal(1, editor.Selected);
                Assert.False(editor.CanFollowNow);
                await editor.FaceLook;
                Assert.False(editor.CanFollowNow);

                editor.Press(Key.Tab, ModifierKeys.Shift);
                Assert.Equal(0, editor.Selected);
                await editor.FaceLook;
                Assert.True(editor.CanFollowNow);
                editor.Press(Key.Escape);
                Assert.False(editor.CanFollowNow);
            }
            finally
            {
                editor.Close();
            }
        });
    }
}

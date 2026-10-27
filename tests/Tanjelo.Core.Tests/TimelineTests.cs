using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Tanjelo.Core.Converters;
using Tanjelo.Editors;
using Point = System.Windows.Point;

namespace Tanjelo.Core.Tests;

/// <summary>The timeline the audio and video editors share: times, frames, sound, pictures, playback and the window.</summary>
[Collection("Windows player")]
public class TimelineTests
{
    /// <summary>A timeline editor with nothing of its own, to try the timeline with.</summary>
    sealed class PlainTimeline(string file) : TimelineEditor<int>(file, Plain, Env.Tools)
    {
        public static readonly Target Plain = new("timeline", "Timeline…", "Just the timeline", Section.Video) { Kind = TargetKind.Tool };

        protected override int Initial() => 0;
        protected override void Show(int state) { }
        public override Target Result() => Plain;
        internal FrameIndex? Index => Frames;
        internal TimelineView TimelineForTests => Timeline;
    }

    static async Task<(EditorWindow Window, PlainTimeline Editor, FrameworkElement Host)> Open(string file)
    {
        var editor = new PlainTimeline(file);
        var window = new EditorWindow(file, PlainTimeline.Plain, editor);
        var host = Ui.Host(window, 1100, 760);
        await window.LoadAsync();
        await editor.Loaded;
        await editor.Seek();
        await Ui.Settle();
        Ui.Layout(host);
        return (window, editor, host);
    }

    /// <summary>Which frame of a numbered video the editor shows, of the frame it should show and its neighbours.</summary>
    internal static async Task<int> Showing(IMediaPreview preview, string video, int expected)
    {
        var picture = preview.Picture!;
        var (width, height) = (picture.PixelWidth, picture.PixelHeight);
        var pixels = Pixels(picture);
        // ffmpeg is run off the UI thread, which waits on nothing.
        return await Task.Run(() => Inspect.WhichFrame(pixels, width, height, video, new[] { expected - 1, expected, expected + 1 }.Where(n => n >= 0)));
    }

    static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        converted.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    /// <summary>Presses Enter in a text box, as typing a time and pressing Enter does.</summary>
    internal static void Enter(System.Windows.Controls.TextBox box, string text)
    {
        box.Text = text;
        box.Focus();
        box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Enter) { RoutedEvent = UIElement.KeyDownEvent });
    }

    /// <summary>A source for key events made by hand; nothing is shown.</summary>
    sealed class FakeSource : PresentationSource
    {
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget? GetCompositionTargetCore() => null;
    }

    [Theory]
    [InlineData(0, "0:00:00.000")]
    [InlineData(62.345, "0:01:02.345")]
    [InlineData(30.0300300, "0:00:30.030")]
    [InlineData(3723.5, "1:02:03.500")]
    public void Times_show_hours_minutes_seconds_and_milliseconds(double seconds, string text) => Assert.Equal(text, Timecode.Format(seconds));

    [Theory]
    [InlineData("62.5", 62.5)]
    [InlineData("1:02.5", 62.5)]
    [InlineData("0:01:02.500", 62.5)]
    [InlineData(" 1:02:03 ", 3723)]
    public void Typed_times_are_read_with_or_without_hours(string text, double seconds)
    {
        Assert.True(Timecode.TryParse(text, out var read));
        Assert.Equal(seconds, read, 6);
    }

    [Theory]
    [InlineData("1:75")]
    [InlineData("soon")]
    [InlineData("")]
    public void Nonsense_times_are_refused(string text) => Assert.False(Timecode.TryParse(text, out _));

    [Fact]
    public void Times_snap_to_frames_at_29_97()
    {
        var frames = FrameIndex.Estimate(60, 30000 / 1001.0);
        Assert.Equal(1798, frames.Count);
        Assert.Equal(30.03, frames.Start(900), 9);         // 900 × 1001 / 30000
        Assert.Equal(899, frames.At(30.0));            // frame 899 is still showing at 0:30
        Assert.Equal(899, frames.Nearest(30.0));       // 29.997 is nearer than 30.030
        Assert.Equal(900, frames.Nearest(30.02));
        Assert.Equal(frames.Start(899), frames.Snap(30.0), 9);
        Assert.Equal(frames.End, frames.Snap(59.99999));
        Assert.Equal(frames.Start(10) + (frames.Start(11) - frames.Start(10)) / 4, frames.Inside(10), 9);
        Assert.True(frames.Estimated);
    }

    [Fact]
    public async Task Frames_and_keyframes_are_read_in_the_order_they_show()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 4, rate: "30000/1001", audio: false,
            encode: ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "30", "-sc_threshold", "0", "-bf", "2"]);
        var media = await TimelineMedia.OpenAsync(Env.Tools, clip, TestContext.Current.CancellationToken);

        var frames = await media.LoadFramesAsync(null, TestContext.Current.CancellationToken);

        Assert.False(frames.Estimated);
        Assert.Equal(Inspect.FrameCount(clip), frames.Count);
        Assert.Equal([0, 30, 60, 90], frames.Keyframes);
        Assert.All(Enumerable.Range(0, frames.Count), n => Assert.Equal(n * 1001 / 30000.0, frames.Start(n), 3));
        Assert.Equal(60, frames.KeyAtOrBefore(75));
        Assert.Equal(90, frames.KeyAfter(60));
        Assert.Null(frames.KeyAfter(90));
        Assert.Equal(frames.Count * 1001 / 30000.0, frames.End, 3);
    }

    [Fact]
    public async Task Frames_of_a_variable_frame_rate_video_keep_their_own_times()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        // 25 frames a second with frames 10 to 14 left out: a gap of a fifth of a second.
        var clip = scratch.Numbered("vfr.mp4", seconds: 4, audio: false,
            encode: ["-vf", "select='not(between(n,10,14))'", "-fps_mode", "passthrough", "-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        var media = await TimelineMedia.OpenAsync(Env.Tools, clip, TestContext.Current.CancellationToken);

        var frames = await media.LoadFramesAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(95, frames.Count);
        Assert.Equal(0.36, frames.Start(9), 3);
        Assert.Equal(0.6, frames.Start(10), 3);       // what was frame 15
        Assert.Equal(0.6, frames.Stop(9), 3);         // frame 9 shows for the whole gap
        Assert.Equal(9, frames.At(0.59));
        Assert.Equal(10, frames.Nearest(0.5));        // 0.6 is nearer than 0.36
        Assert.Equal(0.6, frames.Snap(0.5), 3);
    }

    [Fact]
    public async Task Stills_from_ffmpeg_show_the_exact_frame()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 4, rate: "30000/1001", audio: false);
        var media = await TimelineMedia.OpenAsync(Env.Tools, clip, TestContext.Current.CancellationToken);
        var frames = await media.LoadFramesAsync(null, TestContext.Current.CancellationToken);
        await Ui.Run(async () =>
        {
            using var stills = new StillsPreview(media, () => frames);
            foreach (var n in new[] { 0, 1, 31, 100, frames.Count - 1 })
            {
                await stills.ShowAsync(frames.Inside(n));
                Assert.Equal(n, await Showing(stills, clip, n));
            }
        });
    }

    [Fact]
    public async Task Windows_player_seeks_and_steps_to_the_exact_frame()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 4, rate: "30000/1001");
        var media = await TimelineMedia.OpenAsync(Env.Tools, clip, TestContext.Current.CancellationToken);
        var frames = await media.LoadFramesAsync(null, TestContext.Current.CancellationToken);
        await Ui.Run(async () =>
        {
            using var player = await FramePlayer.OpenAsync(clip, true, frames.Inside(0), TestContext.Current.CancellationToken);
            if (player is null) Assert.Skip("Windows' player can't play video on this PC");
            Assert.Equal(0, await Showing(player, clip, 0));
            // Back and forth by one frame, and jumps, each a seek a quarter of the way into the frame.
            foreach (var n in new[] { 1, 2, 1, 45, 46, 45, 100, 0 })
            {
                await player.ShowAsync(frames.Inside(n));
                Assert.Equal(n, await Showing(player, clip, n));
            }
        });
    }

    [Fact]
    public async Task The_window_moves_frame_by_frame_with_the_keys_and_goes_to_typed_times()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 4, rate: "30000/1001");
        await Ui.Run(async () =>
        {
            var (_, editor, _) = await Open(clip);
            try
            {
                var frames = editor.Index!;
                Assert.False(frames.Estimated);
                Assert.Equal("frame 1 of 119", editor.Where);
                Assert.Equal("0:00:00.000", editor.TimeBox.Text);

                editor.Press(Key.Right);
                editor.Press(Key.Right);
                await editor.Seek();
                Assert.Equal(2, editor.Frame);
                Assert.Equal(2, await Showing(editor.Preview!, clip, 2));
                Assert.Equal("0:00:00.067", editor.TimeBox.Text);

                editor.Press(Key.Right, ModifierKeys.Shift);       // a second on: 1.0667 s is in frame 31
                Assert.Equal(frames.At(frames.Start(2) + 1), editor.Frame);
                editor.Press(Key.Left);
                Assert.Equal(frames.At(frames.Start(2) + 1) - 1, editor.Frame);

                // A typed time goes to the frame that starts nearest to it: 1.5 s is 44.96 frames in.
                Enter(editor.TimeBox, "0:01.5");
                await editor.Seek();
                Assert.Equal(45, editor.Frame);
                Assert.Equal(45, await Showing(editor.Preview!, clip, 45));

                editor.Press(Key.End);
                await editor.Seek();
                Assert.Equal(frames.Count - 1, editor.Frame);
                Assert.Equal(frames.Count - 1, await Showing(editor.Preview!, clip, frames.Count - 1));
                editor.Press(Key.Home);
                Assert.Equal(0, editor.Frame);
                Assert.Equal("frame 1 of 119", editor.Where);
            }
            finally
            {
                editor.Close();
            }
        });
    }

    [Fact]
    public async Task The_timeline_scrubs_zooms_around_the_pointer_and_renders_in_light_and_dark()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 12, size: "640x360");
        await Ui.Run(async () =>
        {
            var (window, editor, host) = await Open(clip);
            try
            {
                var view = editor.TimelineForTests;
                Assert.True(view.ThumbnailCount >= 10, $"{view.ThumbnailCount} thumbnails");
                Assert.NotNull(view.Sound);
                Assert.Contains("640 × 360 · 25 fps · 0:12", window.Subheading.Text);
                Assert.Equal("", window.StatusText.Text);   // everything has loaded

                // A click on the times moves the playhead there, to the frame showing then.
                var ruler = view.Band(TimelineLane.Ruler);
                var at = new Point(view.XAt(5.01), ruler.Top + 10);
                view.PointerDown(at, ModifierKeys.None);
                view.PointerMove(new Point(view.XAt(6.03), at.Y));
                view.PointerUp(new Point(view.XAt(6.03), at.Y));
                Assert.Equal(150, editor.Frame);
                Assert.Equal(6.0, editor.Playhead, 3);
                await editor.Seek();
                await Ui.Settle();

                Ui.Render(host, "timeline-light");
                Ui.UseTheme("dark", host);
                await Ui.Settle();
                Ui.Render(host, "timeline-dark");
                Ui.UseTheme("light", host);

                // The wheel zooms around the pointer; Shift+wheel scrolls.
                var sound = view.Band(TimelineLane.Sound);
                var pointer = new Point(view.XAt(3), sound.Top + 10);
                view.Wheel(pointer, 600, ModifierKeys.None);
                Assert.False(view.Fitted);
                Assert.Equal(3, view.TimeAt(pointer.X), 6);
                var start = view.ViewStart;
                view.Wheel(pointer, -120, ModifierKeys.Shift);
                Assert.True(view.ViewStart > start);
                Ui.Render(host, "timeline-zoomed-light");
                view.Fit();
                Assert.Equal(0, view.ViewStart);
            }
            finally
            {
                editor.Close();
            }
        });
    }

    [Fact]
    public async Task Sound_on_its_own_fills_the_timeline_with_its_shape()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        // A second of tone, then a second of silence.
        var song = scratch.Tone("song.mp3", seconds: 1, channels: 2, filter: "apad=pad_dur=1");
        await Ui.Run(async () =>
        {
            var (window, editor, host) = await Open(song);
            try
            {
                var view = editor.TimelineForTests;
                Assert.False(view.ShowPictures);
                var sound = view.Sound!;
                Assert.InRange(sound.Duration, 1.95, 2.1);
                Assert.True(sound.Peak(0.2, 0.8) > 0.05, $"the tone peaks at {sound.Peak(0.2, 0.8)}");
                Assert.True(sound.Peak(1.2, 1.8) < 0.01, $"the silence peaks at {sound.Peak(1.2, 1.8)}");
                Assert.Null(editor.Canvas);
                Assert.Contains("44.1 kHz · stereo · 0:02", window.Subheading.Text);
                Assert.True(view.Band(TimelineLane.Sound).Height > 300, "the sound should take the room");

                editor.Press(Key.Right);
                Assert.Equal(0.01, editor.Playhead, 6);
                editor.Press(Key.Right, ModifierKeys.Shift);
                Assert.Equal(1.01, editor.Playhead, 6);
                Assert.StartsWith("of 0:00:02.0", editor.Where);
                Ui.Render(host, "timeline-sound-light");
                Ui.UseTheme("dark", host);
                await Ui.Settle();
                Ui.Render(host, "timeline-sound-dark");
            }
            finally
            {
                editor.Close();
            }
        });
    }

    [Fact]
    public async Task Thumbnails_come_from_the_keyframes_in_order()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 4, audio: false);   // a keyframe every second
        var media = await TimelineMedia.OpenAsync(Env.Tools, clip, TestContext.Current.CancellationToken);
        var thumbnails = new List<Thumbnail>();

        await media.ThumbnailsAsync(40, 0.5, thumbnails.Add, TestContext.Current.CancellationToken);

        Assert.Equal([0.0, 1, 2, 3], thumbnails.Select(t => Math.Round(t.Time, 2)));
        Assert.All(thumbnails, t => Assert.Equal((54, 40, 54 * 40 * 4), (t.Width, t.Height, t.Pixels.Length)));
    }

    [Fact]
    public async Task A_video_Windows_cant_play_shows_stills_then_plays_a_copy()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("old.mpg", seconds: 3, encode: ["-c:v", "mpeg2video", "-q:v", "3", "-c:a", "mp2"]);
        var playable = scratch.Numbered("new.mp4", seconds: 1, audio: false);
        await Ui.Run(async () =>
        {
            using (var probe = await FramePlayer.OpenAsync(playable, true, 0.02, TestContext.Current.CancellationToken))
                if (probe is null) Assert.Skip("Windows' player can't play video on this PC");
            using (var direct = await FramePlayer.OpenAsync(clip, true, 0.02, TestContext.Current.CancellationToken))
                if (direct is not null) Assert.Skip("Windows plays MPEG-2 on this PC");

            var editor = new PlainTimeline(clip);
            var window = new EditorWindow(clip, PlainTimeline.Plain, editor);
            Ui.Host(window, 1100, 760);
            try
            {
                await window.LoadAsync();
                Assert.IsType<StillsPreview>(editor.Preview);
                Assert.Equal(0, await Showing(editor.Preview!, clip, 0));
                await editor.Loaded;
                Assert.True(editor.PlaysCopy);
                Assert.IsType<FramePlayer>(editor.Preview);
                editor.Press(Key.Right, ModifierKeys.Shift);
                editor.Press(Key.Right);
                await editor.Seek();
                // The copy is smaller, but the frames are the original's.
                Assert.Equal(26, editor.Frame);
                Assert.Equal(26, await Showing(editor.Preview!, clip, 26));
            }
            finally
            {
                editor.Close();
            }
        });
    }
}

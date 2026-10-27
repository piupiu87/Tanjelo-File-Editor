using System.Windows;
using System.Windows.Input;
using Tanjelo.Editors;
using Point = System.Windows.Point;

namespace Tanjelo.Core.Tests;

/// <summary>The trim editor driven off-screen: handles, keys, typed times, snapping and what it hands the engine.</summary>
[Collection("Windows player")]
public class TrimEditorTests
{
    internal static async Task<(EditorWindow Window, T Editor, FrameworkElement Host)> Open<T>(string file, string key, Func<string, Target, T> make)
        where T : Editor, ITimelineEditor
    {
        var target = Env.Offered(Env.Engine(), key, [file]);
        var editor = make(file, target);
        var window = new EditorWindow(file, target, editor);
        var host = Ui.Host(window, 1100, 760);
        await window.LoadAsync();
        await editor.Loaded;
        await Ui.Settle();
        Ui.Layout(host);
        return (window, editor, host);
    }

    /// <summary>A drag along the timeline from one time to another, on a band, as the mouse would make it.</summary>
    internal static void Drag(TimelineView view, double from, double to, TimelineLane lane = TimelineLane.Sound, int row = 0)
    {
        var (top, height) = view.Band(lane, row);
        var y = top + height / 2;
        view.PointerDown(new Point(view.XAt(from), y), ModifierKeys.None);
        for (var i = 1; i <= 5; i++)
            view.PointerMove(new Point(view.XAt(from + (to - from) * i / 5), y), ModifierKeys.None);
        view.PointerUp(new Point(view.XAt(to), y));
    }

    static double FrameTime(int n) => n * 1001 / 30000.0;

    [Fact]
    public async Task Handles_keys_and_typed_times_set_the_part_kept()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 5, rate: "30000/1001",
            encode: ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "30", "-sc_threshold", "0", "-c:a", "aac"]);
        await Ui.Run(async () =>
        {
            var (window, editor, host) = await Open(clip, "trim", (file, target) => new TrimEditor(file, target, Env.Tools));
            try
            {
                var view = editor.View;
                Assert.Equal(0, editor.Current.Start);
                Assert.False(window.SaveButton.IsEnabled);          // all of it is kept
                Assert.True(editor.Lossless);

                // The start handle, dragged to 1.5 s, lands between frames 44 and 45; the end, to 3.2 s, before frame 96.
                Drag(view, 0, 1.5, TimelineLane.Pictures);
                Assert.Equal(FrameTime(45), editor.Current.Start, 6);
                Assert.Equal(45, editor.Frame);                     // the first frame kept is showing
                Drag(view, editor.Current.End, 3.2);
                Assert.Equal(FrameTime(96), editor.Current.End, 6);
                Assert.Equal(95, editor.Frame);                     // and then the last
                await editor.Seek();
                Assert.Equal(95, await TimelineTests.Showing(editor.Preview!, clip, 95));
                Assert.False(editor.Lossless);
                Assert.StartsWith("The part kept is re-encoded", editor.Saving);
                Assert.Equal("Keeps 0:00:01.702 · 51 frames", editor.Kept);
                Assert.True(window.SaveButton.IsEnabled);
                Ui.Render(host, "trim-video-light");
                Ui.UseTheme("dark", host);
                await Ui.Settle();
                Ui.Render(host, "trim-video-dark");
                Ui.UseTheme("light", host);

                // A press away from the handles moves the playhead.
                var (top, height) = view.Band(TimelineLane.Sound);
                view.PointerDown(new Point(view.XAt(2.5), top + height / 2), ModifierKeys.None);
                view.PointerUp(new Point(view.XAt(2.5), top + height / 2));
                Assert.Equal(74, editor.Frame);
                Assert.Equal(FrameTime(96), editor.Current.End, 6);

                // Each drag is one step to undo.
                editor.Undo();
                Assert.Equal(FrameTime(45), editor.Current.Start, 6);
                Assert.NotEqual(FrameTime(96), editor.Current.End, 6);
                editor.Undo();
                Assert.Equal(0, editor.Current.Start);
                editor.Redo();
                editor.Redo();

                // I and O at the playhead: the frame at the playhead is the last one kept.
                TimelineTests.Enter(editor.TimeBox, "2.002");
                editor.Press(Key.I);
                Assert.Equal(FrameTime(60), editor.Current.Start, 6);
                TimelineTests.Enter(editor.TimeBox, "0:03.97");      // frame 119 starts at 3.9706
                editor.Press(Key.O);
                Assert.Equal(FrameTime(120), editor.Current.End, 6);
                Assert.True(editor.Lossless);
                Assert.StartsWith("Both cuts are on keyframes", editor.Saving);

                // A typed start goes to the nearest frame.
                TimelineTests.Enter(editor.FromBox, "0:01.51");
                Assert.Equal(FrameTime(45), editor.Current.Start, 6);

                // Snapping takes dragged handles to keyframes.
                editor.SnapBox.IsChecked = true;
                Drag(view, editor.Current.Start, 1.3);
                Assert.Equal(FrameTime(30), editor.Current.Start, 6);
                Drag(view, editor.Current.End, 3.2);
                Assert.Equal(FrameTime(90), editor.Current.End, 6);
                Assert.True(editor.Lossless);

                var saved = editor.Result();
                Assert.Equal("0:01.001-0:03.003", saved.Parameters["range"]);
                Assert.Equal("auto", saved.Parameters["mode"]);
                var result = await Task.Run(() => Env.Engine().RunAsync([clip], saved));
                var output = Assert.Single(result.Outputs);
                Assert.Equal(60, await Task.Run(() => Inspect.FrameCount(output)));
                Assert.Equal(["Copied without re-encoding: both cuts are on keyframes"], result.Notes);
            }
            finally
            {
                editor.Close();
            }
        });
    }
}

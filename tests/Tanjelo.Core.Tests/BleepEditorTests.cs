using System.Windows.Input;
using Tanjelo.Editors;
using Point = System.Windows.Point;

namespace Tanjelo.Core.Tests;

/// <summary>The bleep editor driven off-screen: stretches drawn, moved and resized on the sound, keys, and what plays.</summary>
[Collection("Windows player")]
public class BleepEditorTests
{
    [Fact]
    public async Task Stretches_are_drawn_on_the_sound_and_the_sound_playing_has_them()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var song = scratch.Tone("song.wav", seconds: 4, channels: 2);
        await Ui.Run(async () =>
        {
            var (window, editor, host) = await TrimEditorTests.Open(song, "bleep", (file, target) => new BleepEditor(file, target, Env.Tools));
            try
            {
                var view = editor.View;
                Assert.False(window.SaveButton.IsEnabled);

                TrimEditorTests.Drag(view, 1.0, 1.5);                 // a new stretch
                Assert.Equal([(1.0, 1.5)], editor.Current.Ranges);
                Assert.Equal(0, editor.Selected);
                TrimEditorTests.Drag(view, 1.5, 1.8);                 // its end
                TrimEditorTests.Drag(view, 1.4, 1.6);                 // the middle: moved on by 0.2 s
                Assert.Equal([(1.2, 2.0)], editor.Current.Ranges);
                Assert.Equal("1 stretch · one selected", window.StatusText.Text);
                Assert.True(window.SaveButton.IsEnabled);

                // A click on the sound away from the stretches moves the playhead.
                var (top, height) = view.Band(TimelineLane.Sound);
                view.PointerDown(new Point(view.XAt(3), top + height / 2), ModifierKeys.None);
                view.PointerUp(new Point(view.XAt(3), top + height / 2));
                Assert.Equal(3, editor.Playhead, 3);
                Assert.Null(editor.Selected);

                // O with nothing selected marks the second before the playhead; Delete removes the selected one.
                editor.Press(Key.O);
                Assert.Equal([(1.2, 2.0), (2.0, 3.0)], editor.Current.Ranges);
                editor.Press(Key.Delete);
                Assert.Equal([(1.2, 2.0)], editor.Current.Ranges);

                editor.Undo();
                editor.Undo();
                Assert.Equal([(1.2, 2.0)], editor.Current.Ranges);

                await editor.SoundReady;
                if (editor.Preview is FramePlayer player)
                    Assert.NotEqual(song, player.Source);              // plays a copy with the bleeps in
                Ui.Render(host, "bleep-sound-light");
                Ui.UseTheme("dark", host);
                await Ui.Settle();
                Ui.Render(host, "bleep-sound-dark");
                Ui.UseTheme("light", host);

                var saved = editor.Result();
                Assert.Equal("0:01.2-0:02", saved.Parameters["ranges"]);
                Assert.Equal(("tone", "1000"), (saved.Parameters["sound"], saved.Parameters["pitch"]));
            }
            finally
            {
                editor.Close();
            }
        });
    }

    [Fact]
    public async Task A_video_is_bleeped_on_its_own_timeline()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("talk.mp4", seconds: 4);
        await Ui.Run(async () =>
        {
            var (_, editor, host) = await TrimEditorTests.Open(clip, "bleep", (file, target) => new BleepEditor(file, target, Env.Tools));
            try
            {
                Assert.True(editor.View.ShowPictures);
                TrimEditorTests.Drag(editor.View, 0.95, 1.2);
                await editor.SoundReady;
                Ui.Render(host, "bleep-video-light");
                Ui.UseTheme("dark", host);
                await Ui.Settle();
                Ui.Render(host, "bleep-video-dark");
                Ui.UseTheme("light", host);
                Assert.Equal("0:00.95-0:01.2", editor.Result().Parameters["ranges"]);
            }
            finally
            {
                editor.Close();
            }
        });
    }
}

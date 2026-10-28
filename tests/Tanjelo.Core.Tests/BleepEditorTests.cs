using System.Globalization;
using System.Windows.Input;
using Tanjelo.Core.Converters;
using Tanjelo.Editors;
using Point = System.Windows.Point;

namespace Tanjelo.Core.Tests;

/// <summary>The bleep editor driven off-screen: stretches drawn and changed on the sound, each with its cover, and what plays.</summary>
[Collection("Windows player")]
public class BleepEditorTests
{
    /// <summary>The level in dB of a file's sound between two times, around a pitch.</summary>
    static double Level(string path, double from, double to, int hertz)
    {
        var result = ProcessRunner.RunAsync(Env.Tools.Ffmpeg!,
            ["-hide_banner", "-nostdin", "-nostats", "-v", "info", "-i", path, "-af",
                string.Create(CultureInfo.InvariantCulture, $"atrim={from}:{to},bandpass=f={hertz}:width_type=q:w=8,bandpass=f={hertz}:width_type=q:w=8,astats=metadata=0:measure_perchannel=none"),
                "-f", "null", "-"], CancellationToken.None).GetAwaiter().GetResult();
        var match = System.Text.RegularExpressions.Regex.Match(result.StdErr, @"RMS level dB: (-?[\d.]+|-inf)");
        Assert.True(match.Success, result.StdErr);
        return match.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Each_stretch_has_its_own_cover_and_what_plays_has_them_in()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var song = scratch.Tone("song.wav", seconds: 4, channels: 2);     // 440 Hz throughout
        await Ui.Run(async () =>
        {
            var (window, editor, host) = await TrimEditorTests.Open(song, "bleep", (file, target) => new BleepEditor(file, target, Env.Tools));
            try
            {
                var view = editor.View;
                Assert.False(window.SaveButton.IsEnabled);
                Assert.Equal("New stretches", editor.CoverHeading);

                TrimEditorTests.Drag(view, 1.0, 1.5);                 // a new stretch, with the tone new ones get
                Assert.Equal([new Bleep(1.0, 1.5)], editor.Current.Bleeps);
                Assert.Equal("Stretch 1", editor.CoverHeading);
                TrimEditorTests.Drag(view, 1.5, 1.8);                 // its end
                TrimEditorTests.Drag(view, 1.4, 1.6);                 // the middle: moved on by 0.2 s
                Assert.Equal([new Bleep(1.2, 2.0)], editor.Current.Bleeps);
                Assert.Equal("1 stretch · one selected", window.StatusText.Text);
                Assert.True(window.SaveButton.IsEnabled);

                // A click away from the stretches moves the playhead and lets go of the selection.
                var (top, height) = view.Band(TimelineLane.Sound);
                view.PointerDown(new Point(view.XAt(3), top + height / 2), ModifierKeys.None);
                view.PointerUp(new Point(view.XAt(3), top + height / 2));
                Assert.Equal(3, editor.Playhead, 3);
                Assert.Null(editor.Selected);

                // With nothing selected, the cover is for new stretches: silence. O marks the second before the playhead.
                editor.SetSound(tone: false);
                Assert.False(editor.Current.Tone);
                Assert.Equal([new Bleep(1.2, 2.0)], editor.Current.Bleeps);
                editor.Press(Key.O);
                Assert.Equal(1, editor.Selected);
                Assert.Equal([new Bleep(1.2, 2.0), new Bleep(2.0, 3.0, Tone: false)], editor.Current.Bleeps);

                // The first one higher.
                editor.Press(Key.Tab);
                Assert.Equal(0, editor.Selected);
                editor.SetPitch(2000);
                Assert.Equal(new Bleep(1.2, 2.0, Pitch: 2000), editor.Current.Bleeps[0]);

                // What plays is the sound with those bleeps in.
                await editor.SoundReady;
                var player = Assert.IsType<FramePlayer>(editor.Preview);
                var playing = player.Source!;
                Assert.NotEqual(song, playing);
                Assert.StartsWith("Ready", editor.Hearing);
                await Task.Run(() =>
                {
                    Assert.True(Level(playing, 1.3, 1.9, 2000) > -25, "the 2 kHz tone should play in the first stretch");
                    Assert.True(Level(playing, 1.3, 1.9, 440) < -40, "the sound should be gone from the first stretch");
                    Assert.True(Level(playing, 2.1, 2.9, 440) < -40, "the second stretch should be silent");
                    Assert.True(Level(playing, 3.2, 3.8, 440) > -30, "after the stretches the sound plays as it was");
                });

                // Hear it plays the stretch with a second either side, then stops.
                await editor.HearAsync();
                Assert.True(player.Playing);
                Assert.Equal(0.2, editor.Playhead, 1);
                editor.Press(Key.K);

                Ui.Render(host, "bleep-sound-light");
                Ui.UseTheme("dark", host);
                await Ui.Settle();
                Ui.Render(host, "bleep-sound-dark");
                Ui.UseTheme("light", host);

                var saved = editor.Result();
                Assert.Equal("0:01.2-0:02 tone 2000, 0:02-0:03 silence", saved.Parameters["ranges"]);
                var result = await Task.Run(() => Env.Engine().RunAsync([song], saved));
                Assert.Empty(result.Failures);
                Assert.Equal(["2 stretches covered"], result.Notes);

                editor.Press(Key.Delete);
                Assert.Single(editor.Current.Bleeps);
            }
            finally
            {
                editor.Close();
            }
        });
    }

    [Fact]
    public async Task A_stretch_drawn_over_another_covered_differently_takes_its_place()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var song = scratch.Tone("song.wav", seconds: 4, channels: 2);
        await Ui.Run(async () =>
        {
            var (_, editor, _) = await TrimEditorTests.Open(song, "bleep", (file, target) => new BleepEditor(file, target, Env.Tools));
            try
            {
                TrimEditorTests.Drag(editor.View, 0.5, 3.5);
                editor.Press(Key.Escape);
                editor.SetSound(tone: false);                          // new ones silent
                TrimEditorTests.Drag(editor.View, 3.8, 3.9);           // somewhere free, then dragged over the middle
                TrimEditorTests.Drag(editor.View, 3.85, 1.85);
                Assert.Equal([new Bleep(0.5, 1.8), new Bleep(1.8, 1.9, Tone: false), new Bleep(1.9, 3.5)], editor.Current.Bleeps);
                Assert.Equal(1, editor.Selected);
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
                TrimEditorTests.Drag(editor.View, 2.9, 3.4);
                editor.SetSound(tone: false);
                await editor.SoundReady;
                Ui.Render(host, "bleep-video-light");
                Ui.UseTheme("dark", host);
                await Ui.Settle();
                Ui.Render(host, "bleep-video-dark");
                Ui.UseTheme("light", host);
                Assert.Equal("0:00.95-0:01.2 tone 1000, 0:02.9-0:03.4 silence", editor.Result().Parameters["ranges"]);
            }
            finally
            {
                editor.Close();
            }
        });
    }
}

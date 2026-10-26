using ImageMagick;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class ChannelVolumeTests
{
    const string Layouts = """
        Individual channels:
        NAME           DESCRIPTION
        FL             front left
        FR             front right
        FC             front center
        LFE            low frequency
        SL             side left
        SR             side right
        TBC            top back center

        Standard channel layouts:
        NAME           DECOMPOSITION
        mono           FC
        stereo         FL+FR
        5.1(side)      FL+FR+FC+LFE+SL+SR
        """;

    [Fact]
    public void Layouts_and_the_side_of_each_channel_come_from_ffmpeg()
    {
        var layouts = ChannelLayouts.Parse(Layouts.Replace("\n", "\r\n"));

        Assert.Equal(["FL", "FR", "FC", "LFE", "SL", "SR"], layouts.Channels["5.1(side)"]);
        Assert.Equal([-1, 1, 0, 0, -1, 1, 0], new[] { "FL", "FR", "FC", "LFE", "SL", "SR", "TBC" }.Select(layouts.Side));
        Assert.Equal(0, layouts.Side("XYZ"));
    }

    [Fact]
    public void Each_side_gets_its_gain_and_the_middle_keeps_its_own()
    {
        Assert.Equal([0.5, 2, 1, 1, 0.5, 2], ChannelVolumeTool.Gains([-1, 1, 0, 0, -1, 1], 0.5, 2));
    }

    static async Task<BatchResult> Run(string input, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "channel-volume", [input]).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        return await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
    }

    static async Task<string> RunOne(string input, params (string Name, string Value)[] values)
    {
        var result = await Run(input, values);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    /// <summary>A tone on the left and another on the right, at the same level.</summary>
    static string Stereo(Scratch scratch, string name, params string[] extra) => scratch.Ffmpeg(name,
    [
        "-f", "lavfi", "-i", "sine=frequency=440:duration=2", "-f", "lavfi", "-i", "sine=frequency=660:duration=2", .. extra,
        "-filter_complex", "[0:a][1:a]join=inputs=2:channel_layout=stereo[a]", "-map", "[a]",
    ]);

    [Fact]
    public async Task One_side_can_be_silenced()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await RunOne(Stereo(scratch, "talk.wav"), ("left", "0"));

        Assert.Equal(scratch.PathOf("talk-balanced.wav"), output);
        var levels = Inspect.ChannelLevels(output);
        Assert.Equal(double.NegativeInfinity, levels[0]);
        Assert.Equal(Inspect.ChannelLevels(scratch.PathOf("talk.wav"))[1], levels[1], 0.1);
    }

    [Fact]
    public async Task In_surround_sound_every_channel_on_a_side_follows_it()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var surround = scratch.Ffmpeg("film.wav", ["-f", "lavfi", "-i", "sine=duration=2", "-filter_complex",
            "[0:a]asplit=6[a][b][c][d][e][f];[a][b][c][d][e][f]join=inputs=6:channel_layout=5.1(side)[out]", "-map", "[out]"]);

        var levels = Inspect.ChannelLevels(await RunOne(surround, ("right", "50")));

        var full = Inspect.ChannelLevels(surround)[0];
        Assert.Equal([0.0, -6.0, 0.0, 0.0, 0.0, -6.0], levels.Select(l => Math.Round(l - full)));
    }

    [Fact]
    public async Task A_videos_picture_is_copied_and_only_its_sound_changes()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var picture = scratch.Video("picture.mp4", audio: false);
        var clip = Stereo(scratch, "clip.mp4", "-i", picture, "-map", "2:v", "-c:v", "copy", "-c:a", "aac");

        var output = await RunOne(clip, ("right", "0"));

        Assert.Equal(scratch.PathOf("clip-balanced.mp4"), output);
        Assert.Equal([("video", "h264"), ("audio", "aac")], Inspect.Media(output).Streams);
        Assert.Equal(double.NegativeInfinity, Inspect.ChannelLevels(output)[1]);
    }

    [Fact]
    public async Task Mono_has_no_sides()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var result = await Run(scratch.Tone("voice.mp3"), ("left", "50"));

        Assert.Equal("This file's sound has one channel, so it has no sides. Make it stereo first.", Assert.Single(result.Failures).Message);
    }

    [Fact]
    public async Task The_preview_draws_each_channel_after_the_change()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var talk = Stereo(scratch, "talk.wav");
        var engine = Env.Engine();
        var target = Env.Offered(engine, "channel-volume", [talk]).WithParameters(new Dictionary<string, string> { ["left"] = "50" });

        var preview = await engine.PreviewAsync([talk], target, new PreviewRequest(200), TestContext.Current.CancellationToken);

        Assert.Equal((200u, 150u), (new MagickImageInfo(preview!.Image!).Width, new MagickImageInfo(preview.Image!).Height));
        Assert.Equal([new Fact("Sound", "stereo, 0:02"), new Fact("Left", "50%, -6.0 dB"), new Fact("Right", "as it is"),
            new Fact("Colours", "Orange is left, blue right")], preview.Facts);
    }
}

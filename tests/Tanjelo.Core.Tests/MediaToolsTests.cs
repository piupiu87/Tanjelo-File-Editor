namespace Tanjelo.Core.Tests;

public class MediaToolsTests
{
    [Fact]
    public void Video_tools_are_on_the_tools_tab_and_only_for_video()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var menu = Env.Engine().GetMenu([scratch.Text("clip.mp4", "")]);

        var mute = Assert.Single(menu.Targets, t => t.Key == "mute");
        Assert.Equal(TargetKind.Tool, mute.Kind);
        Assert.DoesNotContain("mute", Env.Keys(scratch.Text("song.mp3", "")));
    }

    [Fact]
    public async Task Mute_drops_the_sound_and_copies_the_picture()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne("mute", scratch.Video("talk.mp4"));

        Assert.Equal(scratch.PathOf("talk-muted.mp4"), output);
        var media = Inspect.Media(output);
        Assert.Equal([("video", "h264")], media.Streams);
        Assert.Equal(2.0, media.Duration, 0.2);
    }

    [Fact]
    public async Task Mute_keeps_the_container()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var webm = scratch.Video("clip.webm", codec: ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-c:a", "libopus"]);

        var output = await Env.RunOne("mute", webm);

        Assert.Equal(scratch.PathOf("clip-muted.webm"), output);
        Assert.Equal([("video", "vp9")], Inspect.Media(output).Streams);
    }

    [Fact]
    public async Task Muting_a_silent_video_explains_why_nothing_happened()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var silent = scratch.Video("silent.mp4", audio: false);
        var engine = Env.Engine();

        var result = await engine.RunAsync([silent], Env.Offered(engine, "mute", [silent]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("This video has no sound to remove.", Assert.Single(result.Failures).Message);
        Assert.Equal(["silent.mp4"], scratch.Files());
    }
}

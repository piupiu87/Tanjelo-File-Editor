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

    [Theory]
    [InlineData("speed-2x", "talk-2x.mp4", 1.0)]
    [InlineData("speed-0.5x", "talk-0.5x.mp4", 4.0)]
    [InlineData("speed-1.5x", "talk-1.5x.mp4", 1.33)]
    public async Task Speed_presets_retime_picture_and_sound(string key, string name, double seconds)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne(key, scratch.Video("talk.mp4"));

        Assert.Equal(scratch.PathOf(name), output);
        var media = Inspect.Media(output);
        Assert.Equal([("video", "h264"), ("audio", "aac")], media.Streams);
        Assert.Equal(seconds, media.Duration, 0.15);
    }

    [Fact]
    public async Task Speed_takes_other_factors_and_works_without_sound()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var silent = scratch.Video("silent.mp4", audio: false);
        var engine = Env.Engine();
        var target = Env.Offered(engine, "speed-2x", [silent]).WithParameters(new Dictionary<string, string> { ["factor"] = "4" });

        var result = await engine.RunAsync([silent], target, ct: TestContext.Current.CancellationToken);

        var output = Assert.Single(result.Outputs);
        Assert.Equal(scratch.PathOf("silent-4x.mp4"), output);
        Assert.Equal([("video", "h264")], Inspect.Media(output).Streams);
        Assert.Equal(0.5, Inspect.Media(output).Duration, 0.1);
    }

    [Fact]
    public async Task Speed_writes_mp4_for_containers_it_cant_encode()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne("speed-2x", scratch.Video("broadcast.ts"));
        Assert.Equal(scratch.PathOf("broadcast-2x.mp4"), output);
    }

    [Fact]
    public async Task Split_cuts_at_keyframes_without_re_encoding_when_they_line_up()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        // A keyframe every half second, so keyframe cuts land close to the requested points.
        var clip = scratch.Video("clip.mkv", seconds: 6, codec: ["-c:v", "libx264", "-g", "12", "-pix_fmt", "yuv420p", "-c:a", "aac"]);

        var result = await Env.Run("split-video-3", clip);

        Assert.Equal([scratch.PathOf("clip-part1.mkv"), scratch.PathOf("clip-part2.mkv"), scratch.PathOf("clip-part3.mkv")], result.Outputs);
        Assert.All(result.Outputs, part =>
        {
            var media = Inspect.Media(part);
            Assert.Equal(2.0, media.Duration, 0.6);
            Assert.Contains(("audio", "aac"), media.Streams);
        });
    }

    [Fact]
    public async Task Split_re_encodes_when_keyframes_are_too_far_apart()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("clip.mp4", seconds: 4, codec: ["-c:v", "libx264", "-g", "1000", "-pix_fmt", "yuv420p", "-c:a", "aac"]);

        var result = await Env.Run("split-video-2", clip);

        Assert.Equal([scratch.PathOf("clip-part1.mp4"), scratch.PathOf("clip-part2.mp4")], result.Outputs);
        Assert.All(result.Outputs, part => Assert.Equal(2.0, Inspect.Media(part).Duration, 0.2));
    }

    [Fact]
    public async Task Split_refuses_videos_shorter_than_a_second_per_part()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("short.mp4", seconds: 2);
        var engine = Env.Engine();

        var result = await engine.RunAsync([clip], Env.Offered(engine, "split-video-3", [clip]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("This video is too short to split into 3 parts.", Assert.Single(result.Failures).Message);
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

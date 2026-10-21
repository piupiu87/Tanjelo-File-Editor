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

    [Theory]
    [InlineData("quiet.mp3", "quiet-normalized.mp3", "mp3")]
    [InlineData("quiet.opus", "quiet-normalized.opus", "opus")]
    [InlineData("quiet.wav", "quiet-normalized.wav", "pcm_s16le")]
    public async Task Normalize_brings_quiet_audio_to_the_target_loudness(string name, string expected, string codec)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var quiet = scratch.Tone(name, seconds: 5, filter: "volume=0.02");
        Assert.True(Inspect.Loudness(quiet) < -35);

        var output = await Env.RunOne("normalize", quiet);

        Assert.Equal(scratch.PathOf(expected), output);
        Assert.Equal(codec, Inspect.Codec(output, "audio"));
        Assert.Equal(-16, Inspect.Loudness(output), 1.5);
        Assert.Equal(5.0, Inspect.Media(output).Duration, 0.1);
    }

    [Fact]
    public async Task Normalizing_silence_explains_why_nothing_happened()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var silence = scratch.Tone("silence.wav", filter: "volume=0");
        var engine = Env.Engine();

        var result = await engine.RunAsync([silence], Env.Offered(engine, "normalize", [silence]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("This file is silent.", Assert.Single(result.Failures).Message);
    }

    [Theory]
    [InlineData("mono", "song.flac", 2, "song-mono.flac", 1)]
    [InlineData("mono", "surround.wav", 6, "surround-mono.wav", 1)]
    [InlineData("stereo", "voice.m4a", 1, "voice-stereo.m4a", 2)]
    [InlineData("stereo", "surround.ogg", 6, "surround-stereo.ogg", 2)]
    public async Task Channels_are_mixed_to_mono_or_stereo(string key, string name, int before, string expected, int after)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne(key, scratch.Tone(name, channels: before));

        Assert.Equal(scratch.PathOf(expected), output);
        Assert.Equal(after, Inspect.Media(output).Channels);
        Assert.Equal(2.0, Inspect.Media(output).Duration, 0.1);
    }

    [Fact]
    public async Task Mono_audio_isnt_made_mono_again()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var mono = scratch.Tone("voice.mp3", channels: 1);
        var engine = Env.Engine();

        var result = await engine.RunAsync([mono], Env.Offered(engine, "mono", [mono]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("This file is already mono.", Assert.Single(result.Failures).Message);
    }

    [Theory]
    [InlineData("adelay=1000:all=1,apad=pad_dur=1.5", 2.0)] // silence at both ends
    [InlineData("adelay=1500:all=1", 2.0)]                  // only at the start
    [InlineData("apad=pad_dur=2", 2.0)]                     // only at the end
    public async Task Trim_silence_cuts_quiet_from_the_ends(string shape, double seconds)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne("trim-silence", scratch.Tone("take.mp3", seconds: 2, filter: shape));

        Assert.Equal(scratch.PathOf("take-trimmed.mp3"), output);
        Assert.Equal(seconds, Inspect.Media(output).Duration, 0.15);
    }

    [Fact]
    public async Task Pauses_in_the_middle_are_kept()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        // 1 s tone, 1 s of silence, 1 s tone, then 1 s of silence to trim.
        var take = scratch.Tone("take.wav", seconds: 4, filter: "volume='if(between(t,1,2)+gte(t,3),0,1)':eval=frame");

        var output = await Env.RunOne("trim-silence", take);

        Assert.Equal(3.0, Inspect.Media(output).Duration, 0.15);
    }

    [Fact]
    public async Task Audio_without_silence_at_the_ends_is_left_alone()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var tone = scratch.Tone("tone.wav");
        var engine = Env.Engine();

        var result = await engine.RunAsync([tone], Env.Offered(engine, "trim-silence", [tone]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("There's no silence at the start or end to trim.", Assert.Single(result.Failures).Message);
        Assert.Equal(["tone.wav"], scratch.Files());
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

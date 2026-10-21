namespace Tanjelo.Core.Tests;

public class MediaConverterTests
{
    [Theory]
    [InlineData("mkv", "h264", "aac")]   // stream copy
    [InlineData("mov", "h264", "aac")]   // stream copy
    [InlineData("webm", "vp9", "opus")]
    [InlineData("avi", "mpeg4", "mp3")]
    [InlineData("wmv", "wmv2", "wmav2")]
    public async Task Video_converts_between_containers(string key, string video, string audio)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne(key, scratch.Video("clip.mp4"));

        Assert.Equal(scratch.PathOf("clip." + key), output);
        Assert.Equal(video, Inspect.Codec(output, "video"));
        Assert.Equal(audio, Inspect.Codec(output, "audio"));
        Assert.Equal(2.0, Inspect.Media(output).Duration, 0.2);
    }

    [Fact]
    public async Task Video_that_mp4_cant_hold_is_re_encoded_to_h264()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var webm = scratch.Video("clip.webm", codec: ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-c:a", "libopus"]);

        var output = await Env.RunOne("mp4", webm);

        Assert.Equal("h264", Inspect.Codec(output, "video"));
        Assert.Equal("aac", Inspect.Codec(output, "audio"));
    }

    [Fact]
    public async Task Video_becomes_an_animated_gif()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne("gif", scratch.Video("clip.mp4", seconds: 1));

        using var frames = new ImageMagick.MagickImageCollection(output);
        Assert.True(frames.Count > 5);
    }

    [Fact]
    public async Task An_animated_gif_becomes_a_video()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne("mp4", scratch.AnimatedGif("spin.gif"));
        Assert.Equal("h264", Inspect.Codec(output, "video"));
    }

    [Theory]
    [InlineData("mp3", "mp3")]
    [InlineData("m4a", "aac")]
    [InlineData("wav", "pcm_s16le")]
    [InlineData("flac", "flac")]
    public async Task The_audio_track_is_extracted_from_video(string key, string codec)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne(key, scratch.Video("talk.mp4"));

        var media = Inspect.Media(output);
        Assert.Equal([("audio", codec)], media.Streams);
    }

    [Fact]
    public async Task Extracting_audio_from_a_silent_video_explains_the_problem()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var silent = scratch.Video("silent.mp4", audio: false);
        var engine = Env.Engine();

        var result = await engine.RunAsync([silent], Env.Offered(engine, "mp3", [silent]), ct: TestContext.Current.CancellationToken);

        Assert.Equal("This file has no audio track.", Assert.Single(result.Failures).Message);
        Assert.Equal(["silent.mp4"], scratch.Files());
    }

    [Theory]
    [InlineData("mp3", "mp3")]
    [InlineData("m4a", "aac")]
    [InlineData("flac", "flac")]
    [InlineData("ogg", "vorbis")]
    [InlineData("opus", "opus")]
    [InlineData("aiff", "pcm_s16be")]
    [InlineData("wma", "wmav2")]
    public async Task Audio_converts_between_formats(string key, string codec)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne(key, scratch.Tone("tone.wav"));

        Assert.Equal(codec, Inspect.Codec(output, "audio"));
        Assert.Equal(2.0, Inspect.Media(output).Duration, 0.1);
    }

    [Fact]
    public async Task Surround_audio_is_downmixed_to_stereo_for_wma()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await Env.RunOne("wma", scratch.Tone("surround.wav", channels: 6));
        Assert.Equal(2, Inspect.Media(output).Channels);
    }

    [Fact]
    public async Task Conversion_reports_progress()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var input = scratch.Tone("long.wav", seconds: 20);
        var engine = Env.Engine();
        var fractions = new List<double>();

        await engine.RunAsync([input], Env.Offered(engine, "flac", [input]),
            new ActionProgress<BatchProgress>(p => { if (!double.IsNaN(p.Fraction)) fractions.Add(p.Fraction); }), TestContext.Current.CancellationToken);

        Assert.NotEmpty(fractions);
        Assert.True(fractions.Max() > 0.5);
    }
}

using ImageMagick;

namespace Tanjelo.Core.Tests;

public class VisualizerTests
{
    static async Task<BatchResult> Run(string input, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "visualize", [input]).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        return await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
    }

    static async Task<string> RunOne(string input, params (string Name, string Value)[] values)
    {
        var result = await Run(input, values);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    static async Task<string> Size(string video) =>
        (await ProcessRunner.RunAsync(Env.Tools.Ffprobe!, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", video],
            TestContext.Current.CancellationToken)).StdOut.Trim();

    /// <summary>An MP3 with an album cover, as music downloads come.</summary>
    static string WithCover(Scratch scratch)
    {
        var cover = scratch.Image("cover.jpg", 300, 300, System.Drawing.Color.SeaGreen);
        return scratch.Ffmpeg("song.mp3", ["-f", "lavfi", "-i", "sine=duration=3", "-i", cover, "-map", "0:a", "-map", "1:v", "-c:a", "libmp3lame",
            "-c:v", "mjpeg", "-disposition:v", "attached_pic"]);
    }

    [Fact]
    public async Task A_waveform_video_is_full_hd_and_as_long_as_the_sound()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await RunOne(scratch.Tone("voice.wav", seconds: 3));

        Assert.Equal(scratch.PathOf("voice-video.mp4"), output);
        Assert.Equal("1920,1080", await Size(output));
        var media = Inspect.Media(output);
        Assert.Equal([("video", "h264"), ("audio", "aac")], media.Streams);
        Assert.Equal(3.0, media.Duration, 0.2);
    }

    [Fact]
    public async Task A_part_of_the_sound_in_portrait()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await RunOne(scratch.Tone("voice.wav", seconds: 4), ("shape", "portrait"), ("range", "0:01-0:02.5"));

        Assert.Equal("1080,1920", await Size(output));
        Assert.Equal(1.5, Inspect.Media(output).Duration, 0.2);
    }

    [Fact]
    public async Task A_still_picture_uses_the_cover_art_when_none_is_chosen()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var output = await RunOne(WithCover(scratch), ("style", "image"), ("shape", "square"), ("background", "#000000"));

        Assert.Equal("1080,1080", await Size(output));
        var frame = scratch.Ffmpeg("frame.png", ["-ss", "1", "-i", output, "-frames:v", "1"]);
        using var picture = new MagickImage(frame);
        Assert.True(picture.GetPixels().GetPixel(540, 540).ToColor()!.FuzzyEquals(new MagickColor(46, 139, 87), new Percentage(8)));
    }

    [Fact]
    public async Task A_still_picture_needs_a_picture_from_somewhere()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var result = await Run(scratch.Tone("voice.wav"), ("style", "image"));

        Assert.Equal("Choose a picture: this file has no cover art.", Assert.Single(result.Failures).Message);
    }

    [Fact]
    public async Task The_preview_is_a_frame_from_the_middle()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var song = WithCover(scratch);
        var engine = Env.Engine();

        var preview = await engine.PreviewAsync([song], Env.Offered(engine, "visualize", [song]), new PreviewRequest(320), TestContext.Current.CancellationToken);

        var info = new MagickImageInfo(preview!.Image!);
        Assert.Equal((320u, 180u), (info.Width, info.Height));
        Assert.Equal([new Fact("Video", "1920 × 1080, 0:03"), new Fact("Picture", "The cover art")], preview.Facts);
    }
}

using ImageMagick;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class VideoMomentsTests
{
    static async Task<BatchResult> Run(string key, string input, string times)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, key, [input]).WithParameters(new Dictionary<string, string> { ["times"] = times });
        return await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
    }

    static async Task<IReadOnlyList<string>> RunOk(string key, string input, string times)
    {
        var result = await Run(key, input, times);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return result.Outputs;
    }

    [Theory]
    [InlineData(5.5, "0m05.5s")]
    [InlineData(65, "1m05s")]
    [InlineData(3723.25, "1h02m03.25s")]
    public void Frame_names_carry_their_time_in_a_form_files_allow(double seconds, string name) => Assert.Equal(name, VideoMomentsTool.FileTime(seconds));

    [Fact]
    public async Task Each_chosen_moment_becomes_a_full_size_png()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("clip.mp4", size: "320x240");

        var outputs = await RunOk("save-frames", clip, "1.5, 0:00.5");

        Assert.Equal([scratch.PathOf("clip-0m00.5s.png"), scratch.PathOf("clip-0m01.5s.png")], outputs);
        Assert.All(outputs, o => Assert.Equal((MagickFormat.Png, 320u, 240u), (Inspect.Image(o).Format, Inspect.Image(o).Width, Inspect.Image(o).Height)));
    }

    [Fact]
    public async Task Frames_of_a_phone_video_come_out_upright()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var stored = scratch.Video("stored.mp4", size: "160x120", audio: false);
        var portrait = scratch.Ffmpeg("portrait.mp4", ["-display_rotation:v:0", "90", "-i", stored, "-c", "copy"]);

        var output = Assert.Single(await RunOk("save-frames", portrait, "1"));

        Assert.Equal((120u, 160u), (Inspect.Image(output).Width, Inspect.Image(output).Height));
    }

    [Fact]
    public async Task A_moment_past_the_end_is_refused()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var result = await Run("save-frames", scratch.Video("clip.mp4"), "0:01, 0:05");

        Assert.Equal("0:05 is past the end of this video, which is 0:02 long.", Assert.Single(result.Failures).Message);
        Assert.Equal(["clip.mp4"], scratch.Files());
    }

    [Fact]
    public async Task A_video_is_split_exactly_at_the_chosen_moments()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("talk.mp4", seconds: 4);

        var outputs = await RunOk("split-video-at", clip, "2.5, 1");

        Assert.Equal([scratch.PathOf("talk-part1.mp4"), scratch.PathOf("talk-part2.mp4"), scratch.PathOf("talk-part3.mp4")], outputs);
        Assert.Equal([1.0, 1.5, 1.5], outputs.Select(o => Math.Round(Inspect.Media(o).Duration * 10) / 10));
        Assert.All(outputs, o => Assert.Equal([("video", "h264"), ("audio", "aac")], Inspect.Media(o).Streams));
    }

    [Fact]
    public async Task Cuts_at_keyframes_keep_the_original_encoding()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        // A keyframe every second, so cuts at whole seconds need no re-encoding; a stream copy keeps the odd profile.
        var clip = scratch.Video("talk.mp4", seconds: 3, codec: ["-c:v", "libx264", "-profile:v", "baseline", "-g", "25", "-pix_fmt", "yuv420p", "-c:a", "aac"]);

        var outputs = await RunOk("split-video-at", clip, "1, 2");

        Assert.Equal(3, outputs.Count);
        var profile = await ProcessRunner.RunAsync(Env.Tools.Ffprobe!, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=profile", "-of", "csv=p=0", outputs[1]],
            TestContext.Current.CancellationToken);
        Assert.Contains("Baseline", profile.StdOut);
    }

    [Fact]
    public async Task Parts_shorter_than_half_a_second_are_refused()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var result = await Run("split-video-at", scratch.Video("clip.mp4"), "1, 1.2");

        Assert.Equal("Every part needs to be at least half a second long.", Assert.Single(result.Failures).Message);
    }

    [Fact]
    public async Task The_split_preview_lists_the_parts()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("clip.mp4", seconds: 3);
        var engine = Env.Engine();
        var target = Env.Offered(engine, "split-video-at", [clip]).WithParameters(new Dictionary<string, string> { ["times"] = "0:01.5" });

        var preview = await engine.PreviewAsync([clip], target, new PreviewRequest(80), TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            new Fact("Showing", "0:01.5 of 0:03"),
            new Fact("Part 1", "0:00 to 0:01.5 · 0:01.5 long"),
            new Fact("Part 2", "0:01.5 to 0:03 · 0:01.5 long"),
        ], preview!.Facts);
    }

    [Fact]
    public async Task The_preview_shows_the_frame_under_the_slider_before_any_moment_is_chosen()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("clip.mp4");
        var engine = Env.Engine();

        var preview = await engine.PreviewAsync([clip], Env.Offered(engine, "save-frames", [clip]), new PreviewRequest(80) { At = 1.2 },
            TestContext.Current.CancellationToken);

        Assert.Equal(2.0, preview!.Duration, 0.1);
        Assert.Equal(80u, new MagickImageInfo(preview.Image!).Width);
        Assert.Equal([new Fact("Showing", "0:01.2 of 0:02"), new Fact("Frames", "None yet")], preview.Facts);
    }
}

using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class JoinVideosTests
{
    static MediaInfo Clip(int width = 1920, int height = 1080, string video = "h264", string? audio = "aac", double fps = 30, int rotation = 0) =>
        new(true, video, audio, 10, audio is null ? 0 : 2, audio is null ? 0 : 48000)
        {
            Width = width, Height = height, FrameRate = fps, PixelFormat = "yuv420p", Rotation = rotation,
        };

    static Target Settings(params (string Name, string Value)[] values)
    {
        using var scratch = new Scratch();
        string[] clips = [scratch.Text("a.mp4", ""), scratch.Text("b.mp4", "")];
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        return Env.Offered(Env.Engine(), "join-videos", clips).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
    }

    [Fact]
    public void Matching_clips_are_joined_as_they_are()
    {
        var plan = JoinVideosOperation.PlanFor([Clip(), Clip()], ["mp4", "mp4"], Settings());

        Assert.True(plan.Copy);
        Assert.Equal((1920, 1080), (plan.Width, plan.Height));
    }

    [Theory]
    [InlineData(1280, 720, "h264", "aac", 30, "their sizes differ")]
    [InlineData(1920, 1080, "hevc", "aac", 30, "their video formats differ")]
    [InlineData(1920, 1080, "h264", null, 30, "their sound differs")]
    [InlineData(1920, 1080, "h264", "aac", 25, "their frame rates differ")]
    public void Clips_that_differ_are_re_encoded_and_the_reason_is_given(int width, int height, string video, string? audio, double fps, string reason)
    {
        var plan = JoinVideosOperation.PlanFor([Clip(), Clip(width, height, video, audio, fps)], ["mp4", "mp4"], Settings());

        Assert.False(plan.Copy);
        Assert.Equal($"re-encoded to 1920 × 1080 at 30 fps, because {reason}", plan.Why);
    }

    [Fact]
    public void Clips_from_different_kinds_of_file_are_re_encoded()
    {
        var plan = JoinVideosOperation.PlanFor([Clip(), Clip()], ["mp4", "mkv"], Settings());

        Assert.False(plan.Copy);
        Assert.EndsWith("because they're different kinds of file", plan.Why);
    }

    [Fact]
    public async Task Clips_in_different_kinds_of_file_are_all_kept()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        string[] clips = [scratch.Video("a.mp4"), scratch.Video("b.mkv", codec: ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"])];

        var output = await Run(clips);

        Assert.Equal(4.0, Inspect.Media(output).Duration, 0.2);
    }

    [Fact]
    public void A_rotated_clip_of_the_same_size_still_needs_re_encoding()
    {
        Assert.False(JoinVideosOperation.PlanFor([Clip(), Clip(rotation: 90)], ["mp4", "mp4"], Settings()).Copy);
    }

    [Theory]
    [InlineData("720", 1920, 1080, 1280, 720)]
    [InlineData("720", 1080, 1920, 720, 1280)]
    [InlineData("1080", 640, 480, 1440, 1080)]
    [InlineData("largest", 640, 480, 1920, 1080)]
    public void Sizes_name_the_short_side_so_portrait_stays_portrait(string size, int firstWidth, int firstHeight, int width, int height)
    {
        var plan = JoinVideosOperation.PlanFor([Clip(firstWidth, firstHeight), Clip(1920, 1080)], ["mp4", "mp4"], Settings(("size", size)));

        Assert.Equal((width, height), (plan.Width, plan.Height));
    }

    static async Task<string> Run(string[] clips, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "join-videos", clips).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        var result = await engine.RunAsync(clips, target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    [Fact]
    public async Task Matching_clips_become_one_mp4_without_re_encoding()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        string[] clips = [scratch.Video("a.mp4"), scratch.Video("b.mp4")];

        var output = await Run(clips);

        Assert.Equal(scratch.PathOf("Joined.mp4"), output);
        var media = Inspect.Media(output);
        Assert.Equal([("video", "h264"), ("audio", "aac")], media.Streams);
        Assert.Equal(4.0, media.Duration, 0.2);
    }

    [Fact]
    public async Task Different_clips_are_fitted_to_the_first_in_the_chosen_order_with_silence_where_there_was_none()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        string[] clips = [scratch.Video("wide.mp4", seconds: 2, size: "320x180"), scratch.Video("tall.mov", seconds: 1, audio: false, size: "180x320")];

        var output = await Run(clips, ("order", "2,1"));

        var media = Inspect.Media(output);
        Assert.Equal([("video", "h264"), ("audio", "aac")], media.Streams);
        Assert.Equal(3.0, media.Duration, 0.25);
        var size = await ProcessRunner.RunAsync(Env.Tools.Ffprobe!,
            ["-v", "error", "-show_entries", "stream=width,height", "-select_streams", "v:0", "-of", "csv=p=0", output], TestContext.Current.CancellationToken);
        Assert.Equal("180,320", size.StdOut.Trim());
    }

    [Fact]
    public async Task The_preview_shows_the_clips_in_order_and_how_they_will_be_joined()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        string[] clips = [scratch.Video("a.mp4"), scratch.Video("b.mp4", size: "320x240")];
        var engine = Env.Engine();
        var target = Env.Offered(engine, "join-videos", clips).WithParameters(new Dictionary<string, string> { ["order"] = "2,1" });

        var preview = await engine.PreviewAsync(clips, target, new PreviewRequest(300), TestContext.Current.CancellationToken);

        Assert.NotNull(preview!.Image);
        Assert.Equal(new Fact("Joined", "re-encoded to 320 × 240 at 25 fps, because their sizes differ"), preview.Facts[1]);
        Assert.Equal(new Fact("1.", "b.mp4 · 0:02 · 320 × 240"), preview.Facts[2]);
    }
}

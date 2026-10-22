using ImageMagick;

namespace Tanjelo.Core.Tests;

public class CompressToolTests
{
    static Dictionary<string, string> Set(params (string Name, string Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);

    static async Task<BatchResult> Run(string input, Dictionary<string, string> values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "compress", [input]).WithParameters(values);
        return await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
    }

    static async Task<string> RunOne(string input, Dictionary<string, string> values)
    {
        var result = await Run(input, values);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    [Fact]
    public void Compress_asks_how_small_and_is_offered_for_photos_but_not_animations()
    {
        using var scratch = new Scratch();
        var target = Env.Offered(Env.Engine(), "compress", [scratch.Photo("photo.jpg")]);

        Assert.Equal(TargetKind.Tool, target.Kind);
        Assert.Equal(["mode", "percent", "size", "scale"], target.Options.Select(o => o.Name));
        Assert.DoesNotContain("compress", Env.Keys(scratch.AnimatedGif("moving.gif")));
    }

    [Fact]
    public async Task A_photo_shrinks_to_the_percentage_and_keeps_its_format_and_size()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.jpg");

        var output = await RunOne(photo, Set(("percent", "40")));

        Assert.Equal(scratch.PathOf("photo-compressed.jpg"), output);
        Assert.InRange(new FileInfo(output).Length, 1, new FileInfo(photo).Length * 0.4);
        var info = Inspect.Image(output);
        Assert.Equal((MagickFormat.Jpeg, 640u, 480u), (info.Format, info.Width, info.Height));
    }

    [Fact]
    public async Task A_target_size_and_smaller_dimensions_both_apply()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.webp");

        var output = await RunOne(photo, Set(("mode", "size"), ("size", "0.02"), ("scale", "50")));

        Assert.InRange(new FileInfo(output).Length, 1, 0.02 * 1024 * 1024);
        Assert.Equal(320u, Inspect.Image(output).Width);
    }

    [Fact]
    public async Task When_even_the_lowest_quality_is_too_big_the_picture_gets_smaller()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.jpg", 1600, 1200);

        var output = await RunOne(photo, Set(("mode", "size"), ("size", "0.01")));

        Assert.InRange(new FileInfo(output).Length, 1, 0.01 * 1024 * 1024);
        Assert.True(Inspect.Image(output).Width < 1600);
    }

    [Fact]
    public async Task A_png_that_cant_pack_any_tighter_drops_to_256_colours()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.png");

        var output = await RunOne(photo, Set(("percent", "50")));

        Assert.Equal(scratch.PathOf("photo-compressed.png"), output);
        Assert.InRange(new FileInfo(output).Length, 1, new FileInfo(photo).Length / 2);
        using var image = new MagickImage(output);
        Assert.True(image.TotalColors <= 256, $"{image.TotalColors} colours");
    }

    [Fact]
    public async Task Uncompressed_formats_become_jpg()
    {
        using var scratch = new Scratch();
        var output = await RunOne(scratch.Photo("scan.bmp"), Set());

        Assert.Equal(scratch.PathOf("scan-compressed.jpg"), output);
    }

    [Fact]
    public async Task A_file_already_under_the_target_is_left_alone()
    {
        using var scratch = new Scratch();
        var result = await Run(scratch.Photo("photo.jpg", 64, 48), Set(("mode", "size"), ("size", "5")));

        Assert.Contains("already", Assert.Single(result.Failures).Message);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public async Task The_preview_shows_the_compressed_picture_and_its_size()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.jpg");
        var engine = Env.Engine();
        var target = Env.Offered(engine, "compress", [photo]);

        var preview = await engine.PreviewAsync([photo], target, new PreviewRequest(200), TestContext.Current.CancellationToken);

        using var picture = new MagickImage(preview!.Image!);
        Assert.Equal(200u, picture.Width);
        Assert.Contains(preview.Facts, f => f.Label == "After" && f.Value.Contains("640 × 480"));
        Assert.Contains(preview.Facts, f => f.Label == "Saved as" && f.Value.StartsWith("JPG, quality "));
        Assert.Equal(["photo.jpg"], scratch.Files()); // previews write nothing beside the original
    }

    [Fact]
    public async Task A_video_lands_close_to_its_target_size()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("clip.mov", seconds: 4, size: "640x360",
            codec: ["-c:v", "libx264", "-crf", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k"]);

        var output = await RunOne(clip, Set(("percent", "30"), ("scale", "50")));

        Assert.Equal(scratch.PathOf("clip-compressed.mp4"), output);
        var goal = new FileInfo(clip).Length * 0.3;
        Assert.InRange(new FileInfo(output).Length, goal * 0.5, goal * 1.1);
        Assert.Equal([("video", "h264"), ("audio", "aac")], Inspect.Media(output).Streams);
        Assert.Equal(4.0, Inspect.Media(output).Duration, 0.2);
    }

    [Fact]
    public async Task A_target_too_small_for_the_videos_length_is_refused_with_the_least_that_works()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("clip.mp4", seconds: 4, size: "640x360", codec: ["-c:v", "libx264", "-crf", "10", "-pix_fmt", "yuv420p", "-c:a", "aac"]);

        var result = await Run(clip, Set(("mode", "size"), ("size", "0.01")));

        Assert.Matches("too small for 0:04 of video; it needs at least .+", Assert.Single(result.Failures).Message);
    }

    [Fact]
    public async Task Lossless_audio_becomes_an_mp3_within_the_target()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var song = scratch.Tone("song.wav", seconds: 4, channels: 2);

        var output = await RunOne(song, Set(("percent", "10")));

        Assert.Equal(scratch.PathOf("song-compressed.mp3"), output);
        Assert.InRange(new FileInfo(output).Length, 1, new FileInfo(song).Length * 0.1);
        Assert.Equal(4.0, Inspect.Media(output).Duration, 0.2);
    }

    [Fact]
    public async Task Lossy_audio_keeps_its_format()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var song = scratch.Ffmpeg("song.m4a", ["-f", "lavfi", "-i", "anoisesrc=d=4", "-c:a", "aac", "-b:a", "256k"]);

        var output = await RunOne(song, Set(("percent", "50")));

        Assert.Equal(scratch.PathOf("song-compressed.m4a"), output);
        Assert.InRange(new FileInfo(output).Length, 1, new FileInfo(song).Length * 0.5);
    }
}

using ImageMagick;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class CropToolTests
{
    static Target Settings(params (string Name, string Value)[] values) =>
        new Target("crop", "Crop…", "", Section.Image).WithOptions(Crop.Options).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));

    [Theory]
    [InlineData("1:1", "center", 400, 300, 50, 0, 300, 300)]
    [InlineData("1:1", "left", 400, 300, 0, 0, 300, 300)]
    [InlineData("1:1", "bottom-right", 400, 300, 100, 0, 300, 300)]
    [InlineData("16:9", "top", 300, 400, 0, 0, 300, 169)]
    [InlineData("16:9", "bottom", 300, 400, 0, 231, 300, 169)]
    [InlineData("4:3", "center", 400, 300, 0, 0, 400, 300)]
    public void Aspect_ratios_cut_one_side_and_keep_the_chosen_part(string aspect, string position, int width, int height, int x, int y, int w, int h)
    {
        Assert.Equal(new Crop.Box(x, y, w, h), Crop.Compute(width, height, Settings(("aspect", aspect), ("position", position))));
    }

    [Fact]
    public void Exact_sizes_are_anchored_and_cut_to_fit_the_picture()
    {
        var size = Settings(("mode", "pixels"), ("width", "200"), ("height", "100"), ("position", "bottom-right"));
        Assert.Equal(new Crop.Box(200, 200, 200, 100), Crop.Compute(400, 300, size));

        var tooBig = Settings(("mode", "pixels"), ("width", "1000"), ("height", "100"));
        Assert.Equal(new Crop.Box(0, 100, 400, 100), Crop.Compute(400, 300, tooBig));
    }

    [Fact]
    public void An_exact_offset_is_kept_inside_the_picture()
    {
        var offset = Settings(("mode", "pixels"), ("width", "100"), ("height", "100"), ("position", "custom"), ("x", "250"), ("y", "500"));
        Assert.Equal(new Crop.Box(250, 200, 100, 100), Crop.Compute(400, 300, offset));
    }

    [Fact]
    public void Video_crops_have_even_sizes_and_offsets()
    {
        var box = Crop.Compute(1281, 721, Settings(("aspect", "1:1")), even: true);
        Assert.Equal(new Crop.Box(280, 0, 720, 720), box);
    }

    static async Task<string> Run(string input, params (string Name, string Value)[] values)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "crop", [input]).WithParameters(values.ToDictionary(v => v.Name, v => v.Value));
        var result = await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    [Fact]
    public async Task A_photo_is_cropped_to_a_square_and_keeps_its_format()
    {
        using var scratch = new Scratch();
        var output = await Run(scratch.Photo("photo.jpg", 400, 300));

        Assert.Equal(scratch.PathOf("photo-cropped.jpg"), output);
        var info = Inspect.Image(output);
        Assert.Equal((MagickFormat.Jpeg, 300u, 300u), (info.Format, info.Width, info.Height));
    }

    [Fact]
    public async Task The_crop_applies_to_the_picture_as_its_shown_not_as_its_stored()
    {
        using var scratch = new Scratch();
        var path = scratch.Sideways("portrait.jpg", 120, 80); // shown 80 wide and 120 tall

        var output = await Run(path, ("aspect", "2:3"), ("position", "top"));

        var info = Inspect.Image(output);
        Assert.Equal((80u, 120u), (info.Width, info.Height));
    }

    [Fact]
    public async Task Every_frame_of_an_animation_is_cropped()
    {
        using var scratch = new Scratch();
        var output = await Run(scratch.AnimatedGif("moving.gif"), ("mode", "pixels"), ("width", "20"), ("height", "10"));

        using var frames = new MagickImageCollection(output);
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal((20u, 10u), (f.Page.Width == 0 ? f.Width : f.Page.Width, f.Page.Height == 0 ? f.Height : f.Page.Height)));
    }

    static (int Width, int Height) Size(string video)
    {
        var result = ProcessRunner.RunAsync(Env.Tools.Ffprobe!, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", video],
            CancellationToken.None).GetAwaiter().GetResult();
        var parts = result.StdOut.Trim().Split(',');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    [Fact]
    public async Task A_video_is_cropped_and_its_sound_copied()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("clip.mp4", size: "320x180");

        var output = await Run(clip, ("aspect", "4:5"), ("position", "right"));

        Assert.Equal(scratch.PathOf("clip-cropped.mp4"), output);
        Assert.Equal((144, 180), Size(output));
        var media = Inspect.Media(output);
        Assert.Equal([("video", "h264"), ("audio", "aac")], media.Streams);
        Assert.Equal(2.0, media.Duration, 0.2);
    }

    [Fact]
    public async Task A_phone_video_is_cropped_the_way_it_plays()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var stored = scratch.Video("stored.mp4", size: "160x120", audio: false);
        var portrait = scratch.Ffmpeg("portrait.mp4", ["-display_rotation:v:0", "90", "-i", stored, "-c", "copy"]); // plays 120 wide, 160 tall

        var output = await Run(portrait, ("aspect", "16:9"));

        Assert.Equal((120, 66), Size(output));
    }

    [Fact]
    public async Task A_webm_stays_webm()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("clip.webm", seconds: 1, codec: ["-c:v", "libvpx-vp9", "-deadline", "realtime", "-c:a", "libopus"]);

        var output = await Run(clip, ("mode", "pixels"), ("width", "100"), ("height", "100"));

        Assert.Equal(scratch.PathOf("clip-cropped.webm"), output);
        Assert.Equal((100, 100), Size(output));
        Assert.Equal("opus", Inspect.Codec(output, "audio"));
    }

    [Fact]
    public async Task The_preview_outlines_what_is_kept()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.png", 400, 300);
        var engine = Env.Engine();

        var preview = await engine.PreviewAsync([photo], Env.Offered(engine, "crop", [photo]), new PreviewRequest(200), TestContext.Current.CancellationToken);

        using var picture = new MagickImage(preview!.Image!);
        Assert.Equal((200u, 150u), (picture.Width, picture.Height));
        Assert.Equal([new Fact("Keeps", "300 × 300, from 50, 0"), new Fact("Of", "400 × 300")], preview.Facts);
    }
}

using ImageMagick;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

/// <summary>Redacting video: pixels replaced inside each box only during its stretch, boxes that follow a track, and metadata.</summary>
public class VideoRedactTests
{
    static async Task<BatchResult> Redact(string input, string spec, string style = "solid", string colour = "#123456")
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "redact", [input]);
        Assert.Equal(Section.Video, target.Section);
        var result = await engine.RunAsync([input], target.WithParameters(new Dictionary<string, string> { ["spec"] = spec, ["style"] = style, ["colour"] = colour }),
            ct: TestContext.Current.CancellationToken);
        return result;
    }

    static async Task<string> RedactOk(string input, string spec, string style = "solid", string colour = "#123456")
    {
        var result = await Redact(input, spec, style, colour);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    /// <summary>How far an area of one frame of two videos differs, 0 to 255 on average.</summary>
    static double Differs(string a, string b, int frame, RedactBox area)
    {
        var (one, two) = (Inspect.Frame(a, frame, 320, 240), Inspect.Frame(b, frame, 320, 240));
        long sum = 0;
        for (var y = area.Y; y < area.Y + area.Height; y++)
            for (var x = area.X; x < area.X + area.Width; x++)
                for (var c = 0; c < 3; c++)
                    sum += Math.Abs(one[(y * 320 + x) * 4 + c] - two[(y * 320 + x) * 4 + c]);
        return sum / (area.Width * area.Height * 3.0);
    }

    /// <summary>The average colour of an area of one frame.</summary>
    static (double R, double G, double B) Average(string video, int frame, RedactBox area)
    {
        var pixels = Inspect.Frame(video, frame, 320, 240);
        double r = 0, g = 0, b = 0;
        for (var y = area.Y; y < area.Y + area.Height; y++)
            for (var x = area.X; x < area.X + area.Width; x++)
            {
                var at = (y * 320 + x) * 4;
                (b, g, r) = (b + pixels[at], g + pixels[at + 1], r + pixels[at + 2]);
            }
        var n = area.Width * area.Height;
        return (r / n, g / n, b / n);
    }

    static bool IsColour((double R, double G, double B) seen, int r, int g, int b) =>
        Math.Abs(seen.R - r) < 8 && Math.Abs(seen.G - g) < 8 && Math.Abs(seen.B - b) < 8;

    [Fact]
    public async Task Pixels_in_a_box_are_replaced_only_during_its_stretch()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 3);     // 25 frames a second

        var output = await RedactOk(clip, """{"boxes": [{"x": 41, "y": 61, "width": 99, "height": 79, "from": 1, "to": 2}]}""");

        Assert.Equal(scratch.PathOf("clip-redacted.mp4"), output);
        var inside = new RedactBox(41, 61, 99, 79);
        Assert.True(Differs(clip, output, 24, inside) < 6, "frame 24 comes before the stretch");
        foreach (var frame in new[] { 25, 37, 49 })
            Assert.True(IsColour(Average(output, frame, inside), 0x12, 0x34, 0x56), $"frame {frame} should be covered");
        Assert.True(Differs(clip, output, 50, inside) < 6, "frame 50 comes after the stretch");
        // Around the box, the picture is as it was.
        Assert.True(Differs(clip, output, 37, new RedactBox(160, 0, 160, 240)) < 6);
        Assert.Equal([("video", "h264"), ("audio", "aac")], Inspect.Media(output).Streams);
    }

    [Theory]
    [InlineData("pixelate")]
    [InlineData("blur")]
    public async Task Pixelate_and_blur_hide_the_area(string style)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 1);

        // Over the frame number: afterwards it can't be told which frame it is from the box alone.
        var output = await RedactOk(clip, """[{"x": 0, "y": 0, "width": 160, "height": 100}]""", style);

        Assert.True(Differs(clip, output, 10, new RedactBox(0, 0, 160, 100)) > 20, "the area should look different");
        Assert.True(Differs(clip, output, 10, new RedactBox(170, 110, 150, 130)) < 6, "the rest should look the same");
    }

    [Theory]
    [InlineData("pixelate", 160, 140)]
    [InlineData("blur", 160, 140)]
    [InlineData("pixelate", 38, 90)]
    [InlineData("blur", 38, 90)]
    public void The_editor_makes_each_cover_as_the_saved_copys_filters_do(string style, int width, int height)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 1, audio: false);
        // The part under a box, as is and covered with the saved copy's filters; in full colour, where the saved copy
        // has its colour at half size (4:2:0), which blends each block's colour a little with its neighbours'.
        string Crop(string name, string then) => scratch.Ffmpeg(name, ["-i", clip, "-vf", $"select=eq(n\\,10),format=gbrp,crop={width}:{height}:20:30{then},format=bgra",
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra"]);
        var part = File.ReadAllBytes(Crop("part.bgra", ""));
        var saved = File.ReadAllBytes(Crop("saved.bgra", "," + VideoRedactTool.Cover(style, new Colour(0, 0, 0), width, height)));
        // A blur's edges come out a little apart, as ffmpeg's resizing places the small copy's pixels a little differently.
        var near = style == "blur" ? 8 : 3;

        // At the video's size, and from a picture half the size, as the editor's can be a smaller copy.
        var made = (byte[])part.Clone();
        VideoRedactTool.PreviewCover(made, width, height, width, height, style);
        Assert.True(Inspect.Difference(made, saved) < near, $"the cover differs by {Inspect.Difference(made, saved):0.0}");
        var (w, h) = (width / 2, height / 2);
        var half = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 4; c++)
                    half[(y * w + x) * 4 + c] = (byte)((part[(2 * y * width + 2 * x) * 4 + c] + part[(2 * y * width + 2 * x + 1) * 4 + c]
                        + part[((2 * y + 1) * width + 2 * x) * 4 + c] + part[((2 * y + 1) * width + 2 * x + 1) * 4 + c] + 2) / 4);
        VideoRedactTool.PreviewCover(half, w, h, width, height, style);
        var shrunk = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 4; c++)
                    shrunk[(y * w + x) * 4 + c] = saved[((2 * y) * width + 2 * x) * 4 + c];
        // Blocks' edges fall on the smaller picture's own pixels, a pixel from where they are in the video.
        Assert.True(Inspect.Difference(half, shrunk) < near + 7, $"the cover from a smaller picture differs by {Inspect.Difference(half, shrunk):0.0}");
    }

    [Fact]
    public async Task A_box_with_a_track_moves_along_it()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 3);

        // From the left at 0 s to the right at 2 s, then staying there.
        var output = await RedactOk(clip, """{"boxes": [{"x": 20, "y": 120, "width": 60, "height": 60, "track": [{"at": 0, "x": 20, "y": 120, "width": 60, "height": 60}, {"at": 2, "x": 220, "y": 120, "width": 60, "height": 60}]}]}""");

        Assert.True(IsColour(Average(output, 0, new RedactBox(20, 120, 60, 60)), 0x12, 0x34, 0x56));
        Assert.True(IsColour(Average(output, 25, new RedactBox(120, 120, 60, 60)), 0x12, 0x34, 0x56), "halfway at 1 s");
        Assert.True(Differs(clip, output, 25, new RedactBox(20, 120, 40, 60)) < 6, "it has left the start by 1 s");
        Assert.True(IsColour(Average(output, 70, new RedactBox(220, 120, 60, 60)), 0x12, 0x34, 0x56), "it stays at the end");
    }

    [Fact]
    public async Task The_location_and_other_tracks_go_and_the_sound_is_copied()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var plain = scratch.Numbered("plain.mp4", seconds: 1);
        var subtitles = scratch.Text("words.srt", "1\n00:00:00,000 --> 00:00:01,000\nHello\n");
        var clip = scratch.Ffmpeg("clip.mp4", ["-i", plain, "-i", subtitles, "-map", "0", "-map", "1", "-c", "copy", "-c:s", "mov_text",
            "-metadata", "location=+52.3676+004.9041/", "-metadata", "title=Walk"]);
        Assert.NotNull(Inspect.Tag(clip, "location"));

        var result = await Redact(clip, """[{"x": 10, "y": 10, "width": 40, "height": 40}]""");

        var output = Assert.Single(result.Outputs);
        Assert.Equal(["1 area hidden; the location removed"], result.Notes);
        Assert.Null(Inspect.Tag(output, "location"));
        Assert.Equal("Walk", Inspect.Tag(output, "title"));
        Assert.Equal([("video", "h264"), ("audio", "aac")], Inspect.Media(output).Streams);
        Assert.Equal(Inspect.Bitrate(clip), Inspect.Bitrate(output), 1000.0);     // the same sound, copied
    }

    [Theory]
    [InlineData("", "There's nothing to redact: give the areas, such as {\"boxes\": [{\"x\": 10, \"y\": 20, \"width\": 100, \"height\": 80}]}.")]
    [InlineData("""[{"x": 400, "y": 10, "width": 40, "height": 40}]""", "Area 1 is outside the 320 × 240 picture.")]
    [InlineData("""[{"x": 10, "y": 10, "width": 40, "height": 40, "from": 2, "to": 1}]""", "Area 1 has to end after it starts.")]
    public async Task Boxes_that_cant_be_used_are_refused(string spec, string message)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 1);

        var result = await Redact(clip, spec);

        Assert.Equal(message, Assert.Single(result.Failures).Message);
        Assert.Equal(["clip.mp4"], scratch.Files());
    }

    [Fact]
    public async Task Broken_json_says_where()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 1);

        var result = await Redact(clip, """[{"x": 10, "y": 10, "width": 40""");

        var message = Assert.Single(result.Failures).Message;
        Assert.StartsWith("Couldn't read the boxes: ", message);
        Assert.EndsWith(", on line 1.", message);
    }

    [Fact]
    public async Task A_face_is_followed_as_it_moves()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        Assert.SkipUnless(FaceFollow.CanFollow && Redaction.CanFindFaces, "Windows can't find or follow faces on this PC");
        using var scratch = new Scratch();
        var portrait = Path.Combine(AppContext.BaseDirectory, "Fixtures", "portrait.jpg");
        // The portrait sliding right across a grey picture, 60 pixels a second.
        var walk = scratch.Ffmpeg("walk.mp4", ["-f", "lavfi", "-i", "color=c=gray:s=960x540:r=25:d=3", "-loop", "1", "-i", portrait,
            "-filter_complex", "[1:v]scale=-2:420[p];[0:v][p]overlay=x='40+60*t':y=60:shortest=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-t", "3"]);
        using var first = new MagickImage(await Task.Run(() => Ffmpeg.FrameAsync(Env.Tools, walk, 0, 0, TestContext.Current.CancellationToken)));
        var face = Assert.Single(await Redaction.FindFacesAsync(first));

        var track = await FaceFollow.TrackAsync(Env.Tools, walk, face, 0, 3, new ActionProgress<double>(_ => { }), TestContext.Current.CancellationToken);

        Assert.True(track.Count > 20, $"{track.Count} places");
        var moved = track[^1].X - track[0].X;
        Assert.InRange(moved, 120, 220);               // about 180 pixels in 3 seconds
        Assert.InRange(track[^1].Y - track[0].Y, -20, 20);
    }
}

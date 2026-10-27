using System.Globalization;

namespace Tanjelo.Core.Tests;

/// <summary>Trimming video to the frame: re-encoded where it must be, copied where both cuts are on keyframes.</summary>
public class TrimTests
{
    /// <summary>
    /// Five seconds at 29.97 frames a second with B-frames, a keyframe every 30 frames (0, 30, 60…) and a beep at every
    /// whole second; each frame shows its number.
    /// </summary>
    static string Clip(Scratch scratch, string name = "clip.mp4", params string[] extra) =>
        scratch.Numbered(name, seconds: 5, rate: "30000/1001",
            encode: ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "30", "-sc_threshold", "0", "-bf", "2", "-c:a", "aac", .. extra]);

    static double FrameTime(int n) => n * 1001 / 30000.0;

    static async Task<BatchResult> Trim(string input, string range, string mode = "auto")
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "trim", [input]).WithParameters(new Dictionary<string, string> { ["range"] = range, ["mode"] = mode });
        return await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
    }

    static async Task<(string Output, string Note)> TrimOk(string input, string range, string mode = "auto")
    {
        var result = await Trim(input, range, mode);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return (Assert.Single(result.Outputs), Assert.Single(result.Notes));
    }

    /// <summary>Checks that a trimmed copy holds exactly frames <paramref name="first"/> to <paramref name="last"/> of the source.</summary>
    static void HoldsFrames(string output, string source, int first, int last)
    {
        Assert.Equal(last - first + 1, Inspect.FrameCount(output));
        foreach (var (at, expected) in new[] { (0, first), (last - first, last) })
        {
            var pixels = Inspect.Frame(output, at, 320, 240);
            Assert.Equal(expected, Inspect.WhichFrame(pixels, 320, 240, source, [expected - 1, expected, expected + 1]));
        }
    }

    /// <summary>Checks the sound is where it was against the picture: a beep at a whole second sits as far from the first frame as before.</summary>
    static void InStep(string output, double firstFrame)
    {
        var start = Inspect.PictureStart(output);
        var beep = Inspect.Beeps(output).First(b => b > start + 0.05);
        var expected = Math.Ceiling(firstFrame + 0.05) - firstFrame;
        Assert.True(Math.Abs(beep - start - expected) < 0.012, $"the first beep is {beep - start:0.000} s after the first frame, not {expected:0.000} s");
    }

    [Fact]
    public async Task Cuts_between_keyframes_are_re_encoded_to_the_exact_frame()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = Clip(scratch);

        // 1.5 s is nearest frame 45 (1.5015 s); 3.2 s is nearest frame 96 (3.2032 s), the first one left out.
        var (output, note) = await TrimOk(clip, "0:01.5-0:03.2");

        Assert.Equal(scratch.PathOf("clip-trimmed.mp4"), output);
        Assert.Equal("Re-encoded, so each cut is on the exact frame", note);
        HoldsFrames(output, clip, 45, 95);
        InStep(output, FrameTime(45));
        Assert.Equal(FrameTime(96) - FrameTime(45), Inspect.Media(output).Duration, 2);
    }

    [Fact]
    public async Task Cuts_on_keyframes_are_copied_without_re_encoding()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = Clip(scratch);

        var (output, note) = await TrimOk(clip, "0:01.001-0:03.003");

        Assert.Equal("Copied without re-encoding: both cuts are on keyframes", note);
        HoldsFrames(output, clip, 30, 89);
        // The very same packets, byte for byte the same size, as the source's from frame 30.
        Assert.Equal(Inspect.PacketSizes(clip, FrameTime(30)), Inspect.PacketSizes(output));
        Assert.Equal(0, Inspect.PictureStart(output), 3);
        InStep(output, FrameTime(30));
    }

    [Fact]
    public async Task Lossless_mode_widens_the_cuts_to_the_keyframes_around_them()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = Clip(scratch);

        var (output, note) = await TrimOk(clip, "1.5-3.2", "lossless");

        Assert.Equal("Cut at the keyframes around it, without re-encoding: 0:00:01.001 to 0:00:04.004", note);
        HoldsFrames(output, clip, 30, 119);
        InStep(output, FrameTime(30));
    }

    [Fact]
    public async Task Exact_mode_re_encodes_even_on_keyframes_and_a_part_can_run_to_the_end()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = Clip(scratch);
        var total = Inspect.FrameCount(clip);

        var (exact, exactNote) = await TrimOk(clip, "0:01.001-0:03.003", "exact");
        var (rest, restNote) = await TrimOk(clip, "2.002-");

        Assert.Equal("Re-encoded, so each cut is on the exact frame", exactNote);
        HoldsFrames(exact, clip, 30, 89);
        Assert.Equal("Copied without re-encoding: both cuts are on keyframes", restNote);
        HoldsFrames(rest, clip, 60, total - 1);
        InStep(rest, FrameTime(60));
    }

    [Fact]
    public async Task A_phone_videos_turn_and_its_tags_are_kept()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var stored = Clip(scratch, "stored.mp4", "-metadata", "title=Numbers");
        var portrait = scratch.Ffmpeg("portrait.mp4", ["-display_rotation:v:0", "90", "-i", stored, "-c", "copy", "-map_metadata", "0"]);
        Assert.Equal((240, 320), Inspect.Shown(portrait));

        var (exact, _) = await TrimOk(portrait, "1.5-3.2");
        var (copied, _) = await TrimOk(portrait, "1.001-3.003");

        foreach (var output in new[] { exact, copied })
        {
            Assert.Equal((240, 320), Inspect.Shown(output));
            Assert.Equal("Numbers", Inspect.Tag(output, "title"));
        }
    }

    [Theory]
    [InlineData("0:09-", "0:09 is past the end of this video, which is 0:05 long.")]
    [InlineData("0-", "That keeps all of it, so there's nothing to trim.")]
    [InlineData("1.5-1.51", "Keep at least one frame: the end has to come after the start.")]
    public async Task Ranges_that_keep_nothing_or_everything_are_refused(string range, string message)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = Clip(scratch);

        var result = await Trim(clip, range);

        Assert.Equal(message, Assert.Single(result.Failures).Message);
        Assert.Equal(["clip.mp4"], scratch.Files());
    }
}

/// <summary>Trimming sound: to the sample in lossless formats, at the same bitrate in lossy ones, with tags and cover art.</summary>
public class TrimAudioTests
{
    /// <summary>Silence with one loud sample at <paramref name="click"/>, so where a cut lands can be read to the sample.</summary>
    static string Click(Scratch scratch, string name, int click, double seconds = 5, params string[] encode) =>
        scratch.Ffmpeg(name, ["-f", "lavfi", "-i", $"aevalsrc='if(eq(n,{click}),0.9,0)':s=44100:d={seconds.ToString(CultureInfo.InvariantCulture)}", .. encode]);

    /// <summary>A file's sound as 16-bit mono samples.</summary>
    static short[] Samples(string path)
    {
        using var temp = new Scratch();
        var raw = temp.Ffmpeg("sound.raw", ["-i", path, "-map", "0:a:0", "-ac", "1", "-f", "s16le", "-acodec", "pcm_s16le"]);
        var bytes = File.ReadAllBytes(raw);
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    static int Loudest(short[] samples) => Array.IndexOf(samples, samples.MaxBy(s => Math.Abs((int)s)));

    static async Task<string> Trim(string input, string range)
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "trim", [input]);
        Assert.Equal(Section.Audio, target.Section);
        Assert.DoesNotContain("mode", target.Parameters.Keys);
        var result = await engine.RunAsync([input], target.WithParameters(new Dictionary<string, string> { ["range"] = range }), ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return Assert.Single(result.Outputs);
    }

    [Theory]
    [InlineData("tone.wav", "0.5-1.25", 30000, 22050, 33075)]          // from the start: 0.5 s is sample 22,050
    [InlineData("tone.wav", "2.5-3", 120000, 110250, 22050)]           // further in, after a seek
    [InlineData("tone.flac", "0:02.5-0:03", 120000, 110250, 22050)]
    public async Task Lossless_sound_is_cut_to_the_sample(string name, string range, int click, int first, int length)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var input = Click(scratch, name, click);

        var output = await Trim(input, range);

        Assert.Equal(Formats.Key(input), Formats.Key(output));
        var samples = Samples(output);
        Assert.Equal(length, samples.Length);
        Assert.Equal(click - first, Loudest(samples));
    }

    [Theory]
    [InlineData("song.mp3", "-c:a", "libmp3lame", "-b:a", "192k", "-id3v2_version", "3")]
    [InlineData("song.m4a", "-c:a", "aac", "-b:a", "128k")]
    public async Task Lossy_sound_keeps_its_bitrate_tags_and_cover(string name, params string[] encode)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var cover = scratch.Image("cover.png", 64, 64);
        var input = scratch.Ffmpeg(name, ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100:duration=6", "-i", cover,
            "-map", "0:a", "-map", "1:v", "-c:v", "png", "-disposition:v:0", "attached_pic", .. encode, "-metadata", "title=Song", "-metadata", "artist=Band"]);

        var output = await Trim(input, "1-4.5");

        var before = Inspect.Bitrate(input);
        var after = Inspect.Bitrate(output);
        Assert.True(Math.Abs(after - before) <= before * 0.12, $"{after} bps, the original had {before}");
        Assert.InRange(Inspect.Media(output).Duration, 3.45, 3.6);
        Assert.Equal(("Song", "Band"), (Inspect.Tag(output, "title"), Inspect.Tag(output, "artist")));
        Assert.Contains(Inspect.Media(output).Streams, s => s.Type == "video");     // the cover
    }
}

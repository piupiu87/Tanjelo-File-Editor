using System.Globalization;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

/// <summary>Bleeping: a tone or silence inside each stretch, the sound untouched outside, a video's picture copied.</summary>
public class BleepTests
{
    static async Task<BatchResult> Bleep(string input, string ranges, string sound = "tone", string pitch = "1000")
    {
        var engine = Env.Engine();
        var target = Env.Offered(engine, "bleep", [input]).WithParameters(new Dictionary<string, string> { ["ranges"] = ranges, ["sound"] = sound, ["pitch"] = pitch });
        return await engine.RunAsync([input], target, ct: TestContext.Current.CancellationToken);
    }

    static async Task<(string Output, string Note)> BleepOk(string input, string ranges, string sound = "tone", string pitch = "1000")
    {
        var result = await Bleep(input, ranges, sound, pitch);
        Assert.True(result.Failures.Count == 0, string.Join("; ", result.Failures.Select(f => f.Message)));
        return (Assert.Single(result.Outputs), Assert.Single(result.Notes));
    }

    /// <summary>A file's sound as 16-bit samples, channels side by side.</summary>
    static short[] Samples(string path)
    {
        using var temp = new Scratch();
        var raw = temp.Ffmpeg("sound.raw", ["-i", path, "-map", "0:a:0", "-f", "s16le", "-acodec", "pcm_s16le"]);
        var bytes = File.ReadAllBytes(raw);
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    /// <summary>The level in dB of the sound between two times, after a narrow band-pass around <paramref name="hertz"/>, or of all of it.</summary>
    static double Level(string path, double from, double to, int? hertz = null)
    {
        var band = hertz is { } f ? $",bandpass=f={f}:width_type=q:w=8,bandpass=f={f}:width_type=q:w=8" : "";
        var result = ProcessRunner.RunAsync(Env.Tools.Ffmpeg!,
            ["-hide_banner", "-nostdin", "-nostats", "-v", "info", "-i", path, "-af",
                string.Create(CultureInfo.InvariantCulture, $"atrim={from}:{to}{band},astats=metadata=0:measure_perchannel=none"), "-f", "null", "-"],
            CancellationToken.None).GetAwaiter().GetResult();
        var match = System.Text.RegularExpressions.Regex.Match(result.StdErr, @"RMS level dB: (-?[\d.]+|-inf)");
        Assert.True(match.Success, result.StdErr);
        return match.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Stretches_are_read_sorted_and_joined()
    {
        Assert.Equal([(10.0, 13.0), (20, 21.5)], BleepTool.ParseRanges("0:20-0:21.5, 0:11-0:13; 0:10-0:12"));
        Assert.Equal("0:10-0:13, 0:20-0:21.5", BleepTool.FormatRanges([(20, 21.5), (10, 13)]));
        var error = Assert.Throws<ConversionException>(() => BleepTool.ParseRanges("0:10-0:12, 0:20"));
        Assert.Equal("Couldn't read '0:20' as a stretch. Write each as from-to, such as 0:10-0:12.", error.Message);
    }

    [Fact]
    public async Task Inside_each_stretch_only_the_tone_is_heard_and_outside_the_sound_is_unchanged()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var song = scratch.Tone("song.wav", seconds: 4, channels: 2);

        var (output, note) = await BleepOk(song, "0:01-0:01.5, 0:02.25-0:02.5");

        Assert.Equal(scratch.PathOf("song-bleeped.wav"), output);
        Assert.Equal("2 stretches bleeped", note);
        // Inside, a millisecond and a half in from each end: the 1 kHz tone and none of the 440 Hz.
        foreach (var (from, to) in new[] { (1.0, 1.5), (2.25, 2.5) })
        {
            Assert.True(Level(output, from + 0.002, to - 0.002, 440) < -45, $"the sound is still there in {from}-{to}");
            Assert.True(Level(output, from + 0.002, to - 0.002, 1000) > -25, $"there's no tone in {from}-{to}");
        }
        // Outside, every sample as it was.
        var (before, after) = (Samples(song), Samples(output));
        Assert.Equal(before.Length, after.Length);
        int Sample(double seconds) => (int)(seconds * 44100) * 2;
        foreach (var (from, to) in new[] { (0.0, 0.998), (1.502, 2.248), (2.502, 4.0) })
            Assert.True(before.AsSpan(Sample(from), Sample(to) - Sample(from)).SequenceEqual(after.AsSpan(Sample(from), Sample(to) - Sample(from))),
                $"the sound changed between {from} and {to}");
    }

    [Fact]
    public async Task Silence_leaves_nothing_inside_and_a_videos_picture_is_copied()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("talk.mp4", seconds: 4);

        var (output, note) = await BleepOk(clip, "0:00.9-0:01.2", "silence");

        Assert.Equal("1 stretch silenced", note);
        Assert.True(Level(output, 0.93, 1.17) < -60, "the beep at 1 s should be silenced");
        Assert.True(Level(output, 1.95, 2.05) > -20, "the beep at 2 s should still be there");
        // The very same picture packets: copied, not re-encoded.
        Assert.Equal(Inspect.PacketSizes(clip, 0, 100), Inspect.PacketSizes(output, 0, 100));
        Assert.Equal([("video", "h264"), ("audio", "aac")], Inspect.Media(output).Streams);
    }

    [Theory]
    [InlineData("", "There's nothing to bleep: give the stretches, such as 0:10-0:12.")]
    [InlineData("0:09-0:10", "0:09 is past the end of this file, which is 0:02 long.")]
    [InlineData("soon", "Couldn't read 'soon' as a stretch. Write each as from-to, such as 0:10-0:12.")]
    public async Task Stretches_that_cant_be_bleeped_are_refused(string ranges, string message)
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var song = scratch.Tone("song.mp3");

        var result = await Bleep(song, ranges);

        Assert.Equal(message, Assert.Single(result.Failures).Message);
        Assert.Equal(["song.mp3"], scratch.Files());
    }
}

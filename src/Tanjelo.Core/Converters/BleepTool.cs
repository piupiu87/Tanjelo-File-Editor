using System.Globalization;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Covers stretches of the sound with a tone or silence. The sound is worked on in blocks of 64 samples, so a bleep
/// starts and stops within about a millisecond and a half of its times; everywhere else the sound is left as it was.
/// A video's picture is copied as it is, and only its sound is re-encoded.
/// </summary>
public sealed class BleepTool(Toolbox tools) : IConverter
{
    public static readonly Choice[] Sounds = [new("tone", "Tone"), new("silence", "Silence")];

    /// <summary>How loud the tone is, from 0 to 1: about −13 dB, plainly a bleep without being harsh.</summary>
    const double ToneLevel = 0.3;

    static Target Entry(Section section) => new Target("bleep", "Bleep…", "Tone or silence over parts", section)
    {
        Kind = TargetKind.Tool,
        Running = "Bleeping…",
        Done = "Bleeped",
        Confirm = "Save",
        Editor = EditorKind.Bleep,
    }.WithOptions(
        new Option("ranges", "Bleep", OptionKind.Text, "") { Optional = true, Remember = false, Hint = "Stretches to cover, such as 0:10-0:12, 0:20-0:21.5." },
        new Option("sound", "With", OptionKind.Choice, "tone") { Choices = Sounds },
        new Option("pitch", "Pitch", OptionKind.Number, "1000") { Min = 200, Max = 4000, Whole = true, Unit = "Hz", ShownWhen = new("sound", "tone") });

    static readonly Target VideoEntry = Entry(Section.Video);
    static readonly Target AudioEntry = Entry(Section.Audio);

    public IEnumerable<Target> TargetsFor(string input)
    {
        if (tools.Ffmpeg is null || tools.Ffprobe is null) return [];
        var key = Formats.Key(input);
        return Formats.Videos.Contains(key) ? [VideoEntry] : Formats.Audio.Contains(key) ? [AudioEntry] : [];
    }

    /// <summary>Stretches written "0:10-0:12, 0:20-0:21.5", sorted, with overlapping ones joined.</summary>
    public static IReadOnlyList<(double Start, double End)> ParseRanges(string text)
    {
        var ranges = new List<(double Start, double End)>();
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!OptionValues.TryRange(part, out var range) || range.End is not { } end)
                throw new ConversionException($"Couldn't read '{part}' as a stretch. Write each as from-to, such as 0:10-0:12.");
            ranges.Add((range.Start, end));
        }
        return Join(ranges);
    }

    public static string FormatRanges(IEnumerable<(double Start, double End)> ranges) =>
        string.Join(", ", ranges.OrderBy(r => r.Start).Select(r => $"{OptionValues.FormatTime(r.Start)}-{OptionValues.FormatTime(r.End)}"));

    /// <summary>Sorted, with stretches that overlap or touch made one.</summary>
    public static IReadOnlyList<(double Start, double End)> Join(IEnumerable<(double Start, double End)> ranges)
    {
        var joined = new List<(double Start, double End)>();
        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            if (joined.Count > 0 && range.Start <= joined[^1].End) joined[^1] = (joined[^1].Start, Math.Max(joined[^1].End, range.End));
            else joined.Add(range);
        }
        return joined;
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        if (media.Audio is null) throw new ConversionException("This file has no sound to bleep.");
        var ranges = ParseRanges(target.Text("ranges"));
        if (ranges.Count == 0) throw new ConversionException("There's nothing to bleep: give the stretches, such as 0:10-0:12.");
        if (ranges[0].Start >= media.Duration)
            throw new ConversionException($"{OptionValues.FormatTime(ranges[0].Start)} is past the end of this file, which is {OptionValues.FormatTime(media.Duration)} long.");
        var sound = target.Choice("sound");

        var key = Formats.Key(input);
        var video = media.Video is not null;
        var ext = !video || Ffmpeg.CanEncodeVideo(key) ? Formats.Ext(input) : "mkv";
        var output = OutputPaths.Suffixed(input, "bleeped", ext == Formats.Ext(input) ? null : ext);
        await RenderAsync(tools, input, media, ranges, sound, target.Number("pitch"), output, video ? VideoSound(ext, media) : Ffmpeg.KeepSound(key, media),
            ctx.Progress, ctx.Cancellation);
        var stretches = ranges.Count == 1 ? "1 stretch" : $"{ranges.Count} stretches";
        ctx.Note(sound == "tone" ? $"{stretches} bleeped" : $"{stretches} silenced");
        return [output];
    }

    /// <summary>
    /// A copy for the editor to play while it's open: the picture copied from <paramref name="source"/> (the file playing,
    /// which may be a smaller copy of the original) and the sound bleeped, as AAC. About a second for two minutes.
    /// </summary>
    public static async Task<string> PreviewAsync(Toolbox tools, string source, IReadOnlyList<(double Start, double End)> ranges, string sound, double pitch,
        string folder, CancellationToken ct)
    {
        var media = await Ffmpeg.ProbeAsync(tools, source, ct);
        var output = Path.Combine(folder, "bleeped-" + Guid.NewGuid().ToString("N")[..8] + (media.Video is not null ? ".mp4" : ".m4a"));
        await RenderAsync(tools, source, media, ranges, sound, pitch, output, ["-c:a", "aac", "-b:a", "192k"], new ActionProgress<double>(_ => { }), ct);
        return output;
    }

    static async Task RenderAsync(Toolbox tools, string input, MediaInfo media, IReadOnlyList<(double Start, double End)> ranges, string sound, double pitch,
        string output, string[] encode, IProgress<double> progress, CancellationToken ct)
    {
        var within = ranges.Count == 0 ? "0" : string.Join("+", ranges.Select(r => string.Create(CultureInfo.InvariantCulture, $"between(t,{r.Start:0.######},{r.End:0.######})")));
        var rate = media.SampleRate > 0 ? media.SampleRate : 48000;
        var layout = media.ChannelLayout is { Length: > 0 } named ? named : media.Channels switch { 1 => "mono", 2 => "stereo", var n => $"{Math.Max(1, n)}c" };
        // Blocks of 64 samples, so the sound is switched within a millisecond and a half of each time.
        var muted = $"[0:a:0]asetnsamples=n=64:p=0,volume=0:enable='{within}'";
        var graph = sound == "tone"
            ? $"{muted}[kept];sine=frequency={pitch.ToString(CultureInfo.InvariantCulture)}:sample_rate={rate}:samples_per_frame=64," +
              $"volume={(ToneLevel * 8).ToString(CultureInfo.InvariantCulture)},volume=0:enable='not({within})',aformat=sample_rates={rate}:channel_layouts={layout}[tone];" +
              "[kept][tone]amix=inputs=2:duration=first:normalize=0[a]"
            : $"{muted}[a]";
        var video = media.Video is not null;
        string[] bleeped = ["-filter_complex", graph, "-map", "[a]", .. encode, "-map_metadata", "0", .. MediaTools.FastStart(output)];
        // The picture copied, with subtitles or cover art where the container takes them; without, if it doesn't.
        string[][] plans = video
            ? [["-map", "0:V:0", "-c:v", "copy", "-map", "0:s?", "-c:s", "copy", .. bleeped], ["-map", "0:V:0", "-c:v", "copy", .. bleeped]]
            : [["-map", "0:v?", "-c:v", "copy", .. bleeped], bleeped];
        ConversionException? failed = null;
        foreach (var plan in plans)
        {
            try
            {
                await Ffmpeg.RunAsync(tools, input, output, plan, media.Duration, progress, ct);
                return;
            }
            catch (ConversionException ex)
            {
                failed = ex;
                OutputPaths.TryDelete(output);
            }
        }
        throw failed!;
    }

    /// <summary>The sound a video's container takes, at the bitrate it had where that's known.</summary>
    static string[] VideoSound(string ext, MediaInfo media)
    {
        var kbps = string.Create(CultureInfo.InvariantCulture, $"{Math.Max(128, (int)Math.Round(media.AudioBitrate / 1000.0))}k");
        return ext switch
        {
            "webm" => ["-c:a", "libopus", "-b:a", kbps],
            "avi" => ["-c:a", "libmp3lame", "-b:a", kbps],
            "wmv" => ["-c:a", "wmav2", "-b:a", kbps],
            _ => ["-c:a", "aac", "-b:a", kbps],
        };
    }
}

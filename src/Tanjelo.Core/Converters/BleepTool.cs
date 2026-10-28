using System.Globalization;

namespace Tanjelo.Core.Converters;

/// <summary>A stretch of sound to cover, in seconds, and what covers it: a tone at a pitch in hertz, or silence.</summary>
public readonly record struct Bleep(double Start, double End, bool Tone = true, int Pitch = 1000)
{
    /// <summary>"0:10-0:12 tone 1000" or "0:20-0:21.5 silence", as <see cref="BleepTool.ParseBleeps"/> reads it.</summary>
    public override string ToString() =>
        $"{OptionValues.FormatTime(Start)}-{OptionValues.FormatTime(End)} " + (Tone ? $"tone {Pitch.ToString(CultureInfo.InvariantCulture)}" : "silence");

    /// <summary>What covers it, as people read it: "Tone, 1000 Hz" or "Silence".</summary>
    public string Cover => Tone ? $"Tone, {Pitch.ToString(CultureInfo.InvariantCulture)} Hz" : "Silence";

    public bool SameCover(Bleep other) => Tone == other.Tone && (!Tone || Pitch == other.Pitch);
}

/// <summary>
/// Covers stretches of the sound, each with a tone of its own pitch or with silence. The sound is worked on in blocks of
/// 64 samples, so a bleep starts and stops within about a millisecond and a half of its times; everywhere else the
/// sound is left as it was. A video's picture is copied as it is, and only its sound is re-encoded.
/// </summary>
public sealed class BleepTool(Toolbox tools) : IConverter
{
    public static readonly Choice[] Sounds = [new("tone", "Tone"), new("silence", "Silence")];

    /// <summary>The pitches a tone can have, in hertz.</summary>
    public const int LowestPitch = 200, HighestPitch = 4000;

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
        new Option("ranges", "Bleep", OptionKind.Text, "")
        {
            Optional = true,
            Remember = false,
            Hint = "Stretches to cover, such as 0:10-0:12, 0:20-0:21.5. Each takes the sound and pitch below, unless it says its own: 0:10-0:12 silence, 0:20-0:21.5 tone 800.",
        },
        new Option("sound", "With", OptionKind.Choice, "tone") { Choices = Sounds },
        new Option("pitch", "Pitch", OptionKind.Number, "1000") { Min = LowestPitch, Max = HighestPitch, Whole = true, Unit = "Hz", ShownWhen = new("sound", "tone") });

    static readonly Target VideoEntry = Entry(Section.Video);
    static readonly Target AudioEntry = Entry(Section.Audio);

    public IEnumerable<Target> TargetsFor(string input)
    {
        if (tools.Ffmpeg is null || tools.Ffprobe is null) return [];
        var key = Formats.Key(input);
        return Formats.Videos.Contains(key) ? [VideoEntry] : Formats.Audio.Contains(key) ? [AudioEntry] : [];
    }

    /// <summary>
    /// Stretches written "0:10-0:12, 0:20-0:21.5 silence, 0:30-0:31 tone 800", sorted, with ones that overlap or touch
    /// made one when they're covered alike. A stretch without a sound of its own takes <paramref name="tone"/> and
    /// <paramref name="pitch"/>.
    /// </summary>
    public static IReadOnlyList<Bleep> ParseBleeps(string text, bool tone = true, int pitch = 1000)
    {
        var bleeps = new List<Bleep>();
        foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!OptionValues.TryRange(words[0], out var range) || range.End is not { } end)
                throw new ConversionException($"Couldn't read '{part}' as a stretch. Write each as from-to, such as 0:10-0:12, and add silence or tone 800 to cover it otherwise.");
            // After the times: nothing, "silence", "tone", or "tone 800" (or just "800", with or without "Hz").
            var rest = words.Skip(1).Select(w => w.ToLowerInvariant()).Where(w => w != "hz").ToList();
            var (isTone, hertz) = (tone, pitch);
            var number = rest switch
            {
                [] or ["silence"] or ["tone"] => null,
                ["tone", var n] => n,
                [var n] => n,
                _ => "",
            };
            if (rest is ["silence"]) isTone = false;
            else if (rest is ["tone"]) isTone = true;
            else if (number is not null)
            {
                if (!int.TryParse(number.EndsWith("hz", StringComparison.Ordinal) ? number[..^2] : number, NumberStyles.None, CultureInfo.InvariantCulture, out hertz))
                    throw new ConversionException($"Couldn't read '{part}': after the times, write silence, or tone and a pitch such as tone 800.");
                isTone = true;
            }
            if (isTone && hertz is < LowestPitch or > HighestPitch)
                throw new ConversionException($"The pitch in '{part}' must be from {LowestPitch} to {HighestPitch} Hz.");
            bleeps.Add(new Bleep(range.Start, end, isTone, hertz));
        }
        return Join(bleeps);
    }

    public static string FormatBleeps(IEnumerable<Bleep> bleeps) => string.Join(", ", bleeps.OrderBy(b => b.Start));

    /// <summary>Sorted, with stretches that overlap or touch made one when they're covered alike; ones covered differently mustn't overlap.</summary>
    public static IReadOnlyList<Bleep> Join(IEnumerable<Bleep> bleeps)
    {
        var joined = new List<Bleep>();
        foreach (var bleep in bleeps.OrderBy(b => b.Start))
        {
            if (joined.Count > 0 && bleep.Start <= joined[^1].End)
            {
                if (!bleep.SameCover(joined[^1]) && bleep.Start < joined[^1].End)
                    throw new ConversionException($"The stretches {OptionValues.FormatTime(joined[^1].Start)}-{OptionValues.FormatTime(joined[^1].End)} and " +
                        $"{OptionValues.FormatTime(bleep.Start)}-{OptionValues.FormatTime(bleep.End)} overlap but are covered differently.");
                if (bleep.SameCover(joined[^1]))
                {
                    joined[^1] = joined[^1] with { End = Math.Max(joined[^1].End, bleep.End) };
                    continue;
                }
            }
            joined.Add(bleep);
        }
        return joined;
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        if (media.Audio is null) throw new ConversionException("This file has no sound to bleep.");
        var bleeps = ParseBleeps(target.Text("ranges"), target.Choice("sound") == "tone", (int)target.Number("pitch"));
        if (bleeps.Count == 0) throw new ConversionException("There's nothing to bleep: give the stretches, such as 0:10-0:12.");
        if (bleeps[0].Start >= media.Duration)
            throw new ConversionException($"{OptionValues.FormatTime(bleeps[0].Start)} is past the end of this file, which is {OptionValues.FormatTime(media.Duration)} long.");

        var key = Formats.Key(input);
        var video = media.Video is not null;
        var ext = !video || Ffmpeg.CanEncodeVideo(key) ? Formats.Ext(input) : "mkv";
        var output = OutputPaths.Suffixed(input, "bleeped", ext == Formats.Ext(input) ? null : ext);
        await RenderAsync(tools, input, media, bleeps, output, video ? VideoSound(ext, media) : Ffmpeg.KeepSound(key, media), ctx.Progress, ctx.Cancellation);
        var stretches = bleeps.Count == 1 ? "1 stretch" : $"{bleeps.Count} stretches";
        ctx.Note(bleeps.All(b => b.Tone) ? $"{stretches} bleeped" : bleeps.All(b => !b.Tone) ? $"{stretches} silenced" : $"{stretches} covered");
        return [output];
    }

    /// <summary>The tone's level in each channel, as ffmpeg's mix gives it: full in mono, and 3 dB down in each of two or more.</summary>
    internal static double ToneIn(int channels) => channels == 1 ? ToneLevel : ToneLevel / Math.Sqrt(2);

    /// <summary>
    /// The sound muted inside every stretch, mixed with one tone per pitch that sounds only inside its own stretches.
    /// </summary>
    static async Task RenderAsync(Toolbox tools, string input, MediaInfo media, IReadOnlyList<Bleep> bleeps, string output, string[] encode,
        IProgress<double> progress, CancellationToken ct)
    {
        static string Within(IEnumerable<Bleep> some) => some.Any()
            ? string.Join("+", some.Select(b => string.Create(CultureInfo.InvariantCulture, $"between(t,{b.Start:0.######},{b.End:0.######})")))
            : "0";
        var rate = media.SampleRate > 0 ? media.SampleRate : 48000;
        var layout = media.ChannelLayout is { Length: > 0 } named ? named : media.Channels switch { 1 => "mono", 2 => "stereo", var n => $"{Math.Max(1, n)}c" };
        // Blocks of 64 samples, so the sound is switched within a millisecond and a half of each time.
        var graph = $"[0:a:0]asetnsamples=n=64:p=0,volume=0:enable='{Within(bleeps)}'";
        var pitches = bleeps.Where(b => b.Tone).Select(b => b.Pitch).Distinct().ToList();
        if (pitches.Count == 0)
        {
            graph += "[a]";
        }
        else
        {
            graph += "[kept]";
            for (var i = 0; i < pitches.Count; i++)
                graph += $";sine=frequency={pitches[i].ToString(CultureInfo.InvariantCulture)}:sample_rate={rate}:samples_per_frame=64," +
                    $"volume={(ToneLevel * 8).ToString(CultureInfo.InvariantCulture)}," +
                    $"volume=0:enable='not({Within(bleeps.Where(b => b.Tone && b.Pitch == pitches[i]))})',aformat=sample_rates={rate}:channel_layouts={layout}[t{i}]";
            graph += ";[kept]" + string.Concat(pitches.Select((_, i) => $"[t{i}]")) + $"amix=inputs={pitches.Count + 1}:duration=first:normalize=0[a]";
        }
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

/// <summary>
/// The sound of a file as plain 16-bit samples, for an editor to hear bleeps before saving: decoded once, then each set
/// of bleeps is written into a copy of it in moments, sample-exact, instead of having ffmpeg make the whole sound again.
/// </summary>
public sealed class BleepSound
{
    readonly string _wave;
    readonly long _data;
    readonly long _length;
    int _made;

    BleepSound(string wave, long data, long length, int rate, int channels)
    {
        _wave = wave;
        _data = data;
        _length = length;
        Rate = rate;
        Channels = channels;
    }

    public int Rate { get; }
    public int Channels { get; }

    /// <summary>Decodes the first sound track into a WAV in <paramref name="folder"/>: about half a second for five minutes.</summary>
    public static async Task<BleepSound> DecodeAsync(Toolbox tools, string input, string folder, CancellationToken ct)
    {
        var wave = Path.Combine(folder, "sound.wav");
        await Ffmpeg.RunAsync(tools, input, wave, ["-map", "0:a:0", "-vn", "-c:a", "pcm_s16le", "-rf64", "auto"], 0, new ActionProgress<double>(_ => { }), ct);
        return Read(wave);
    }

    /// <summary>Finds the samples in a 16-bit WAV that ffmpeg wrote: where they start, how many bytes, at what rate.</summary>
    internal static BleepSound Read(string wave)
    {
        using var file = File.OpenRead(wave);
        using var reader = new BinaryReader(file);
        var riff = new string(reader.ReadChars(4));
        if (riff is not ("RIFF" or "RF64")) throw new ConversionException("Couldn't read the sound ffmpeg made.");
        reader.ReadUInt32();
        reader.ReadChars(4);
        int rate = 0, channels = 0;
        long big = -1;
        while (file.Position + 8 <= file.Length)
        {
            var id = new string(reader.ReadChars(4));
            long size = reader.ReadUInt32();
            var next = file.Position + size + (size & 1);
            switch (id)
            {
                case "ds64":
                    reader.ReadUInt64();
                    big = (long)reader.ReadUInt64();
                    break;
                case "fmt ":
                    reader.ReadUInt16();
                    channels = reader.ReadUInt16();
                    rate = (int)reader.ReadUInt32();
                    break;
                case "data":
                    var length = big >= 0 && size == uint.MaxValue ? big : size;
                    if (rate <= 0 || channels <= 0) throw new ConversionException("Couldn't read the sound ffmpeg made.");
                    return new BleepSound(wave, file.Position, Math.Min(length, file.Length - file.Position), rate, channels);
            }
            file.Position = next;
        }
        throw new ConversionException("Couldn't read the sound ffmpeg made.");
    }

    /// <summary>
    /// A copy of the sound with these bleeps in, as the saved file will have them (a tone at its level, or silence), as a
    /// new WAV beside the decoded one. The copy is the only work that grows with the length: a second or so for an hour.
    /// </summary>
    public string Write(IReadOnlyList<Bleep> bleeps)
    {
        var made = Path.Combine(Path.GetDirectoryName(_wave)!, $"bleeped-{Interlocked.Increment(ref _made)}.wav");
        File.Copy(_wave, made, overwrite: true);
        using var file = new FileStream(made, FileMode.Open, FileAccess.Write, FileShare.Read);
        var frame = 2 * Channels;
        var frames = _length / frame;
        var level = BleepTool.ToneIn(Channels) * short.MaxValue;
        var buffer = new byte[frame * 4096];
        foreach (var bleep in bleeps)
        {
            long first = Math.Clamp((long)Math.Round(bleep.Start * Rate), 0, frames), last = Math.Clamp((long)Math.Round(bleep.End * Rate), 0, frames);
            file.Position = _data + first * frame;
            for (var at = first; at < last;)
            {
                var count = (int)Math.Min(4096, last - at);
                Array.Clear(buffer);
                if (bleep.Tone)
                {
                    // The same tone as the saved file: one wave from the start of the sound, heard where the stretches are.
                    var step = 2 * Math.PI * bleep.Pitch / Rate;
                    for (var i = 0; i < count; i++)
                    {
                        var sample = (short)Math.Round(level * Math.Sin(step * (at + i)));
                        for (var c = 0; c < Math.Min(2, Channels); c++)
                            BitConverter.TryWriteBytes(buffer.AsSpan(i * frame + c * 2, 2), sample);
                    }
                }
                file.Write(buffer, 0, count * frame);
                at += count;
            }
        }
        return made;
    }
}

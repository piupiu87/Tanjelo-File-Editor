using System.Globalization;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Keeps one stretch of a video or a sound file. Video is cut to the frame: when both cuts fall on keyframes the
/// streams are copied, which is instant and loses nothing; otherwise the kept part is re-encoded so each cut lands on
/// the exact frame. A "smart cut" that re-encodes only around the cuts is left out on purpose: it breaks too easily
/// with some encoders and players. Sound is cut to the sample.
/// </summary>
public sealed class TrimTool(Toolbox tools) : IConverter
{
    public static readonly Choice[] Modes =
    [
        new("auto", "Lossless when the cuts are on keyframes, otherwise exact"),
        new("exact", "Exact: re-encode the part kept"),
        new("lossless", "Lossless: widen the cuts to the keyframes around them"),
    ];

    static readonly Option Range = new("range", "Keep", OptionKind.TimeRange, "")
    {
        Remember = false,
        Hint = "From and to, such as 0:30.033-1:00. Each end goes to the nearest frame; leave out the end to keep the rest.",
    };

    static readonly Target VideoEntry = new Target("trim", "Trim…", "Frame by frame, exact times", Section.Video)
    {
        Kind = TargetKind.Tool,
        Running = "Trimming…",
        Done = "Trimmed",
        Confirm = "Save",
        Editor = EditorKind.Trim,
    }.WithOptions(Range, new Option("mode", "Cut", OptionKind.Choice, "auto") { Choices = Modes });

    static readonly Target AudioEntry = new Target("trim", "Trim…", "On the sound's shape", Section.Audio)
    {
        Kind = TargetKind.Tool,
        Running = "Trimming…",
        Done = "Trimmed",
        Confirm = "Save",
        Editor = EditorKind.Trim,
    }.WithOptions(Range with { Hint = "From and to, such as 0:30.5-1:00, to the millisecond; leave out the end to keep the rest." });

    public IEnumerable<Target> TargetsFor(string input)
    {
        if (tools.Ffmpeg is null || tools.Ffprobe is null) return [];
        var key = Formats.Key(input);
        return Formats.Videos.Contains(key) ? [VideoEntry] : Formats.Audio.Contains(key) ? [AudioEntry] : [];
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        if (media.Duration <= 0) throw new ConversionException("Couldn't tell how long this file is.");
        if (Formats.Audio.Contains(Formats.Key(input))) return [await TrimAudioAsync(input, media, target, ctx)];
        if (media.Video is null) throw new ConversionException("Couldn't find a picture in this video.");
        return [await TrimVideoAsync(input, media, target, ctx)];
    }

    /// <summary>
    /// Keeps one stretch of the sound, cut to the sample. WAV, FLAC and AIFF stay lossless; lossy formats are re-encoded
    /// at the bitrate they had. Tags and cover art come along.
    /// </summary>
    async Task<string> TrimAudioAsync(string input, MediaInfo media, Target target, JobContext ctx)
    {
        var (from, to) = target.TimeRange("range");
        var end = Math.Min(to ?? media.Duration, media.Duration);
        if (from >= media.Duration - 0.0005)
            throw new ConversionException($"{OptionValues.FormatTime(from)} is past the end of this file, which is {OptionValues.FormatTime(media.Duration)} long.");
        if (from <= 0.0005 && end >= media.Duration - 0.0005)
            throw new ConversionException("That keeps all of it, so there's nothing to trim.");

        var rate = media.SampleRate > 0 ? media.SampleRate : 48000;
        // Seeking a second or so ahead spares decoding everything before; the samples are then counted from there.
        var seek = Math.Max(0, Math.Floor(from) - 1);
        long first = (long)Math.Round(from * rate), skip = first - (long)Math.Round(seek * rate), keep = (long)Math.Round(end * rate) - first;
        var cut = string.Create(CultureInfo.InvariantCulture, $"atrim=start_sample={skip}:end_sample={skip + keep},asetpts=PTS-STARTPTS");
        var key = Formats.Key(input);
        var output = OutputPaths.Suffixed(input, "trimmed");
        string[] sound = ["-map", "0:a:0", "-af", cut, .. Ffmpeg.KeepSound(key, media), "-map_metadata", "0", .. key == "mp3" ? new[] { "-id3v2_version", "3" } : []];
        // Cover art, where the format keeps it as a picture of its own; without it if that doesn't work.
        string[] cover = key is "mp3" or "m4a" or "flac" && media.CoverArt ? ["-map", "0:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic"] : [];
        ConversionException? failed = null;
        foreach (var plan in cover.Length > 0 ? new[] { [.. sound, .. cover], sound } : [sound])
        {
            try
            {
                await Ffmpeg.RunRawAsync(tools, ["-ss", Seconds(seek), "-i", input, .. plan, output], end - from, ctx.Progress, ctx.Cancellation);
                return output;
            }
            catch (ConversionException ex)
            {
                failed = ex;
                OutputPaths.TryDelete(output);
            }
        }
        throw failed!;
    }

    /// <summary>The cuts, moved to frames: where the first frame kept starts and where the part ends.</summary>
    /// <param name="ToEnd">Whether the part runs to the end of the video.</param>
    /// <param name="Before">How long the frame before the first one kept lasts, for seeking just ahead of it; 0 at the start.</param>
    /// <param name="Last">How long the last frame kept lasts.</param>
    internal sealed record Cut(double Start, double End, bool ToEnd, bool StartsOnKey, bool EndsOnKey, double Before, double Last)
    {
        public double Length => End - Start;
        public bool OnKeyframes => StartsOnKey && (ToEnd || EndsOnKey);
    }

    /// <summary>Finds the frames at the cuts by reading only the packets around them, so long files stay quick.</summary>
    /// <param name="widen">Moves the cuts out to keyframes: the start back to the one it depends on, the end on to the next.</param>
    internal async Task<Cut> FindCutAsync(string input, MediaInfo media, double from, double? to, bool widen, CancellationToken ct)
    {
        var end = Math.Min(to ?? media.Duration, media.Duration);
        if (from >= media.Duration - 0.001)
            throw new ConversionException($"{OptionValues.FormatTime(from)} is past the end of this video, which is {OptionValues.FormatTime(media.Duration)} long.");
        // Keyframes can be several seconds apart: look far enough around each cut to find them.
        const double around = 12;
        (double, double)[] windows = end - from < 2 * around ? [(from - around, end + around)] : [(from - around, from + around), (end - around, end + around)];
        var frames = await FrameIndex.LoadAsync(tools, input, media, windows, null, ct);

        // The first window starts at the file's start when the cut is near it, and the last one reaches its end.
        var fromStart = from - around <= 0;
        var first = frames.Nearest(from);
        var lastLength = frames.Stop(frames.Count - 1) - frames.Start(frames.Count - 1);
        // The end is where the first frame left out starts, unless the part runs to the end.
        var toEnd = to is null || end >= media.Duration - 0.0005 || end >= frames.End - lastLength / 2;
        var after = toEnd ? frames.Count : frames.Nearest(end);
        if (widen)
        {
            first = frames.KeyAtOrBefore(first);
            if (!toEnd && !frames.IsKey(after))
            {
                if (frames.KeyAfter(after) is { } key) after = key;
                else (toEnd, after) = (true, frames.Count);
            }
        }
        if (after <= first)
            throw new ConversionException("Keep at least one frame: the end has to come after the start.");
        if (fromStart && first == 0 && toEnd)
            throw new ConversionException("That keeps all of it, so there's nothing to trim.");
        var start = frames.Start(first);
        var stop = toEnd ? Math.Max(media.Duration, frames.End) : frames.Start(after);
        return new Cut(start, stop, toEnd, frames.IsKey(first), toEnd || frames.IsKey(after),
            first > 0 ? start - frames.Start(first - 1) : 0, frames.Stop(after - 1) - frames.Start(after - 1));
    }

    async Task<string> TrimVideoAsync(string input, MediaInfo media, Target target, JobContext ctx)
    {
        var (from, to) = target.TimeRange("range");
        var mode = target.Choice("mode");
        var cut = await FindCutAsync(input, media, from, to, widen: mode == "lossless", ctx.Cancellation);

        if (mode != "exact" && cut.OnKeyframes)
        {
            var copied = await CopyAsync(input, cut, ctx);
            if (copied is not null)
            {
                ctx.Note(mode == "lossless" && (Math.Abs(cut.Start - from) > cut.Last / 2 || (to is { } end && !cut.ToEnd && Math.Abs(cut.End - end) > cut.Last / 2))
                    ? $"Cut at the keyframes around it, without re-encoding: {Timecode.Format(cut.Start)} to {Timecode.Format(cut.End)}"
                    : "Copied without re-encoding: both cuts are on keyframes");
                return copied;
            }
        }
        ctx.Note("Re-encoded, so each cut is on the exact frame");
        return await EncodeAsync(input, media, cut, ctx);
    }

    /// <summary>
    /// Copies the streams from the keyframe at the start. A part that stops before the end is cut off there by the
    /// segment muxer, which keeps each keyframe's B-frames with it. Sound from just before the keyframe comes along,
    /// so in MP4 and MOV everything is then shifted to start at the first frame, and an edit list hides that sound,
    /// as players expect. Null when the copy doesn't come out as long as it should, so the caller can re-encode.
    /// </summary>
    async Task<string?> CopyAsync(string input, Cut cut, JobContext ctx)
    {
        var ext = Formats.Ext(input);
        var output = OutputPaths.Suffixed(input, "trimmed");
        // Just past the keyframe's start, so rounding can't land the seek on the keyframe before.
        var seek = cut.Start + Math.Min(0.001, cut.Last / 4);
        string[] map = ["-map", "0:V:0", "-map", "0:a?", "-c", "copy", "-map_metadata", "0"];
        var progress = new ActionProgress<double>(f => ctx.Progress.Report(f * 0.9));
        using var temp = OutputPaths.CreateTemp();
        var part = Path.Combine(temp.Path, "part000." + ext);
        try
        {
            string[] into = cut.ToEnd
                ? ["-avoid_negative_ts", "make_zero", part]
                : ["-t", Seconds(cut.Length + 2), "-f", "segment", "-segment_times", Seconds(cut.Length), "-segment_time_delta", Seconds(cut.Last / 4),
                    "-reset_timestamps", "1", Path.Combine(temp.Path, "part%03d." + ext)];
            await Ffmpeg.RunRawAsync(tools, ["-ss", Seconds(seek), "-i", input, .. map, .. into], cut.Length, progress, ctx.Cancellation);
            if (!File.Exists(part)) return null;
            var lead = ext is "mp4" or "m4v" or "mov" ? await PictureStartAsync(part, ctx.Cancellation) : 0;
            await Ffmpeg.RunRawAsync(tools, ["-i", part, "-map", "0", "-c", "copy", "-map_metadata", "0",
                .. lead > 0.0005 ? new[] { "-output_ts_offset", "-" + Seconds(lead), "-avoid_negative_ts", "disabled" } : [],
                .. MediaTools.FastStart(output), output], 0, progress, ctx.Cancellation);
            var made = await Ffmpeg.ProbeAsync(tools, output, ctx.Cancellation);
            // A copy cut at the wrong keyframe would be off by a second or more.
            if (Math.Abs(made.Duration - cut.Length) <= Math.Max(0.25, cut.Last * 2)) return output;
        }
        catch (ConversionException)
        {
            // Some files won't copy this way; re-encoding still works.
        }
        OutputPaths.TryDelete(output);
        return null;
    }

    /// <summary>When the picture starts in a file, after any sound that comes before it.</summary>
    async Task<double> PictureStartAsync(string path, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(tools.Ffprobe!, ["-v", "error", "-select_streams", "V:0", "-show_entries", "stream=start_time", "-of", "csv=p=0", path], ct);
        return double.TryParse(result.StdOut.Trim().TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ? start : 0;
    }

    /// <summary>
    /// Re-encodes the part, cut to the frame. ffmpeg seeks to halfway into the frame before the first one kept, so the
    /// first frame it keeps is that one whatever the rounding; the picture then ends a quarter of a frame before the
    /// end, and the sound runs exactly from the first frame's start for the part's length.
    /// </summary>
    async Task<string> EncodeAsync(string input, MediaInfo media, Cut cut, JobContext ctx)
    {
        var key = Ffmpeg.CanEncodeVideo(Formats.Key(input)) ? Formats.Key(input) : "mp4";
        var output = OutputPaths.Suffixed(input, "trimmed", key == Formats.Key(input) ? null : key);
        var seek = Math.Max(0, cut.Start - cut.Before / 2);
        var picture = $"setpts=PTS-STARTPTS,trim=end={Seconds(cut.Length - cut.Last / 4)}";
        string[] sound = media.Audio is null ? ["-an"] : ["-map", "0:a:0", "-af", $"atrim=start={Seconds(cut.Start - seek)}:duration={Seconds(cut.Length)},asetpts=PTS-STARTPTS"];
        await Ffmpeg.RunRawAsync(tools, ["-ss", Seconds(seek), "-i", input, "-map", "0:V:0", .. sound, "-sn", "-dn",
            .. Ffmpeg.VideoEncoder(key, picture), .. MediaTools.FastStart(output), output], cut.Length, ctx.Progress, ctx.Cancellation);
        return output;
    }

    /// <summary>Seconds to the microsecond, as ffmpeg reads them: cuts between frames need more than milliseconds.</summary>
    static string Seconds(double seconds) => Math.Max(0, seconds).ToString("0.######", CultureInfo.InvariantCulture);
}

using System.Globalization;

namespace Tanjelo.Core.Converters;

/// <summary>Times on a timeline as the editors show them, "0:01:02.345", always with the hours.</summary>
public static class Timecode
{
    public static string Format(double seconds)
    {
        var ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
    }

    /// <summary>Reads "62.5", "1:02.5" or "0:01:02.500", as typed in an editor or given to tanj.</summary>
    public static bool TryParse(string text, out double seconds) => OptionValues.TryTime(text, out seconds) && double.IsFinite(seconds);
}

/// <summary>
/// Every frame of a video's picture in the order they're shown: when each starts and which are keyframes. Times count
/// from the start of the file, as ffmpeg's <c>-ss</c> does, and frame numbers from 0, as ffmpeg's <c>n</c> does.
/// </summary>
public sealed class FrameIndex
{
    readonly double[] _starts;
    readonly bool[] _key;
    readonly int[] _keys;

    FrameIndex(double[] starts, bool[] key, double end, bool estimated)
    {
        _starts = starts;
        _key = key;
        _keys = Enumerable.Range(0, key.Length).Where(i => key[i]).ToArray();
        End = Math.Max(end, starts.Length > 0 ? starts[^1] : 0);
        Estimated = estimated;
    }

    public int Count => _starts.Length;

    /// <summary>When the last frame stops showing.</summary>
    public double End { get; }

    /// <summary>
    /// A stand-in worked out from the frame rate, used while the real list is read: the numbers and times are right for
    /// most videos, but no keyframes are known.
    /// </summary>
    public bool Estimated { get; }

    /// <summary>The keyframes, in order: cuts there can be made without re-encoding.</summary>
    public IReadOnlyList<int> Keyframes => _keys;

    public double Start(int frame) => _starts[Math.Clamp(frame, 0, Count - 1)];

    /// <summary>When a frame stops showing, which is when the next one starts.</summary>
    public double Stop(int frame) => frame + 1 < Count ? _starts[Math.Max(0, frame + 1)] : End;

    /// <summary>
    /// A quarter of the way into a frame's time on screen: where to seek a player to show that frame, whether it shows
    /// the frame on screen at a time or the frame that starts nearest to it. Halfway would be a tie for the second.
    /// </summary>
    public double Inside(int frame) => Start(frame) + (Stop(frame) - Start(frame)) / 4;

    /// <summary>The frame on screen at a time: the last one to start at or before it.</summary>
    public int At(double seconds)
    {
        var at = Array.BinarySearch(_starts, seconds);
        return Math.Clamp(at >= 0 ? at : ~at - 1, 0, Count - 1);
    }

    /// <summary>The frame that starts nearest to a time.</summary>
    public int Nearest(double seconds)
    {
        var at = At(seconds);
        return at + 1 < Count && _starts[at + 1] - seconds < seconds - _starts[at] ? at + 1 : at;
    }

    /// <summary>A time moved to the nearest frame start, or to the end after the last frame.</summary>
    public double Snap(double seconds)
    {
        var frame = Nearest(seconds);
        return frame == Count - 1 && End - seconds < seconds - _starts[frame] ? End : _starts[frame];
    }

    public bool IsKey(int frame) => frame >= 0 && frame < Count && _key[frame];

    /// <summary>The keyframe a frame depends on: itself, or the last one before it; 0 when none is known.</summary>
    public int KeyAtOrBefore(int frame)
    {
        var at = Array.BinarySearch(_keys, frame);
        return at >= 0 ? _keys[at] : ~at > 0 ? _keys[~at - 1] : 0;
    }

    /// <summary>The first keyframe after a frame; null when there's none, as at the end.</summary>
    public int? KeyAfter(int frame)
    {
        var at = Array.BinarySearch(_keys, frame + 1);
        var index = at >= 0 ? at : ~at;
        return index < _keys.Length ? _keys[index] : null;
    }

    /// <summary>A stand-in for a steady frame rate, from the rate ffprobe reports; see <see cref="Estimated"/>.</summary>
    public static FrameIndex Estimate(double duration, double frameRate)
    {
        var rate = frameRate is > 0 and < 1000 ? frameRate : 25;
        var count = Math.Max(1, (int)Math.Floor(duration * rate + 0.5));
        var starts = Enumerable.Range(0, count).Select(i => i / rate).ToArray();
        return new FrameIndex(starts, new bool[count], duration, estimated: true);
    }

    /// <summary>
    /// Reads ffprobe's list of the picture's packets, one "pts_time,dts_time,duration_time,flags" line each, in the
    /// order they're stored. Packets marked to be discarded (before an edit list's start) aren't shown, so they're left out.
    /// </summary>
    internal static FrameIndex Parse(IEnumerable<string> lines, double startTime, double duration)
    {
        var frames = new List<(double Start, double Length, bool Key)>();
        foreach (var line in lines)
        {
            var fields = line.Split(',');
            if (fields.Length < 4 || fields[3].Contains('D')) continue;
            if (!Seconds(fields[0], out var time) && !Seconds(fields[1], out time)) continue;
            Seconds(fields[2], out var length);
            frames.Add((time - startTime, length, fields[3].Contains('K')));
        }
        frames.Sort((a, b) => a.Start.CompareTo(b.Start));
        // Two packets at one time would be one frame shown twice; keep the first.
        var unique = frames.Where((f, i) => i == 0 || f.Start > frames[i - 1].Start + 1e-7).ToList();
        if (unique.Count == 0) throw new ConversionException("Couldn't find any frames in this video.");
        var last = unique[^1];
        var typical = unique.Count > 1 ? (unique[^1].Start - unique[0].Start) / (unique.Count - 1) : 0;
        var end = last.Start + (last.Length > 0 ? last.Length : typical);
        return new FrameIndex(unique.Select(f => f.Start).ToArray(), unique.Select(f => f.Key).ToArray(),
            end > last.Start ? end : Math.Max(duration, last.Start), estimated: false);

        static bool Seconds(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    /// <summary>
    /// Lists the frames by reading the picture's packets, which is quick (about 6,000 a second) as nothing is decoded.
    /// With <paramref name="windows"/>, only the frames in and around those stretches are read: enough to find exact
    /// cuts in a long file. Their times are right, but frame numbers then count from the first one read.
    /// </summary>
    internal static async Task<FrameIndex> LoadAsync(Toolbox tools, string path, MediaInfo media, IReadOnlyList<(double From, double To)>? windows,
        IProgress<double>? progress, CancellationToken ct)
    {
        var lines = new List<string>();
        string[] interval = windows is { Count: > 0 }
            ? ["-read_intervals", string.Join(',', windows.Select(w =>
                string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, w.From + media.StartTime):0.######}%{w.To + media.StartTime:0.######}")))]
            : [];
        var length = Math.Max(media.Duration, 0.001);
        await Ffmpeg.PipeAsync(tools.Ffprobe!, ["-v", "error", "-select_streams", "V:0", .. interval,
            "-show_entries", "packet=pts_time,dts_time,duration_time,flags", "-of", "csv=p=0", path], async stream =>
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                var count = 0;
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    lines.Add(line);
                    if (progress is not null && ++count % 2000 == 0
                        && double.TryParse(line.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
                        progress.Report(Math.Clamp((at - media.StartTime) / length, 0, 1));
                }
            }, ct);
        return Parse(lines, media.StartTime, media.Duration);
    }
}

/// <summary>The loudest point of the sound in each hundredth of a second, from 0 to 1, for drawing its shape.</summary>
public sealed class Waveform(float[] peaks)
{
    /// <summary>The seconds each peak covers.</summary>
    public const double Step = 0.01;

    /// <summary>Samples a second read for the shape: enough for a picture of the sound, and quick even for an hour.</summary>
    const int Rate = 8000;

    public IReadOnlyList<float> Peaks => peaks;

    public double Duration => peaks.Length * Step;

    /// <summary>The loudest point between two times; 0 outside the sound.</summary>
    public float Peak(double from, double to)
    {
        int first = Math.Max(0, (int)Math.Floor(from / Step)), last = Math.Min(peaks.Length - 1, (int)Math.Ceiling(to / Step) - 1);
        var peak = 0f;
        for (var i = first; i <= Math.Max(first, last) && i < peaks.Length; i++)
            peak = Math.Max(peak, peaks[i]);
        return peak;
    }

    /// <summary>Reads the first sound track as 8 kHz mono and keeps the peak of every hundredth of a second.</summary>
    internal static async Task<Waveform> LoadAsync(Toolbox tools, string path, CancellationToken ct)
    {
        var peaks = new List<float>();
        const int perPeak = (int)(Rate * Step);
        await Ffmpeg.PipeAsync(tools.Ffmpeg!, ["-hide_banner", "-nostdin", "-v", "error", "-i", path, "-map", "0:a:0", "-ac", "1",
            "-ar", Rate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-acodec", "pcm_s16le", "-"], async stream =>
            {
                var buffer = new byte[perPeak * 2 * 256];
                int filled = 0, inPeak = 0, peak = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
                    if (read == 0) break;
                    filled += read;
                    var whole = filled & ~1;
                    for (var i = 0; i < whole; i += 2)
                    {
                        var sample = Math.Abs((int)(short)(buffer[i] | buffer[i + 1] << 8));
                        if (sample > peak) peak = sample;
                        if (++inPeak == perPeak)
                        {
                            peaks.Add(Math.Min(1f, peak / 32768f));
                            (inPeak, peak) = (0, 0);
                        }
                    }
                    // An odd byte waits for its other half.
                    if (whole < filled) buffer[0] = buffer[whole];
                    filled -= whole;
                }
                if (inPeak > 0) peaks.Add(Math.Min(1f, peak / 32768f));
            }, ct);
        return new Waveform([.. peaks]);
    }
}

/// <summary>A small picture of a video at a moment, as 32-bit BGRA pixels, row after row.</summary>
public sealed record Thumbnail(double Time, int Width, int Height, byte[] Pixels);

/// <summary>
/// An audio or video file opened for an editor's timeline: its length and shape, and in the background its frames,
/// the shape of its sound and thumbnails. Everything comes from ffprobe and ffmpeg; nothing is written beside the file.
/// </summary>
public sealed class TimelineMedia
{
    readonly Toolbox _tools;
    readonly MediaInfo _info;

    TimelineMedia(Toolbox tools, string path, MediaInfo info)
    {
        _tools = tools;
        Path = path;
        _info = info;
    }

    public string Path { get; }

    /// <summary>The whole file's length in seconds.</summary>
    public double Duration => _info.Duration;

    public bool HasVideo => _info.Video is not null;
    public bool HasAudio => _info.Audio is not null;

    /// <summary>The picture's size as it plays, after any turn a phone asked for.</summary>
    public int Width => _info.Width;
    public int Height => _info.Height;

    public double FrameRate => _info.FrameRate;
    public int SampleRate => _info.SampleRate;
    public int Channels => _info.Channels;

    /// <summary>ffmpeg's names for the picture's and the sound's codecs, such as "h264" and "aac".</summary>
    public string? VideoCodec => _info.Video;
    public string? AudioCodec => _info.Audio;

    public static async Task<TimelineMedia> OpenAsync(Toolbox tools, string path, CancellationToken ct)
    {
        if (tools.Ffmpeg is null || tools.Ffprobe is null)
            throw new ConversionException("Editing audio and video needs ffmpeg. Install it with: winget install Gyan.FFmpeg.Essentials");
        var info = await Ffmpeg.ProbeAsync(tools, path, ct);
        if (info.Video is null && info.Audio is null)
            throw new ConversionException("There's no picture or sound in this file that ffmpeg can read.");
        if (info.Duration <= 0)
            throw new ConversionException("Couldn't tell how long this file is.");
        return new TimelineMedia(tools, path, info);
    }

    /// <summary>A stand-in for the frames from the frame rate, to work with until <see cref="LoadFramesAsync"/> is done.</summary>
    public FrameIndex EstimatedFrames => FrameIndex.Estimate(Duration, FrameRate);

    /// <summary>Lists every frame and keyframe. An hour of video takes about 20 seconds; progress goes from 0 to 1.</summary>
    public Task<FrameIndex> LoadFramesAsync(IProgress<double>? progress, CancellationToken ct) =>
        FrameIndex.LoadAsync(_tools, Path, _info, null, progress, ct);

    /// <summary>The shape of the sound: about 2.5 seconds for an hour.</summary>
    public Task<Waveform> LoadWaveformAsync(CancellationToken ct) => Waveform.LoadAsync(_tools, Path, ct);

    /// <summary>
    /// Thumbnails <paramref name="height"/> pixels tall, at most one per <paramref name="every"/> seconds, handed to
    /// <paramref name="found"/> as they're made. Only keyframes are decoded, which makes this quick, so each thumbnail
    /// is of the keyframe nearest its place rather than of an exact moment.
    /// </summary>
    public async Task ThumbnailsAsync(int height, double every, Action<Thumbnail> found, CancellationToken ct)
    {
        if (!HasVideo || Width <= 0 || Height <= 0) return;
        var width = Math.Max(2, (int)Math.Round(height * (double)Width / Height / 2) * 2);
        var times = new Queue<double>();
        var timed = new SemaphoreSlim(0);
        var interval = Math.Max(0.04, every).ToString("0.###", CultureInfo.InvariantCulture);
        await Ffmpeg.PipeAsync(_tools.Ffmpeg!, ["-hide_banner", "-nostdin", "-v", "info", "-skip_frame", "nokey", "-i", Path,
            "-map", "0:V:0", "-an", "-sn", "-dn",
            "-vf", $"select='isnan(prev_selected_t)+gte(t-prev_selected_t,{interval})',scale={width}:{height},format=bgra,showinfo",
            "-fps_mode", "passthrough", "-f", "rawvideo", "-"], async stream =>
            {
                while (true)
                {
                    var pixels = new byte[width * height * 4];
                    if (!await Ffmpeg.FillAsync(stream, pixels, ct)) break;
                    // showinfo says when the picture is from, on the other pipe; it normally comes first.
                    var time = await timed.WaitAsync(2000, ct) ? Dequeue() : double.NaN;
                    found(new Thumbnail(time, width, height, pixels));
                }
            }, ct, line =>
            {
                var at = line.IndexOf(" pts_time:", StringComparison.Ordinal);
                if (at < 0 || !line.Contains("showinfo", StringComparison.Ordinal)) return;
                var text = line[(at + 10)..].TrimStart().Split(' ', 2)[0];
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) return;
                lock (times) times.Enqueue(time);
                timed.Release();
            });

        double Dequeue()
        {
            lock (times) return times.Dequeue();
        }
    }

    /// <summary>
    /// A small copy to play when Windows can't play the file itself, such as MPEG-2, ProRes or AIFF: H.264 at most 540
    /// pixels tall with a keyframe every 15 frames, so seeking is quick, and every frame kept at its own time. About
    /// 3 seconds for 2 minutes of 1080p. The copy goes in <paramref name="folder"/>.
    /// </summary>
    public async Task<string> PreviewCopyAsync(string folder, IProgress<double> progress, CancellationToken ct)
    {
        var output = System.IO.Path.Combine(folder, HasVideo ? "preview.mp4" : "preview.m4a");
        string[] args = HasVideo
            ? ["-map", "0:V:0", "-map", "0:a:0?", "-sn", "-dn", "-vf", "scale=-2:'2*trunc(min(540,ih)/2)'", "-c:v", "libx264", "-preset", "ultrafast",
                "-g", "15", "-crf", "24", "-pix_fmt", "yuv420p", "-fps_mode", "passthrough", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"]
            : ["-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "192k"];
        await Ffmpeg.RunAsync(_tools, Path, output, args, Duration, progress, ct);
        return output;
    }

    /// <summary>One frame exactly, as a PNG at most <paramref name="maxSize"/> pixels on its long side (0 for full size).</summary>
    public Task<byte[]> FrameAsync(FrameIndex frames, int frame, int maxSize, CancellationToken ct) =>
        Ffmpeg.FrameAsync(_tools, Path, SeekTime(frames, frame), maxSize, ct);

    /// <summary>
    /// Where to seek ffmpeg for a frame. It shows the first frame at or after the time, so halfway between the frame
    /// before and this one lands on this one even when the times are rounded.
    /// </summary>
    internal static double SeekTime(FrameIndex frames, int frame) =>
        frame <= 0 ? 0 : (frames.Start(frame - 1) + frames.Start(frame)) / 2;
}

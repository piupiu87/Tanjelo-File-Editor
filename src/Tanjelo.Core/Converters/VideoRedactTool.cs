using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;

namespace Tanjelo.Core.Converters;

/// <summary>Where a box is at one moment, for a box that follows something moving; times in seconds.</summary>
public sealed record BoxAt(double At, int X, int Y, int Width, int Height);

/// <summary>
/// An area of a video to hide, in the picture's pixels as it plays (upright), from one time to another; with
/// <see cref="Track"/>, it moves in straight lines between the places it lists.
/// </summary>
public sealed record VideoBox(int X, int Y, int Width, int Height, double From = 0, double? To = null)
{
    public IReadOnlyList<BoxAt>? Track { get; init; }

    /// <summary>Where the box is at a moment: its own place, or along its track.</summary>
    public RedactBox At(double seconds)
    {
        if (Track is not { Count: > 0 } track) return new RedactBox(X, Y, Width, Height);
        if (seconds <= track[0].At) return Box(track[0]);
        for (var i = 1; i < track.Count; i++)
        {
            if (seconds > track[i].At) continue;
            var (a, b) = (track[i - 1], track[i]);
            var f = b.At > a.At ? (seconds - a.At) / (b.At - a.At) : 1;
            return new RedactBox((int)Math.Round(a.X + (b.X - a.X) * f), (int)Math.Round(a.Y + (b.Y - a.Y) * f),
                (int)Math.Round(a.Width + (b.Width - a.Width) * f), (int)Math.Round(a.Height + (b.Height - a.Height) * f));
        }
        return Box(track[^1]);

        static RedactBox Box(BoxAt at) => new(at.X, at.Y, at.Width, at.Height);
    }

    public bool Covers(double seconds) => seconds >= From && (To is not { } to || seconds < to);
}

/// <summary>Reads and writes the boxes of a video redaction as JSON: {"boxes": [{"x": 10, "y": 20, "width": 100, "height": 80, "from": 1.5, "to": 3}]}.</summary>
public static class VideoBoxSpec
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    sealed record File(IReadOnlyList<VideoBox> Boxes);

    public static string Write(IEnumerable<VideoBox> boxes) => JsonSerializer.Serialize(new File(boxes.ToList()), Json);

    /// <summary>Boxes from JSON, or from a .json file when <paramref name="text"/> is a path (found beside <paramref name="besides"/>).</summary>
    public static IReadOnlyList<VideoBox> Read(string text, string besides)
    {
        text = text.Trim();
        if (text.Length == 0) return [];
        if (!text.StartsWith('{') && !text.StartsWith('['))
        {
            var path = Path.GetFullPath(text, OutputPaths.DirectoryOf(besides));
            if (!System.IO.File.Exists(path)) throw new ConversionException(string.Format(Strings.CouldnTFindTheBoxesFile, path));
            text = System.IO.File.ReadAllText(path);
        }
        try
        {
            var boxes = text.TrimStart().StartsWith('[')
                ? JsonSerializer.Deserialize<List<VideoBox>>(text, Json)
                : JsonSerializer.Deserialize<File>(text, Json)?.Boxes;
            return boxes ?? [];
        }
        catch (JsonException ex)
        {
            throw new ConversionException(string.Format(Strings.CouldnTReadTheBoxes, ex.Message.Split(" Path:")[0].Split(" LineNumber:")[0].TrimEnd('.')) +
                (ex.LineNumber is { } line ? string.Format(Strings.OnLine, line + 1) : "."), ex);
        }
    }
}

/// <summary>
/// Hides areas of a video for stretches of time, each as a solid colour, big pixels or a strong blur, worked out the
/// way photos are (<see cref="Redaction"/>). The picture is always re-encoded and the sound copied. Like a redacted
/// photo, the copy loses what could show the hidden parts or where it was made: the location tags, cover pictures,
/// subtitles and data tracks such as a camera's GPS log.
/// </summary>
public sealed class VideoRedactTool(Toolbox tools) : IConverter
{
    static readonly Target Entry = new Target("redact", Strings.Redact, Strings.AreasForStretchesOfTime, Section.Video)
    {
        Kind = TargetKind.Tool,
        Running = Strings.Redacting,
        Done = Strings.Redacted,
        Confirm = Strings.Save,
        Editor = EditorKind.RedactVideo,
    }.WithOptions(
        new Option("spec", Strings.Areas, OptionKind.Text, "")
        {
            Optional = true,
            Remember = false,
            Hint = Strings.TheAreasAsJSONOrAJson +
                Strings.InPixelsAndSecondsLeaveOutTo,
        },
        new Option("style", Strings.Style, OptionKind.Choice, "solid") { Choices = Redaction.Styles },
        new Option("colour", Strings.Colour, OptionKind.Colour, "#000000") { ShownWhen = new("style", "solid") });

    public IEnumerable<Target> TargetsFor(string input) =>
        tools.Ffmpeg is not null && tools.Ffprobe is not null && Formats.Videos.Contains(Formats.Key(input)) ? [Entry] : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        if (media.Video is null || media.Width <= 0 || media.Height <= 0)
            throw new ConversionException(Strings.CouldnTFindAPictureInThis);
        var boxes = VideoBoxSpec.Read(target.Text("spec"), input);
        if (boxes.Count == 0) throw new ConversionException(Strings.ThereSNothingToRedactGiveThe2);
        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            if (box.Width < 1 || box.Height < 1)
                throw new ConversionException(string.Format(Strings.AreaNeedsAWidthAndAHeight, i + 1));
            if (new RedactBox(box.X, box.Y, box.Width, box.Height).Within(media.Width, media.Height) is null && box.Track is not { Count: > 0 })
                throw new ConversionException(string.Format(Strings.AreaIsOutsideThePicture, i + 1, Describe.Pixels(media.Width, media.Height)));
            if (box.To is { } to && to <= box.From)
                throw new ConversionException(string.Format(Strings.AreaHasToEndAfterItStarts, i + 1));
            if (box.From >= media.Duration)
                throw new ConversionException(string.Format(Strings.AreaStartsAtPastTheEndOf, i + 1, OptionValues.FormatTime(box.From), OptionValues.FormatTime(media.Duration)));
        }

        var key = Ffmpeg.CanEncodeVideo(Formats.Key(input)) ? Formats.Key(input) : "mp4";
        var output = OutputPaths.Suffixed(input, "redacted", key == Formats.Key(input) ? null : key);
        var frame = media.FrameRate > 0 ? 1 / media.FrameRate : 0.04;
        var graph = Graph(boxes, target.Choice("style"), target.Colour("colour"), frame, media.Width, media.Height);
        using var temp = OutputPaths.CreateTemp();
        // In a file of its own: a long track makes a graph longer than Windows lets a command line be.
        var script = Path.Combine(temp.Path, "redact.txt");
        await File.WriteAllTextAsync(script, graph, ctx.Cancellation);
        string[] tags = ["-map_metadata", "0", .. MetadataTool.LocationTags.SelectMany(t => new[] { "-metadata", t + "=" })];
        string[] encode = ["-/filter_complex", script, "-map", "[v]", "-map", "0:a?", .. Ffmpeg.VideoEncoder(key, graph: true), .. tags, .. MediaTools.FastStart(output)];
        // The sound is copied when the container is the same; otherwise, or if that fails, it's re-encoded too.
        IEnumerable<string[]> plans = key == Formats.Key(input) ? [[.. encode, "-c:a", "copy"], encode] : [encode];
        await Ffmpeg.RunFirstWorkingAsync(tools, input, output, plans, media.Duration, ctx);
        ctx.Note(Describe.Count(boxes.Count, Strings.OneArea, Strings.NAreas) + Strings.Hidden + (MetadataTool.LocationTags.Any(media.Tags.ContainsKey) ? Strings.TheLocationRemoved : ""));
        return [output];
    }

    /// <summary>
    /// The filter graph: the picture split once per box, each copy cropped to its box and covered, then laid back over
    /// the picture during the box's stretch. Boxes are widened to even pixels, so 4:2:0 colour can't leave an edge
    /// showing; one that moves is placed anew on every frame from its track.
    /// </summary>
    internal static string Graph(IReadOnlyList<VideoBox> boxes, string style, Colour colour, double frame, int width, int height)
    {
        var graph = new StringBuilder();
        graph.Append($"[0:V:0]split={boxes.Count + 1}[base]").Append(string.Concat(boxes.Select((_, i) => $"[c{i}]"))).Append(";\n");
        var places = new List<(string X, string Y)>();
        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            var track = box.Track is { Count: > 0 } t ? Simplify(t) : [new BoxAt(box.From, box.X, box.Y, box.Width, box.Height)];
            // One size for the whole stretch, the largest along the track, so the cover can be cropped once.
            var w = Math.Min(Even(track.Max(p => p.Width) + 4), width - width % 2);
            var h = Math.Min(Even(track.Max(p => p.Height) + 4), height - height % 2);
            string Place(Func<BoxAt, double> centre, int size, int room) =>
                $"clip(2*floor(({Along(track, centre)}-{size / 2})/2),0,{Math.Max(0, room - size)})";
            var (x, y) = (Place(p => p.X + p.Width / 2.0, w, width), Place(p => p.Y + p.Height / 2.0, h, height));
            places.Add((x, y));
            graph.Append($"[c{i}]crop=w={w}:h={h}:x='{x}':y='{y}':exact=1,{Cover(style, colour, w, h)}[s{i}];\n");
        }
        var last = "base";
        for (var i = 0; i < boxes.Count; i++)
        {
            // From a quarter of a frame early, so rounding in the times can't leave a frame out or take one in too many.
            var from = Seconds(boxes[i].From - frame / 4);
            var during = boxes[i].To is { } to ? $"gte(t,{from})*lt(t,{Seconds(to - frame / 4)})" : $"gte(t,{from})";
            graph.Append($"[{last}][s{i}]overlay=x='{places[i].X}':y='{places[i].Y}':enable='{during}'[v{i}];\n");
            last = $"v{i}";
        }
        graph.Append($"[{last}]{Ffmpeg.EvenSize}[v]");
        return graph.ToString();
    }

    /// <summary>
    /// Makes a pixelated or blurred cover in place from the part of a frame under a box, as <see cref="Cover"/>'s
    /// filters make it, and quickly enough for the editor to make one for every frame as it plays.
    /// <paramref name="pixels"/> holds that part as 32-bit BGR, <paramref name="width"/> by <paramref name="height"/>,
    /// which can be smaller than the box is in the video (<paramref name="boxWidth"/> by <paramref name="boxHeight"/>),
    /// as the editor's picture is.
    /// </summary>
    public static void PreviewCover(byte[] pixels, int width, int height, int boxWidth, int boxHeight, string style)
    {
        if (style is not ("pixelate" or "blur")) return;
        var (w, h, sigma) = Smaller(style, boxWidth, boxHeight);
        var small = Shrink(pixels, width, height, w, h);
        if (style == "pixelate")
        {
            // Each block the average of its pixels (scale, flags=area), drawn flat (flags=neighbor).
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    Put(pixels, (y * width + x) * 4, small, ((2 * y + 1) * h / (2 * height) * w + (2 * x + 1) * w / (2 * width)) * 3);
            return;
        }
        // Blurred on the smaller copy, its edges carried on outwards (fillborders, gblur twice), then brought back to
        // size smoothly (flags=bilinear).
        Blur(small, w, h, sigma);
        Blur(small, w, h, sigma);
        var mix = new double[3];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + 0.5) * h / height - 0.5, 0, h - 1);
            var (y0, fy) = ((int)sy, sy - (int)sy);
            var y1 = Math.Min(h - 1, y0 + 1);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5) * w / width - 0.5, 0, w - 1);
                var (x0, fx) = ((int)sx, sx - (int)sx);
                var x1 = Math.Min(w - 1, x0 + 1);
                for (var c = 0; c < 3; c++)
                    mix[c] = (small[(y0 * w + x0) * 3 + c] * (1 - fx) + small[(y0 * w + x1) * 3 + c] * fx) * (1 - fy)
                        + (small[(y1 * w + x0) * 3 + c] * (1 - fx) + small[(y1 * w + x1) * 3 + c] * fx) * fy;
                Put(pixels, (y * width + x) * 4, mix, 0);
            }
        }
    }

    static void Put(byte[] pixels, int at, double[] colour, int from)
    {
        for (var c = 0; c < 3; c++) pixels[at + c] = (byte)Math.Clamp(Math.Round(colour[from + c]), 0, 255);
        pixels[at + 3] = 255;
    }

    /// <summary>A picture made smaller by averaging, three channels to a pixel; a pixel of the small one with none of the picture's centres in it takes the nearest.</summary>
    static double[] Shrink(byte[] pixels, int width, int height, int w, int h)
    {
        var sums = new double[w * h * 3];
        var counts = new int[w * h];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var to = (2 * y + 1) * h / (2 * height) * w + (2 * x + 1) * w / (2 * width);
                for (var c = 0; c < 3; c++) sums[to * 3 + c] += pixels[(y * width + x) * 4 + c];
                counts[to]++;
            }
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0)
            {
                var (x, y) = ((2 * (i % w) + 1) * width / (2 * w), (2 * (i / w) + 1) * height / (2 * h));
                for (var c = 0; c < 3; c++) sums[i * 3 + c] = pixels[(y * width + x) * 4 + c];
                continue;
            }
            for (var c = 0; c < 3; c++) sums[i * 3 + c] /= counts[i];
        }
        return sums;
    }

    /// <summary>A Gaussian blur of a small picture of three channels, across and then down, its edges carried on outwards.</summary>
    static void Blur(double[] small, int w, int h, double sigma)
    {
        var radius = (int)Math.Ceiling(sigma * 3);
        var weights = Enumerable.Range(-radius, 2 * radius + 1).Select(d => Math.Exp(-d * d / (2 * sigma * sigma))).ToArray();
        var total = weights.Sum();
        var line = new double[Math.Max(w, h) * 3];
        foreach (var (count, length, step, along) in new[] { (h, w, 3, w * 3), (w, h, w * 3, 3) })
            for (var i = 0; i < count; i++)
            {
                var start = i * along;
                for (var j = 0; j < length; j++)
                    for (var c = 0; c < 3; c++)
                    {
                        double sum = 0;
                        for (var d = -radius; d <= radius; d++)
                            sum += weights[d + radius] * small[start + Math.Clamp(j + d, 0, length - 1) * step + c];
                        line[j * 3 + c] = sum / total;
                    }
                for (var j = 0; j < length; j++)
                    for (var c = 0; c < 3; c++) small[start + j * step + c] = line[j * 3 + c];
            }
    }

    /// <summary>
    /// The smaller copy a cover for a box of this size is made from: for pixelating, a pixel for each block; for
    /// blurring, a copy small enough to blur strongly in a moment, and the blur on it.
    /// </summary>
    static (int Width, int Height, double Sigma) Smaller(string style, int w, int h)
    {
        var block = Redaction.Block(new RedactBox(0, 0, w, h));
        if (style == "pixelate") return (Math.Max(1, (int)Math.Ceiling(w / (double)block)), Math.Max(1, (int)Math.Ceiling(h / (double)block)), 0);
        var sigma = block * 1.5;
        var shrink = Math.Max(1, sigma / 3);
        // Even, so the border added for the blur lines up with 4:2:0 colour.
        return (Math.Max(2, 2 * (int)Math.Round(w / shrink / 2)), Math.Max(2, 2 * (int)Math.Round(h / shrink / 2)), sigma / shrink);
    }

    /// <summary>What goes over a box of this size, as the photo redaction makes it.</summary>
    internal static string Cover(string style, Colour colour, int w, int h)
    {
        var (sw, sh, sigma) = Smaller(style, w, h);
        switch (style)
        {
            case "pixelate":
                return $"scale={sw}:{sh}:flags=area,scale={w}:{h}:flags=neighbor";
            case "blur":
                // gblur goes wrong on a picture smaller than its reach, leaving dark bands and the wrong colours, so the
                // copy is given a border of its edge pixels to blur into, and cut out of it again afterwards.
                var border = 2 * (int)Math.Ceiling(sigma * 3.3);
                return string.Create(CultureInfo.InvariantCulture,
                    $"scale={sw}:{sh}:flags=area,pad={sw + 2 * border}:{sh + 2 * border}:{border}:{border}," +
                    $"fillborders=left={border}:right={border}:top={border}:bottom={border}:mode=smear," +
                    $"gblur=sigma={sigma:0.###}:steps=3,gblur=sigma={sigma:0.###}:steps=3,crop={sw}:{sh}:{border}:{border},scale={w}:{h}:flags=bilinear");
            default:
                // Opaque even when the colour isn't: a see-through cover would show what's under it.
                return $"drawbox=x=0:y=0:w=iw:h=ih:color=0x{colour.R:X2}{colour.G:X2}{colour.B:X2}@1:t=fill";
        }
    }

    /// <summary>A value along a track as an expression of the time t, in straight lines between its places.</summary>
    static string Along(IReadOnlyList<BoxAt> track, Func<BoxAt, double> value)
    {
        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        if (track.Count == 1) return N(value(track[0]));
        var parts = new List<string> { $"lt(t,{N(track[0].At)})*{N(value(track[0]))}" };
        for (var i = 0; i + 1 < track.Count; i++)
        {
            var (a, b) = (track[i], track[i + 1]);
            var slope = b.At > a.At ? (value(b) - value(a)) / (b.At - a.At) : 0;
            parts.Add($"gte(t,{N(a.At)})*lt(t,{N(b.At)})*({N(value(a))}+{slope.ToString("0.######", CultureInfo.InvariantCulture)}*(t-{N(a.At)}))");
        }
        parts.Add($"gte(t,{N(track[^1].At)})*{N(value(track[^1]))}");
        return "(" + string.Join("+", parts) + ")";
    }

    /// <summary>
    /// The places a straight line between their neighbours wouldn't miss by more than two pixels are left out, so a
    /// track read at every frame becomes a handful of corners.
    /// </summary>
    internal static IReadOnlyList<BoxAt> Simplify(IReadOnlyList<BoxAt> track)
    {
        if (track.Count <= 2) return track;
        var keep = new bool[track.Count];
        keep[0] = keep[^1] = true;
        void Mark(int first, int last)
        {
            if (last - first < 2) return;
            var (a, b) = (track[first], track[last]);
            var (worst, at) = (0.0, -1);
            for (var i = first + 1; i < last; i++)
            {
                var f = b.At > a.At ? (track[i].At - a.At) / (b.At - a.At) : 0;
                var miss = new[]
                {
                    Math.Abs(a.X + (b.X - a.X) * f - track[i].X), Math.Abs(a.Y + (b.Y - a.Y) * f - track[i].Y),
                    Math.Abs(a.Width + (b.Width - a.Width) * f - track[i].Width), Math.Abs(a.Height + (b.Height - a.Height) * f - track[i].Height),
                }.Max();
                if (miss > worst) (worst, at) = (miss, i);
            }
            if (worst <= 2) return;
            keep[at] = true;
            Mark(first, at);
            Mark(at, last);
        }
        Mark(0, track.Count - 1);
        return track.Where((_, i) => keep[i]).ToList();
    }

    static int Even(int value) => value + value % 2;

    static string Seconds(double seconds) => Math.Max(0, seconds).ToString("0.######", CultureInfo.InvariantCulture);
}

/// <summary>
/// Follows a face through a stretch of video, on this PC, so a box can move with it: by finding the faces in each frame
/// looked at with YuNet, or on Windows 10, which can't run YuNet, with Windows' own face tracker.
/// </summary>
public static class FaceFollow
{
    /// <summary>Frames a second looked at: enough to keep up with a face, and quick.</summary>
    const int Rate = 15;

    /// <summary>How big YuNet is shown the face, in pixels across: well inside the 10 to 300 it finds, as the face moves nearer or away.</summary>
    const int FaceSize = 64;

    /// <summary>The widest picture Windows' tracker looks at; it finds faces well at this size.</summary>
    const int Widest = 640;

    /// <summary>How long a face can go missing, in seconds, before it counts as gone and the box stops there.</summary>
    const double Lost = 0.5;

    public static bool CanFollow => YuNet.IsAvailable || WindowsFollows;

    static bool WindowsFollows
    {
        get
        {
            try { return FaceTracker.IsSupported; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        }
    }

    /// <summary>The smallest square YuNet looks at around a face, in pixels of the frames it's given.</summary>
    const int Around = 256;

    /// <summary>The faces in one frame looked at, in the video's own pixels, given the frame's pixels and number and where the face was last.</summary>
    delegate Task<IEnumerable<RedactBox>> FacesIn(byte[] pixels, int number, RedactBox last);

    /// <summary>
    /// Where the face in <paramref name="start"/> (a box as <see cref="Redaction.FindFacesAsync"/> makes them, around a
    /// face) goes from <paramref name="from"/> to <paramref name="to"/> seconds: a place for every frame looked at,
    /// ending early where the face is lost.
    /// </summary>
    public static async Task<IReadOnlyList<BoxAt>> TrackAsync(Toolbox tools, string input, RedactBox start, double from, double to,
        IProgress<double> progress, CancellationToken ct)
    {
        if (!CanFollow) throw new ConversionException(Strings.WindowsCanTFollowFacesOnThis);
        var media = await Ffmpeg.ProbeAsync(tools, input, ct);
        if (YuNet.IsAvailable)
        {
            // Frames sized for the face: a small one in a crowd is looked at enlarged, a close-up smaller and quicker.
            var (width, height) = Size(media, Math.Clamp(media.Width * FaceSize / (double)Math.Max(1, start.Width), Math.Min(320, media.Width), Math.Min(1920, 2 * media.Width)));
            var (sx, sy) = ((float)media.Width / width, (float)media.Height / height);
            using var finder = new YuNet.Finder();
            return await FollowAsync(tools, input, start, from, to, width, height, "bgr24", width * height * 3, (pixels, _, last) =>
            {
                // Only around where the face was last, as it can't have gone far: a small square is quick to look at.
                var side = Math.Max(Around, 6 * Math.Max(last.Width / sx, last.Height / sy));
                var (cx, cy) = ((last.X + last.Width / 2.0) / sx, (last.Y + last.Height / 2.0) / sy);
                var area = new RedactBox((int)(cx - side / 2), (int)(cy - side / 2), (int)side, (int)side).Within(width, height) ?? new RedactBox(0, 0, width, height);
                return Task.FromResult(finder.Look(pixels, width, height, area).Select(f => f.Scaled(sx, sy).Box()));
            }, progress, ct);
        }

        var (w, h) = Size(media, Math.Min(Widest, media.Width));
        var (tx, ty) = ((double)media.Width / w, (double)media.Height / h);
        var tracker = await FaceTracker.CreateAsync();
        // The tracker takes grey pictures on some PCs and only NV12 on others; ffmpeg makes whichever it takes.
        var gray = FaceTracker.GetSupportedBitmapPixelFormats().Contains(BitmapPixelFormat.Gray8);
        var (format, ffmpegFormat, size) = gray ? (BitmapPixelFormat.Gray8, "gray", w * h) : (BitmapPixelFormat.Nv12, "nv12", w * h * 3 / 2);
        return await FollowAsync(tools, input, start, from, to, w, h, ffmpegFormat, size, async (pixels, n, _) =>
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), format, w, h);
            using var frame = Windows.Media.VideoFrame.CreateWithSoftwareBitmap(bitmap);
            // The tracker follows faces through time, so each frame says when it is.
            frame.RelativeTime = frame.SystemRelativeTime = TimeSpan.FromSeconds((double)n / Rate);
            frame.Duration = TimeSpan.FromSeconds(1.0 / Rate);
            try
            {
                var faces = await tracker.ProcessNextFrameAsync(frame);
                return faces.Select(f => new RedactBox((int)Math.Round(f.FaceBox.X * tx), (int)Math.Round(f.FaceBox.Y * ty),
                    (int)Math.Round(f.FaceBox.Width * tx), (int)Math.Round(f.FaceBox.Height * ty)).Grown(0.2, 0.4, 0.25)).ToList();
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                throw new ConversionException(string.Format(Strings.WindowsFaceTrackerCouldnTLookAt, ex.HResult), ex);
            }
        }, progress, ct);
    }

    /// <summary>The video's frames this wide, both sides even.</summary>
    static (int Width, int Height) Size(MediaInfo media, double width)
    {
        var scale = width / Math.Max(1, media.Width);
        return (Math.Max(2, (int)Math.Round(media.Width * scale / 2) * 2), Math.Max(2, (int)Math.Round(media.Height * scale / 2) * 2));
    }

    static async Task<IReadOnlyList<BoxAt>> FollowAsync(Toolbox tools, string input, RedactBox start, double from, double to,
        int width, int height, string format, int size, FacesIn facesIn, IProgress<double> progress, CancellationToken ct)
    {
        var track = new List<BoxAt> { new(from, start.X, start.Y, start.Width, start.Height) };
        var last = start;
        var lastSeen = from;
        var length = Math.Max(0.001, to - from);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Ffmpeg.PipeAsync(tools.Ffmpeg!, ["-hide_banner", "-nostdin", "-v", "error", "-ss", Seconds(from), "-i", input, "-t", Seconds(length),
                "-map", "0:V:0", "-vf", $"fps={Rate},scale={width}:{height},format={format}", "-f", "rawvideo", "-"], async stream =>
                {
                    var pixels = new byte[size];
                    for (var n = 0; await Ffmpeg.FillAsync(stream, pixels, stop.Token); n++)
                    {
                        var at = from + (double)n / Rate;
                        var faces = await facesIn(pixels, n, last);
                        // The face nearest where it was last, if it isn't too far and, once found, is about the size it was:
                        // a face can't cross the picture or double in a frame, so a neighbour's face isn't taken for it.
                        // (A box drawn by hand can be any size around the face.)
                        var found = track.Count > 1;
                        var nearest = faces
                            .Select(b => (Box: b, Off: Distance(b, last)))
                            .Where(c => c.Off < Math.Max(last.Width, last.Height) && (!found || (c.Box.Width < 2 * last.Width && 2 * c.Box.Width > last.Width)))
                            .OrderBy(c => c.Off)
                            .Select(c => (RedactBox?)c.Box)
                            .FirstOrDefault();
                        if (nearest is { } box)
                        {
                            track.Add(new BoxAt(at, box.X, box.Y, box.Width, box.Height));
                            (last, lastSeen) = (box, at);
                        }
                        else if (at - lastSeen > Lost)
                        {
                            stop.Cancel();
                            break;
                        }
                        progress.Report(Math.Clamp((at - from) / length, 0, 1));
                    }
                }, stop.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The face was lost: the track ends where it was last seen.
        }
        return track;

        static double Distance(RedactBox a, RedactBox b) =>
            Math.Sqrt(Math.Pow(a.X + a.Width / 2.0 - b.X - b.Width / 2.0, 2) + Math.Pow(a.Y + a.Height / 2.0 - b.Y - b.Height / 2.0, 2));
    }

    static string Seconds(double seconds) => Math.Max(0, seconds).ToString("0.######", CultureInfo.InvariantCulture);
}

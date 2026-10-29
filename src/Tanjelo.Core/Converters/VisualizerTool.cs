using System.Globalization;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Turns audio into an MP4 for sites that only take video: a moving waveform, or a still picture, in landscape,
/// portrait or square. The picture can be any file or the audio's own cover art.
/// </summary>
public sealed class VisualizerTool(Toolbox tools) : IConverter, IPreviewer
{
    static readonly Target Entry = new Target("visualize", Strings.MakeAVideo, Strings.WaveformOrPictureAsMP4, Section.Video)
    {
        Kind = TargetKind.Tool,
        Running = Strings.MakingAVideo,
        Done = Strings.VideoMade,
        Confirm = Strings.MakeVideo,
    }.WithOptions(
        new Option("style", Strings.Show, OptionKind.Choice, "wave") { Choices = [new("wave", Strings.Waveform), new("image", Strings.AStillPicture)] },
        new Option("shape", Strings.Shape, OptionKind.Choice, "landscape")
        {
            Choices = [new("landscape", Strings.Landscape169), new("portrait", Strings.Portrait916), new("square", Strings.Square)],
        },
        new Option("wave", Strings.WaveColour, OptionKind.Colour, "#F97A3D") { ShownWhen = new("style", "wave") },
        new Option("background", Strings.Background, OptionKind.Colour, "#1B1B1B"),
        new Option("image", Strings.Picture, OptionKind.File, "")
        {
            Optional = true,
            FileFilter = BackgroundTool.Pictures,
            Hint = Strings.EmptyUsesTheCoverArtIfThe,
        },
        new Option("range", Strings.Part2, OptionKind.TimeRange, "") { Remember = false, Hint = Strings.EmptyMakesAVideoOfTheWhole });

    public IEnumerable<Target> TargetsFor(string input) =>
        tools.Ffmpeg is not null && tools.Ffprobe is not null && Formats.Audio.Contains(Formats.Key(input)) ? [Entry] : [];

    /// <summary>The video's size for each shape, at full HD.</summary>
    internal static (int Width, int Height) Size(string shape) => shape switch
    {
        "portrait" => (1080, 1920),
        "square" => (1080, 1080),
        _ => (1920, 1080),
    };

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        var (start, length) = Part(media, target);
        using var temp = OutputPaths.CreateTemp();
        var picture = await PictureAsync(input, media, target, temp.Path, ctx.Cancellation);
        var (width, height) = Size(target.Choice("shape"));
        var output = OutputPaths.Suffixed(input, "video", "mp4");
        var wave = target.Choice("style") == "wave";
        string[] encode = ["-c:v", "libx264", "-preset", "medium", "-crf", "23", .. wave ? Array.Empty<string>() : ["-tune", "stillimage"],
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output];
        try
        {
            await Ffmpeg.RunRawAsync(tools, [.. Inputs(input, picture, start, length, wave), .. Graph(target, picture is not null, width, height, length), .. encode],
                length, ctx.Progress, ctx.Cancellation);
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        return [output];
    }

    public async Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx)
    {
        var input = inputs[0];
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        var (start, length) = Part(media, target);
        using var temp = OutputPaths.CreateTemp();
        var picture = await PictureAsync(input, media, target, temp.Path, ctx.Cancellation);
        var (width, height) = Size(target.Choice("shape"));
        // One frame from the middle of the part, made at preview size. The very first frame comes before the
        // waveform has any sound to draw, so it's taken a moment in.
        var scale = (double)request.MaxSize / Math.Max(width, height);
        var (w, h) = ((int)(width * scale) / 2 * 2, (int)(height * scale) / 2 * 2);
        var frame = Path.Combine(temp.Path, "frame.png");
        var wave = target.Choice("style") == "wave";
        var middle = start + length / 2;
        await Ffmpeg.RunRawAsync(tools, [.. Inputs(input, picture, Math.Max(start, middle - 0.5), 1, wave), .. Graph(target, picture is not null, w, h, 1),
            "-ss", "0.4", "-frames:v", "1", "-an", "-update", "1", frame], 0, new ActionProgress<double>(_ => { }), ctx.Cancellation);
        return new Preview(await File.ReadAllBytesAsync(frame, ctx.Cancellation),
        [
            new(Strings.Video, $"{Describe.Pixels(width, height)}, {Describe.Length(length)}"),
            new(Strings.Picture, picture is null ? Strings.None : target.File("image") is { } file ? Path.GetFileName(file) : Strings.TheCoverArt),
        ]);
    }

    /// <summary>Where the video starts in the audio, and how long it runs.</summary>
    static (double Start, double Length) Part(MediaInfo media, Target target)
    {
        if (media.Audio is null || media.Duration <= 0)
            throw new ConversionException(Strings.ThisFileHasNoSoundToShow);
        var (start, end) = target.TimeRange("range");
        if (start >= media.Duration)
            throw new ConversionException(string.Format(Strings.ThePartStartsAfterTheEndOf, Describe.Length(media.Duration)));
        return (start, Math.Min(end ?? media.Duration, media.Duration) - start);
    }

    /// <summary>The chosen picture, or the cover art saved to a file; null for a waveform on plain colour.</summary>
    async Task<string?> PictureAsync(string input, MediaInfo media, Target target, string temp, CancellationToken ct)
    {
        if (target.File("image") is { } file) return file;
        if (media.CoverArt)
        {
            var cover = Path.Combine(temp, "cover.png");
            await Ffmpeg.RunRawAsync(tools, ["-i", input, "-map", "0:v:0", "-frames:v", "1", "-update", "1", cover], 0, new ActionProgress<double>(_ => { }), ct);
            return cover;
        }
        if (target.Choice("style") == "image")
            throw new ConversionException(Strings.ChooseAPictureThisFileHasNo);
        return null;
    }

    static string[] Inputs(string input, string? picture, double start, double length, bool wave)
    {
        string[] audio = ["-ss", Ffmpeg.Time(start), "-t", Ffmpeg.Time(length), "-i", input];
        return picture is null ? audio : [.. audio, "-loop", "1", "-framerate", wave ? "30" : "10", "-i", picture];
    }

    /// <summary>
    /// A background of colour (or the picture), the picture or the waveform over it, and the sound. A picture
    /// fills the frame behind a waveform, darkened so the wave stands out; on its own it's shown whole.
    /// </summary>
    static string[] Graph(Target target, bool picture, int width, int height, double length)
    {
        var wave = target.Choice("style") == "wave";
        var rate = wave ? 30 : 10;
        var size = $"{width}x{height}";
        var graph = $"color=c={target.Colour("background").ToFfmpeg()}:s={size}:r={rate}:d={Ffmpeg.Time(length)}[bg];";
        var bottom = "[bg]";
        if (picture)
        {
            graph += wave
                ? $"[1:v]scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1,drawbox=c=black@0.45:t=fill[pic];"
                : $"[1:v]scale={width}:{height}:force_original_aspect_ratio=decrease,setsar=1[pic];";
            graph += "[bg][pic]overlay=(W-w)/2:(H-h)/2:shortest=1[base];";
            bottom = "[base]";
        }
        if (wave)
        {
            var band = Math.Max(2, (int)(Math.Min(width, height) * 0.4) / 2 * 2);
            graph += $"[0:a]showwaves=s={width}x{band}:mode=cline:draw=full:rate={rate}:scale=sqrt:colors={target.Colour("wave").ToFfmpeg()}[wave];";
            graph += $"{bottom}[wave]overlay=0:(H-h)/2:shortest=1,format=yuv420p[v]";
        }
        else
        {
            graph += $"{bottom}format=yuv420p[v]";
        }
        return ["-filter_complex", graph, "-map", "[v]", "-map", "0:a:0", "-r", rate.ToString(CultureInfo.InvariantCulture)];
    }
}

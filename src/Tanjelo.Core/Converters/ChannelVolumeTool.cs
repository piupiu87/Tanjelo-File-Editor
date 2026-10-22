using System.Collections.Concurrent;
using System.Globalization;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Makes the left and right of a recording louder or quieter separately: a lapel mic that only reached one side,
/// say. In surround sound every left-side channel follows the left setting and every right-side one the right;
/// centre and bass channels stay as they are. In a video only the sound is re-encoded.
/// </summary>
public sealed class ChannelVolumeTool(Toolbox tools) : IConverter, IPreviewer
{
    static Target Entry(Section section) => new Target("channel-volume", "Channel volume…", "Louder or quieter on each side", section)
    {
        Kind = TargetKind.Tool,
        Running = "Changing the volume…",
        Done = "Volume changed",
        Confirm = "Change volume",
    }.WithOptions(
        new Option("left", "Left", OptionKind.Number, "100") { Min = 0, Max = 400, Whole = true, Unit = "%", Slider = true },
        new Option("right", "Right", OptionKind.Number, "100")
        {
            Min = 0, Max = 400, Whole = true, Unit = "%", Slider = true,
            Hint = "0% silences a side and 200% makes it twice as loud (+6 dB). In surround sound, every channel on that side.",
        });

    static readonly Target AudioEntry = Entry(Section.Audio);
    static readonly Target VideoEntry = Entry(Section.Video);

    public IEnumerable<Target> TargetsFor(string input)
    {
        if (tools.Ffmpeg is null || tools.Ffprobe is null) return [];
        var key = Formats.Key(input);
        if (Formats.Audio.Contains(key)) return [AudioEntry];
        if (Formats.Videos.Contains(key)) return [VideoEntry];
        return [];
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        var (left, right) = (target.Number("left") / 100, target.Number("right") / 100);
        if (left == 1 && right == 1)
            throw new ConversionException("Both sides are at 100%, so nothing would change.");
        var (pan, _, _) = await PanAsync(media, left, right, ctx.Cancellation);

        var key = Formats.Key(input);
        string[] args;
        string output;
        if (Formats.Videos.Contains(key))
        {
            // The picture is copied; the sound is re-encoded the way the container usually holds it.
            var container = Ffmpeg.CanEncodeVideo(key) ? key : "mp4";
            output = OutputPaths.Suffixed(input, "balanced", container);
            string[] sound = container switch
            {
                "webm" => ["-c:a", "libopus", "-b:a", "160k"],
                "avi" => ["-c:a", "libmp3lame", "-q:a", "2"],
                "wmv" => ["-c:a", "wmav2", "-b:a", "192k"],
                _ => ["-c:a", "aac", "-b:a", "192k"],
            };
            args = ["-map", "0:v?", "-map", "0:a:0", "-map", "0:s?", "-c", "copy", "-af", pan, .. sound, .. MediaTools.FastStart(output)];
        }
        else
        {
            output = OutputPaths.Suffixed(input, "balanced");
            args = ["-map", "0:a:0", "-af", pan, .. Ffmpeg.AudioEncoder(key)];
        }
        await Ffmpeg.RunFirstWorkingAsync(tools, input, output, [args], media.Duration, ctx);
        return [output];
    }

    /// <summary>A picture of each channel's sound after the change, top to bottom, with the change in decibels.</summary>
    public async Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx)
    {
        var media = await Ffmpeg.ProbeAsync(tools, inputs[0], ctx.Cancellation);
        var (left, right) = (target.Number("left") / 100, target.Number("right") / 100);
        var (pan, layout, sides) = await PanAsync(media, left, right, ctx.Cancellation);

        using var temp = OutputPaths.CreateTemp();
        var picture = Path.Combine(temp.Path, "waves.png");
        var size = $"{request.MaxSize}x{request.MaxSize * 3 / 4}";
        // Each channel in its side's colour, on a dark panel. Two minutes is enough to see the balance, and keeps a
        // long film from taking a while.
        var colours = string.Join('|', sides.Select(side => side switch { < 0 => Left, > 0 => Right, _ => Middle }));
        await Ffmpeg.RunRawAsync(tools, ["-t", "120", "-i", inputs[0], "-filter_complex",
            $"[0:a:0]{pan},showwavespic=s={size}:split_channels=1:draw=full:colors={colours}[w];color=c=0x1B1B1B:s={size}[bg];[bg][w]overlay=shortest=1[v]",
            "-map", "[v]", "-frames:v", "1", "-update", "1", picture], 0, new ActionProgress<double>(_ => { }), ctx.Cancellation);

        return new Preview(await File.ReadAllBytesAsync(picture, ctx.Cancellation),
        [
            new("Sound", $"{layout}, {Describe.Length(media.Duration)}"),
            new("Left", Change(left)),
            new("Right", Change(right)),
            new("Colours", sides.Any(s => s == 0) ? "Orange is left, blue right, grey the middle" : "Orange is left, blue right"),
        ]);
    }

    const string Left = "0xF97A3D", Right = "0x3A7BD5", Middle = "0x9A9A9A";

    static string Change(double gain) => gain switch
    {
        0 => "silent",
        1 => "as it is",
        _ => string.Create(CultureInfo.InvariantCulture, $"{gain * 100:0}%, {20 * Math.Log10(gain):+0.0;-0.0} dB"),
    };

    /// <summary>The pan filter that scales each channel by the gain for its side, the layout it works on, and each channel's side.</summary>
    async Task<(string Pan, string Layout, IReadOnlyList<int> Sides)> PanAsync(MediaInfo media, double left, double right, CancellationToken ct)
    {
        if (media.Audio is null)
            throw new ConversionException("This file has no sound.");
        if (media.Channels < 2)
            throw new ConversionException("This file's sound has one channel, so it has no sides. Make it stereo first.");
        var layouts = await LayoutsAsync(ct);
        var name = media.ChannelLayout is { Length: > 0 } layout && layouts.Channels.ContainsKey(layout) ? layout : media.Channels == 2 ? "stereo" : null;
        if (name is null)
            throw new ConversionException($"Tanjelo can't tell which side each of this file's {media.Channels} channels is on.");
        var sides = layouts.Channels[name].Select(layouts.Side).ToList();
        var gains = Gains(sides, left, right);
        return ($"pan={name}|" + string.Join('|', gains.Select((g, i) => string.Create(CultureInfo.InvariantCulture, $"c{i}={g:0.####}*c{i}"))), name, sides);
    }

    /// <param name="sides">For each channel: -1 on the left, 1 on the right, 0 in the middle.</param>
    internal static double[] Gains(IReadOnlyList<int> sides, double left, double right) =>
        sides.Select(side => side switch { < 0 => left, > 0 => right, _ => 1.0 }).ToArray();

    static readonly ConcurrentDictionary<string, Task<ChannelLayouts>> Known = new();

    Task<ChannelLayouts> LayoutsAsync(CancellationToken ct) => Known.GetOrAdd(tools.Ffmpeg!, async ffmpeg =>
    {
        var result = await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-layouts"], ct);
        return ChannelLayouts.Parse(result.StdOut);
    });
}

/// <summary>ffmpeg's channel layouts and channels, from <c>ffmpeg -layouts</c>: "5.1(side)" is FL+FR+FC+LFE+SL+SR, "SL" is "side left".</summary>
sealed record ChannelLayouts(IReadOnlyDictionary<string, string[]> Channels, IReadOnlyDictionary<string, string> Descriptions)
{
    public static ChannelLayouts Parse(string text)
    {
        var channels = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = 0;
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line.StartsWith("Individual channels", StringComparison.Ordinal)) section = 1;
            else if (line.StartsWith("Standard channel layouts", StringComparison.Ordinal)) section = 2;
            else if (line.StartsWith("NAME", StringComparison.Ordinal) || line.Trim().Length == 0) continue;
            else if (line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) is [var name, var rest])
            {
                if (section == 1) descriptions[name] = rest.Trim();
                else if (section == 2) channels[name] = rest.Trim().Split('+');
            }
        }
        return new ChannelLayouts(channels, descriptions);
    }

    /// <summary>-1 for a channel on the left, 1 on the right, 0 in the middle or unknown.</summary>
    public int Side(string channel) => Descriptions.GetValueOrDefault(channel, "") switch
    {
        var d when d.Contains("left", StringComparison.OrdinalIgnoreCase) => -1,
        var d when d.Contains("right", StringComparison.OrdinalIgnoreCase) => 1,
        _ => 0,
    };
}

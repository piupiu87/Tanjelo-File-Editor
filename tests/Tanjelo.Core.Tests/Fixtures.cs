using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using ImageMagick;

namespace Tanjelo.Core.Tests;

/// <summary>The tools found on this machine, detected once for the whole run.</summary>
static class Env
{
    public static Toolbox Tools { get; } = Toolbox.Detect();

    public static bool HasFfmpeg => Tools.Ffmpeg is not null && Tools.Ffprobe is not null;

    public const string NoFfmpeg = "ffmpeg isn't installed";
    public const string NoWord = "Microsoft Word isn't installed";
    public const string NoExcel = "Microsoft Excel isn't installed";
    public const string NoPowerPoint = "Microsoft PowerPoint isn't installed";

    public static ConverterEngine Engine(ConverterOptions? options = null) => new(Tools, options);

    /// <summary>Runs one menu entry the way the app does and fails the test on any per-file failure.</summary>
    public static async Task<BatchResult> Run(string key, params string[] inputs) => await Run(Engine(), key, inputs);

    public static async Task<BatchResult> Run(ConverterEngine engine, string key, params string[] inputs)
    {
        var target = Offered(engine, key, inputs);
        var result = await engine.RunAsync(inputs, target, ct: TestContext.Current.CancellationToken);
        Assert.True(result.Failures.Count == 0, string.Join(Environment.NewLine, result.Failures.Select(f => $"{f.Input}: {f.Message}")));
        Assert.False(result.Canceled);
        return result;
    }

    /// <summary>Like <see cref="Run(string, string[])"/>, but expects exactly one output and returns it.</summary>
    public static async Task<string> RunOne(string key, params string[] inputs)
    {
        var result = await Run(key, inputs);
        var output = Assert.Single(result.Outputs);
        Assert.True(File.Exists(output) || Directory.Exists(output), $"{output} wasn't created");
        return output;
    }

    public static Target Offered(ConverterEngine engine, string key, IReadOnlyList<string> inputs)
    {
        var menu = engine.GetMenu(inputs);
        var target = menu.Targets.FirstOrDefault(t => t.Key == key);
        Assert.True(target is not null, $"'{key}' isn't offered; the menu has: {string.Join(", ", menu.Targets.Select(t => t.Key))}");
        return target;
    }

    public static IReadOnlyList<string> Keys(params string[] inputs) => Engine().GetMenu(inputs).Targets.Select(t => t.Key).ToList();

    public static IReadOnlyList<string> Keys(Section section, params string[] inputs) =>
        Engine().GetMenu(inputs).Targets.Where(t => t.Section == section).Select(t => t.Key).ToList();
}

/// <summary>A scratch folder per test, with generators for the files the converters read.</summary>
sealed class Scratch : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "tanjelo-tests", Guid.NewGuid().ToString("N")[..10]);

    public Scratch() => Directory.CreateDirectory(Dir);

    public string PathOf(string name) => Path.Combine(Dir, name);

    public string[] Files() => Directory.GetFileSystemEntries(Dir).Select(Path.GetFileName).Order().ToArray()!;

    /// <summary>A solid-colour image, saved by System.Drawing in the format the extension names.</summary>
    public string Image(string name, int width = 64, int height = 48, Color? color = null)
    {
        var path = PathOf(name);
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
            g.Clear(color ?? Color.Orange);
        bitmap.Save(path, Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            ".bmp" => ImageFormat.Bmp,
            ".gif" => ImageFormat.Gif,
            ".tif" or ".tiff" => ImageFormat.Tiff,
            _ => ImageFormat.Png,
        });
        return path;
    }

    /// <summary>A noisy, photo-like picture that doesn't compress to nothing, saved at high quality in the format the extension names.</summary>
    public string Photo(string name, int width = 640, int height = 480)
    {
        var path = PathOf(name);
        using var image = new MagickImage("plasma:orange-steelblue", (uint)width, (uint)height);
        image.AddNoise(NoiseType.Gaussian);
        image.Quality = 95;
        image.Write(path);
        return path;
    }

    /// <summary>A JPEG stored <paramref name="width"/> by <paramref name="height"/> with an EXIF note to turn it a quarter, as phones save portraits.</summary>
    public string Sideways(string name, int width = 120, int height = 80)
    {
        var path = PathOf(name);
        using var image = new MagickImage("plasma:", (uint)width, (uint)height);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        image.SetProfile(exif);
        image.Orientation = Orientation.RightTop; // ImageMagick writes this into the EXIF
        image.Write(path);
        return path;
    }

    /// <summary>A PNG that is transparent except for an opaque square in the middle.</summary>
    public string TransparentPng(string name, int size = 40)
    {
        var path = PathOf(name);
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.FillRectangle(Brushes.Navy, size / 4, size / 4, size / 2, size / 2);
        }
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    public string AnimatedGif(string name, int frames = 3)
    {
        var path = PathOf(name);
        using var collection = new MagickImageCollection();
        for (var i = 0; i < frames; i++)
        {
            var frame = new MagickImage(new MagickColor((byte)(i * 80), 120, 200), 32, 32) { AnimationDelay = 10 };
            collection.Add(frame);
        }
        collection.Write(path, MagickFormat.Gif);
        return path;
    }

    /// <summary>A PDF of blank pages, US Letter unless <paramref name="width"/> (in points) says otherwise.</summary>
    public string Pdf(string name, int pages = 1, int width = 612)
    {
        var path = PathOf(name);
        using var collection = new MagickImageCollection();
        for (var i = 0; i < pages; i++)
        {
            var page = new MagickImage(MagickColors.White, (uint)width, 792) { Density = new Density(72, 72, DensityUnit.PixelsPerInch) };
            collection.Add(page);
        }
        collection.Write(path, MagickFormat.Pdf);
        return path;
    }

    public string Text(string name, string content)
    {
        var path = PathOf(name);
        File.WriteAllText(path, content);
        return path;
    }

    public string Folder(string name, params string[] files)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(path);
        foreach (var file in files)
            File.WriteAllText(Path.Combine(path, file), "contents of " + file);
        return path;
    }

    /// <summary>A test-pattern video with a sine tone (H.264 + AAC by default), made by ffmpeg's lavfi sources.</summary>
    public string Video(string name, double seconds = 2, bool audio = true, string size = "160x120", params string[] codec)
    {
        var d = seconds.ToString(CultureInfo.InvariantCulture);
        string[] inputs = ["-f", "lavfi", "-i", $"testsrc2=size={size}:rate=25:duration={d}"];
        if (audio) inputs = [.. inputs, "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={d}"];
        string[] encode = codec.Length > 0 ? codec : ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac"];
        return Ffmpeg(name, [.. inputs, .. encode, "-shortest"]);
    }

    /// <summary>A sine tone; <paramref name="filter"/> can shape it, e.g. pad it with silence.</summary>
    public string Tone(string name, double seconds = 2, int channels = 1, string? filter = null)
    {
        var d = seconds.ToString(CultureInfo.InvariantCulture);
        string[] args = ["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={d}", "-ac", channels.ToString()];
        if (filter is not null) args = [.. args, "-af", filter];
        return Ffmpeg(name, args);
    }

    public string Ffmpeg(string name, string[] args)
    {
        var path = PathOf(name);
        var result = ProcessRunner.RunAsync(Env.Tools.Ffmpeg!, ["-hide_banner", "-nostdin", "-v", "error", "-y", .. args, path], CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, $"ffmpeg couldn't make {name}: {result.LastError}");
        return path;
    }

    public void Dispose() => OutputPaths.TryDelete(Dir);
}

/// <summary>Reads back what the converters wrote.</summary>
static class Inspect
{
    public static int PdfPages(string path)
    {
        var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
        return (int)Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult().PageCount;
    }

    public static MagickImageInfo Image(string path) => new(path);

    /// <summary>Codec names by stream type, e.g. ("video", "h264"), plus the duration in seconds.</summary>
    public static (List<(string Type, string Codec)> Streams, double Duration, int Channels) Media(string path)
    {
        var result = ProcessRunner.RunAsync(Env.Tools.Ffprobe!,
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name,channels:format=duration", "-of", "json", path], CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, $"ffprobe can't read {path}: {result.LastError}");
        using var json = System.Text.Json.JsonDocument.Parse(result.StdOut);
        var streams = new List<(string, string)>();
        var channels = 0;
        foreach (var stream in json.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = stream.GetProperty("codec_type").GetString()!;
            streams.Add((type, stream.GetProperty("codec_name").GetString()!));
            if (type == "audio" && channels == 0 && stream.TryGetProperty("channels", out var c)) channels = c.GetInt32();
        }
        var duration = double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return (streams, duration, channels);
    }

    public static string? Codec(string path, string type) => Media(path).Streams.FirstOrDefault(s => s.Type == type).Codec;

    /// <summary>A tag on the file itself, such as "title", as ffprobe reports it; null when it isn't there.</summary>
    public static string? Tag(string path, string name)
    {
        var result = ProcessRunner.RunAsync(Env.Tools.Ffprobe!, ["-v", "error", "-show_entries", "format_tags", "-of", "json", path], CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, $"ffprobe can't read {path}: {result.LastError}");
        using var json = System.Text.Json.JsonDocument.Parse(result.StdOut);
        if (!json.RootElement.TryGetProperty("format", out var format) || !format.TryGetProperty("tags", out var tags)) return null;
        return tags.EnumerateObject().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).Value.ToString() is { Length: > 0 } value ? value : null;
    }

    /// <summary>Each channel's RMS level in dB, as ffmpeg's astats measures it; negative infinity for silence.</summary>
    public static List<double> ChannelLevels(string path)
    {
        var result = ProcessRunner.RunAsync(Env.Tools.Ffmpeg!,
            ["-hide_banner", "-nostdin", "-nostats", "-v", "info", "-i", path, "-map", "0:a:0", "-af", "astats=metadata=0:measure_overall=none", "-f", "null", "-"],
            CancellationToken.None).GetAwaiter().GetResult();
        return System.Text.RegularExpressions.Regex.Matches(result.StdErr, @"RMS level dB: (-?[\d.]+|-inf)")
            .Select(m => m.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();
    }

    /// <summary>Integrated loudness in LUFS, as ffmpeg's loudnorm measures it.</summary>
    public static double Loudness(string path)
    {
        var result = ProcessRunner.RunAsync(Env.Tools.Ffmpeg!,
            ["-hide_banner", "-nostdin", "-nostats", "-v", "info", "-i", path, "-af", "loudnorm=print_format=summary", "-f", "null", "-"], CancellationToken.None).GetAwaiter().GetResult();
        var match = System.Text.RegularExpressions.Regex.Match(result.StdErr, @"Input Integrated:\s+(-?[\d.]+) LUFS");
        Assert.True(match.Success, $"no loudness reported for {path}");
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}

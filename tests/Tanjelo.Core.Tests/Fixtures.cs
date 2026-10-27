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

/// <summary>Lets PDFsharp draw text with the fonts installed in Windows. It can only be set once, before any font is used.</summary>
static class PdfFonts
{
    static PdfFonts() => PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;

    public static void Ready() { }
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

    /// <summary>An A4 PDF with real text, each page drawn by one of <paramref name="pages"/> with PDFsharp in Windows' own fonts.</summary>
    public string TextPdf(string name, params Action<PdfSharp.Drawing.XGraphics>[] pages) => TextPdf(name, null, pages);

    /// <param name="password">Encrypts the PDF so it can only be opened with this password.</param>
    public string TextPdf(string name, string? password, params Action<PdfSharp.Drawing.XGraphics>[] pages)
    {
        PdfFonts.Ready();
        var path = PathOf(name);
        using var document = new PdfSharp.Pdf.PdfDocument();
        foreach (var draw in pages)
        {
            var page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            using var g = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            draw(g);
        }
        if (password is not null) document.SecuritySettings.UserPassword = password;
        document.Save(path);
        return path;
    }

    /// <summary>A PDF of one page that is only a picture of text, like a scan, with no text layer.</summary>
    public string ScannedPdf(string name, params string[] lines)
    {
        var path = PathOf(name);
        using var image = new MagickImage(TextPicture(lines, 1700, 2200)) { Density = new Density(200, 200, DensityUnit.PixelsPerInch) }; // a page at 200 dpi
        image.Write(path, MagickFormat.Pdf);
        return path;
    }

    /// <summary>A picture of text, such as a screenshot, saved in the format the extension names.</summary>
    public string TextPicture(string name, int width, int height, params string[] lines)
    {
        var path = PathOf(name);
        using var image = new MagickImage(TextPicture(lines, width, height));
        image.Write(path);
        return path;
    }

    static byte[] TextPicture(string[] lines, int width, int height)
    {
        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 42, GraphicsUnit.Pixel))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            for (var i = 0; i < lines.Length; i++)
                g.DrawString(lines[i], font, Brushes.Black, width / 10f, height / 11f + i * 64);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
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

    /// <summary>
    /// A video whose every frame shows its own number, ffmpeg's n, in large digits over a moving test pattern, so any
    /// picture taken from it can be told apart from its neighbours (see <see cref="Inspect.WhichFrame"/>).
    /// </summary>
    /// <param name="rate">Frames a second, such as "25" or "30000/1001".</param>
    /// <param name="encode">Encoder settings; H.264 with a keyframe every second and AAC by default.</param>
    public string Numbered(string name, double seconds = 4, string rate = "25", string size = "320x240", bool audio = true, params string[] encode)
    {
        var d = seconds.ToString(CultureInfo.InvariantCulture);
        string[] inputs = ["-f", "lavfi", "-i", $"testsrc2=size={size}:rate={rate}:duration={d}," +
            "drawtext=fontfile='C\\:/Windows/Fonts/arial.ttf':text='%{n}':fontsize=h/3:fontcolor=white:box=1:boxcolor=black:boxborderw=6:x=8:y=8"];
        // The sound beeps for a tenth of a second at every whole second, so its place against the frames can be measured.
        if (audio) inputs = [.. inputs, "-f", "lavfi", "-i", $"aevalsrc='if(lt(mod(t,1),0.1),0.5*sin(2*PI*1000*t),0)':s=48000:d={d}"];
        string[] codec = encode.Length > 0 ? encode : ["-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "25", "-c:a", "aac"];
        return Ffmpeg(name, [.. inputs, .. codec, "-shortest"]);
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

    /// <summary>Frame <paramref name="frame"/> of a video as ffmpeg numbers them, as BGRA pixels at a size of our choosing.</summary>
    public static byte[] Frame(string path, int frame, int width, int height) =>
        TryFrame(path, frame, width, height) ?? throw new Xunit.Sdk.XunitException($"{path} has no frame {frame}");

    static byte[]? TryFrame(string path, int frame, int width, int height)
    {
        using var temp = new Scratch();
        var raw = temp.PathOf("frame.bgra");
        temp.Ffmpeg("frame.bgra", ["-i", path, "-map", "0:V:0", "-vf", $"select=eq(n\\,{frame}),scale={width}:{height}", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra"]);
        var pixels = File.ReadAllBytes(raw);
        return pixels.Length == width * height * 4 ? pixels : null;
    }

    /// <summary>
    /// Which of the <paramref name="candidates"/> frames of a numbered video (<see cref="Scratch.Numbered"/>) a picture
    /// of it shows: the one ffmpeg pulls out by number that differs from it least. Colour channels only, as BGRA.
    /// Candidates past the end are left out.
    /// </summary>
    public static int WhichFrame(byte[] pixels, int width, int height, string video, IEnumerable<int> candidates) =>
        candidates.Select(n => (n, Frame: TryFrame(video, n, width, height))).Where(c => c.Frame is not null)
            .MinBy(c => Difference(pixels, c.Frame!)).n;

    /// <summary>The mean difference of the colour channels of two BGRA pictures of one size, 0 to 255.</summary>
    public static double Difference(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
            if (i % 4 != 3) sum += Math.Abs(a[i] - b[i]);
        return sum / (a.Length * 0.75);
    }

    /// <summary>When each burst of sound after a silence starts, such as the beeps of a numbered video, in seconds.</summary>
    public static List<double> Beeps(string path)
    {
        var result = ProcessRunner.RunAsync(Env.Tools.Ffmpeg!,
            ["-hide_banner", "-nostdin", "-nostats", "-v", "info", "-i", path, "-map", "0:a:0", "-af", "silencedetect=noise=-30dB:d=0.05", "-f", "null", "-"],
            CancellationToken.None).GetAwaiter().GetResult();
        return System.Text.RegularExpressions.Regex.Matches(result.StdErr, @"silence_end: (-?[\d.]+)")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
    }

    /// <summary>When the picture starts, after any sound before it, as players show it.</summary>
    public static double PictureStart(string path) => double.Parse(Probe(path, "-select_streams", "V:0", "-show_entries", "stream=start_time"), CultureInfo.InvariantCulture);

    /// <summary>The sizes of a video's picture packets in the order they're stored, from the one at <paramref name="from"/> seconds.</summary>
    public static List<int> PacketSizes(string path, double from = 0, int count = 12)
    {
        var lines = Probe(path, "-select_streams", "V:0", "-show_entries", "packet=pts_time,size").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(',')).Select(f => (Time: double.Parse(f[0], CultureInfo.InvariantCulture), Size: int.Parse(f[1], CultureInfo.InvariantCulture))).ToList();
        var at = lines.FindIndex(p => Math.Abs(p.Time - from) < 0.002);
        Assert.True(at >= 0, $"{path} has no packet at {from}");
        return lines.Skip(at).Take(count).Select(p => p.Size).ToList();
    }

    /// <summary>The picture's size as players show it, after any turn the file asks for.</summary>
    public static (int Width, int Height) Shown(string path)
    {
        var fields = Probe(path, "-select_streams", "V:0", "-show_entries", "stream=width,height:stream_side_data=rotation").Split(',', '\n');
        var (width, height) = (int.Parse(fields[0], CultureInfo.InvariantCulture), int.Parse(fields[1], CultureInfo.InvariantCulture));
        var turned = fields.Length > 2 && int.TryParse(fields[2].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var rotation) && Math.Abs(rotation) % 180 == 90;
        return turned ? (height, width) : (width, height);
    }

    static string Probe(string path, params string[] args)
    {
        var result = ProcessRunner.RunAsync(Env.Tools.Ffprobe!, ["-v", "error", .. args, "-of", "csv=p=0", path], CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, $"ffprobe can't read {path}: {result.LastError}");
        return result.StdOut.Trim();
    }

    /// <summary>How many frames a video's picture has, counted from its packets.</summary>
    public static int FrameCount(string path)
    {
        var result = ProcessRunner.RunAsync(Env.Tools.Ffprobe!, ["-v", "error", "-select_streams", "V:0", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", path],
            CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, $"ffprobe can't read {path}: {result.LastError}");
        return int.Parse(result.StdOut.Trim().TrimEnd(','), CultureInfo.InvariantCulture);
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

using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

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
        using var document = new PdfDocument();
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

    // ---- PDFs for the PDF tools. Their content is written as PDF operators, so where everything sits in the page's
    // own coordinates is known exactly, whatever the page's rotation or boxes.

    /// <summary>A Letter-sized page per rotation, each with "Rotated N" at 72, 700 in the page's own coordinates, turned by /Rotate.</summary>
    public string RotatedPdf(string name, params int[] rotations)
    {
        var path = PathOf(name);
        using var document = new PdfDocument();
        foreach (var rotation in rotations)
        {
            var page = RawPdf.Page(document, 0, 0, 612, 792);
            RawPdf.Text(document, page, $"Rotated {rotation}", 72, 700);
            if (rotation != 0) page.Elements.SetInteger("/Rotate", rotation);
        }
        document.Save(path);
        return path;
    }

    /// <summary>
    /// Pages whose visible part isn't at the origin: a crop box of 100, 150 to 500, 700 on a Letter page; a page whose
    /// media box starts at 200, 300; and a page with /UserUnit 2, half as many units for a Letter page. Each has
    /// "Visible N" 20 units in from the visible box's top-left corner.
    /// </summary>
    public string BoxesPdf(string name)
    {
        var path = PathOf(name);
        using var document = new PdfDocument();
        var cropped = RawPdf.Page(document, 0, 0, 612, 792);
        cropped.Elements["/CropBox"] = RawPdf.Rect(100, 150, 500, 700);
        RawPdf.Text(document, cropped, "Visible 1", 120, 680);
        RawPdf.Text(document, cropped, "Hidden margin", 20, 20);
        var moved = RawPdf.Page(document, 200, 300, 812, 1092);
        RawPdf.Text(document, moved, "Visible 2", 220, 1072);
        var scaled = RawPdf.Page(document, 0, 0, 306, 396);
        scaled.Elements["/UserUnit"] = new PdfReal(2);
        RawPdf.Text(document, scaled, "Visible 3", 20, 376, size: 6);
        document.Save(path);
        return path;
    }

    /// <summary>
    /// A Letter page whose content leaves the graphics state changed: page 1 opens a q it never closes and halves
    /// everything after it; page 2 closes one more Q than it opens and then doubles everything.
    /// </summary>
    public string ChangedStatePdf(string name)
    {
        var path = PathOf(name);
        using var document = new PdfDocument();
        var first = RawPdf.Page(document, 0, 0, 612, 792);
        RawPdf.Content(first, "q 0.5 0 0 0.5 0 0 cm 1 0 0 rg 0 0 300 300 re f");
        RawPdf.Text(document, first, "Left halved", 72, 700);
        var second = RawPdf.Page(document, 0, 0, 612, 792);
        RawPdf.Text(document, second, "Left doubled", 72, 300);
        RawPdf.Content(second, "Q 2 0 0 2 0 0 cm 0 0 1 rg 0 0 50 50 re f");
        document.Save(path);
        return path;
    }

    /// <summary>
    /// A form, filled in, with comments: a text field "name" (value "Filled in"), a ticked check box "agree", a sticky
    /// note with its popup, a red rectangle comment, a hidden comment and a link to a web page.
    /// </summary>
    /// <param name="needAppearances">Leaves the text field without an appearance and sets /NeedAppearances, as some form makers do.</param>
    /// <param name="xfa">Adds an XFA form beside the ordinary one, as LiveCycle Designer makes them.</param>
    public string FormPdf(string name, bool needAppearances = false, bool xfa = false)
    {
        var path = PathOf(name);
        using var document = new PdfDocument();
        var page = RawPdf.Page(document, 0, 0, 612, 792);
        RawPdf.Text(document, page, "Application form", 72, 720, size: 18);
        var helvetica = RawPdf.Helvetica(document);
        var fonts = RawPdf.Dict(document, ("/Helv", helvetica.Reference!));

        var text = RawPdf.Annotation(document, page, "/Widget", 100, 650, 300, 670);
        text.Elements["/FT"] = new PdfName("/Tx");
        text.Elements["/T"] = new PdfString("name");
        text.Elements["/V"] = new PdfString("Filled in");
        text.Elements["/DA"] = new PdfString("/Helv 12 Tf 0 g");
        if (!needAppearances)
            RawPdf.Appearance(document, text, 200, 20, "/Tx BMC q BT /Helv 12 Tf 0 g 2 6 Td (Filled in) Tj ET Q EMC", RawPdf.Dict(document, ("/Font", fonts)));

        var check = RawPdf.Annotation(document, page, "/Widget", 100, 600, 116, 616);
        check.Elements["/FT"] = new PdfName("/Btn");
        check.Elements["/T"] = new PdfString("agree");
        check.Elements["/V"] = new PdfName("/Yes");
        check.Elements["/AS"] = new PdfName("/Yes");
        var on = RawPdf.Form(document, 16, 16, "0 g 1 w 0.5 0.5 15 15 re S 3 3 10 10 re f");
        var off = RawPdf.Form(document, 16, 16, "0 g 1 w 0.5 0.5 15 15 re S");
        check.Elements["/AP"] = RawPdf.Dict(document, ("/N", RawPdf.Dict(document, ("/Yes", on.Reference!), ("/Off", off.Reference!))));

        var note = RawPdf.Annotation(document, page, "/Text", 400, 700, 420, 720);
        note.Elements["/Contents"] = new PdfString("Looks good");
        RawPdf.Appearance(document, note, 20, 20, "1 0.85 0 rg 0 0 20 20 re f");
        var popup = RawPdf.Annotation(document, page, "/Popup", 420, 600, 600, 700);
        popup.Elements["/Parent"] = note.Reference;
        note.Elements["/Popup"] = popup.Reference;

        var square = RawPdf.Annotation(document, page, "/Square", 300, 500, 400, 560);
        square.Elements["/C"] = RawPdf.Array(1, 0, 0);
        RawPdf.Appearance(document, square, 100, 60, "1 0 0 RG 4 w 2 2 96 56 re S");

        var hidden = RawPdf.Annotation(document, page, "/Square", 300, 400, 400, 460);
        hidden.Elements.SetInteger("/F", 2); // hidden
        RawPdf.Appearance(document, hidden, 100, 60, "0 0 1 rg 0 0 100 60 re f");

        var link = RawPdf.Annotation(document, page, "/Link", 100, 300, 250, 320);
        link.Elements["/A"] = RawPdf.Dict(document, ("/S", new PdfName("/URI")), ("/URI", new PdfString("https://example.com/")));
        link.Elements["/Border"] = RawPdf.Array(0, 0, 0);

        var form = RawPdf.Dict(document,
            ("/Fields", new PdfArray(document, text.Reference!, check.Reference!)),
            ("/DA", new PdfString("/Helv 0 Tf 0 g")),
            ("/DR", RawPdf.Dict(document, ("/Font", fonts))));
        if (needAppearances) form.Elements["/NeedAppearances"] = new PdfBoolean(true);
        if (xfa)
        {
            var packet = RawPdf.Stream(document, "<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\"><template/></xdp:xdp>");
            form.Elements["/XFA"] = packet.Reference;
        }
        document.Internals.Catalog.Elements["/AcroForm"] = form;
        document.Save(path);
        return path;
    }

    /// <param name="encryption">"aes256", "aes128" or "rc4".</param>
    /// <param name="open">The password to open it, if any.</param>
    /// <param name="permissions">The permissions password, which also lifts the limits.</param>
    /// <param name="limits">Forbids printing, copying and changes.</param>
    public string EncryptedPdf(string name, string encryption, string? open, string? permissions = null, bool limits = false)
    {
        var path = PathOf(name);
        using var document = new PdfDocument();
        var page = RawPdf.Page(document, 0, 0, 612, 792);
        RawPdf.Text(document, page, "Locked text", 72, 700);
        document.Outlines.Add("Start", page);
        var security = document.SecuritySettings;
        if (open is not null) security.UserPassword = open;
        if (permissions is not null) security.OwnerPassword = permissions;
        if (limits)
        {
            security.PermitPrint = false;
            security.PermitFullQualityPrint = false;
            security.PermitExtractContent = false;
            security.PermitModifyDocument = false;
            security.PermitAnnotations = false;
            security.PermitFormsFill = false;
            security.PermitAssembleDocument = false;
        }
        switch (encryption)
        {
            case "aes256": document.SecurityHandler.SetEncryptionToV5(true); break;
            case "aes128": document.SecurityHandler.SetEncryptionToV4UsingAES(true); break;
            default: document.SecurityHandler.SetEncryptionToV2With128Bits(); break;
        }
        document.Save(path);
        return path;
    }

    /// <summary>
    /// A two-page PDF of pictures of every kind the PDF tools tell apart, each a width no other has (see
    /// <see cref="RawPdf.Pictures"/>). Page 1: a large JPEG shown small and again smaller, a photo kept losslessly, a
    /// screenshot, a CMYK JPEG and a tiny picture. Page 2: the large JPEG again, a picture with a soft mask, a 1-bit
    /// scan, a stencil mask and a JPEG 2000. Both pages have a line of text, and page 2 a bookmark.
    /// </summary>
    public string PicturesPdf(string name)
    {
        var path = PathOf(name);
        using var document = new PdfDocument();
        var first = RawPdf.Page(document, 0, 0, 612, 792);
        var second = RawPdf.Page(document, 0, 0, 612, 792);
        RawPdf.Text(document, first, "Pictures page one", 72, 750);
        RawPdf.Text(document, second, "Pictures page two", 72, 750);
        var pictures = RawPdf.Pictures(document);
        RawPdf.Draw(first, pictures["jpeg"], 72, 480, 240, 180);
        RawPdf.Draw(first, pictures["jpeg"], 330, 600, 120, 90);
        RawPdf.Draw(first, pictures["flate-photo"], 72, 280, 200, 150);
        RawPdf.Draw(first, pictures["screenshot"], 330, 380, 200, 150);
        RawPdf.Draw(first, pictures["cmyk"], 72, 60, 200, 150);
        RawPdf.Draw(first, pictures["tiny"], 330, 60, 10, 10);
        RawPdf.Draw(second, pictures["jpeg"], 72, 480, 240, 180);
        RawPdf.Draw(second, pictures["soft-mask"], 330, 560, 150, 150);
        RawPdf.Draw(second, pictures["one-bit"], 72, 300, 204, 100);
        RawPdf.Draw(second, pictures["stencil"], 330, 300, 96, 96, "0 0.5 0 rg");
        if (pictures.TryGetValue("jpeg2000", out var jpx)) RawPdf.Draw(second, jpx, 72, 60, 150, 112);
        document.Outlines.Add("Second page", second);
        document.Save(path);
        return path;
    }

    /// <summary>Chapters of pages, each page saying "Chapter page N", with a top-level bookmark at each chapter's first page and one beneath it at its second.</summary>
    public string BookmarkedPdf(string name, params (string Title, int Pages)[] chapters)
    {
        var path = PathOf(name);
        using var document = new PdfDocument();
        foreach (var (title, count) in chapters)
        {
            var pages = Enumerable.Range(1, count).Select(n =>
            {
                var page = RawPdf.Page(document, 0, 0, 612, 792);
                RawPdf.Text(document, page, $"{title} page {n}", 72, 700);
                return page;
            }).ToList();
            var bookmark = document.Outlines.Add(title, pages[0], opened: true);
            if (count > 1) bookmark.Outlines.Add($"{title} details", pages[1]);
        }
        document.Save(path);
        return path;
    }

    /// <summary>A page with a digital signature field whose signature is a placeholder: enough to be seen as signed, not to verify.</summary>
    public string SignedPdf(string name)
    {
        var path = PathOf(name);
        using var document = new PdfDocument();
        var page = RawPdf.Page(document, 0, 0, 612, 792);
        RawPdf.Text(document, page, "Signed text", 72, 700);
        var signature = RawPdf.Dict(document,
            ("/Type", new PdfName("/Sig")),
            ("/Filter", new PdfName("/Adobe.PPKLite")),
            ("/SubFilter", new PdfName("/adbe.pkcs7.detached")),
            ("/ByteRange", RawPdf.Array(0, 0, 0, 0)),
            ("/Contents", new PdfString(new string('0', 32))));
        document.Internals.AddObject(signature);
        var field = RawPdf.Annotation(document, page, "/Widget", 0, 0, 0, 0);
        field.Elements["/FT"] = new PdfName("/Sig");
        field.Elements["/T"] = new PdfString("Signature1");
        field.Elements["/V"] = signature.Reference;
        field.Elements.SetInteger("/F", 132); // print, locked
        document.Internals.Catalog.Elements["/AcroForm"] = RawPdf.Dict(document,
            ("/Fields", new PdfArray(document, field.Reference!)),
            ("/SigFlags", new PdfInteger(3)));
        document.Save(path);
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

    /// <summary>The first sound track's bitrate in bits a second, or the whole file's when the track doesn't say.</summary>
    public static long Bitrate(string path)
    {
        var stream = Probe(path, "-select_streams", "a:0", "-show_entries", "stream=bit_rate").Trim(',');
        return long.TryParse(stream, NumberStyles.None, CultureInfo.InvariantCulture, out var rate) ? rate
            : long.Parse(Probe(path, "-show_entries", "format=bit_rate").Trim(','), CultureInfo.InvariantCulture);
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

/// <summary>
/// Builds PDF objects by hand, for fixtures PDFsharp's drawing can't make: forms, comments, signatures, odd pictures,
/// and content placed at exact coordinates of the page's own space.
/// </summary>
static class RawPdf
{
    /// <summary>A new page whose media box runs from x0, y0 to x1, y1.</summary>
    public static PdfPage Page(PdfDocument document, double x0, double y0, double x1, double y1)
    {
        var page = document.AddPage();
        page.MediaBox = Rect(x0, y0, x1, y1);
        return page;
    }

    public static PdfRectangle Rect(double x0, double y0, double x1, double y1) => new(new XPoint(x0, y0), new XPoint(x1, y1));

    /// <summary>Adds operators to the end of the page's content, in one stream, so nothing is put between them.</summary>
    public static void Content(PdfPage page, string operators)
    {
        var added = Encoding.ASCII.GetBytes(operators + "\n");
        if (page.Contents.Elements.Count > 0 && page.Contents.Elements[0] is PdfReference { Value: PdfDictionary first })
            first.Stream.Value = [.. first.Stream.UnfilteredValue, .. added];
        else
            page.Contents.AppendContent().CreateStream(added);
    }

    /// <summary>A line of text in Helvetica with its baseline starting at x, y in the page's own coordinates.</summary>
    public static void Text(PdfDocument document, PdfPage page, string text, double x, double y, double size = 12)
    {
        var fonts = page.Resources.Elements.GetDictionary("/Font");
        if (fonts is null)
        {
            fonts = new PdfDictionary(document);
            page.Resources.Elements["/Font"] = fonts;
        }
        if (!fonts.Elements.ContainsKey("/RawHelv")) fonts.Elements["/RawHelv"] = Helvetica(document).Reference;
        Content(page, $"BT /RawHelv {Number(size)} Tf {Number(x)} {Number(y)} Td ({text}) Tj ET");
    }

    public static PdfDictionary Helvetica(PdfDocument document)
    {
        var font = Dict(document,
            ("/Type", new PdfName("/Font")),
            ("/Subtype", new PdfName("/Type1")),
            ("/BaseFont", new PdfName("/Helvetica")),
            ("/Encoding", new PdfName("/WinAnsiEncoding")));
        document.Internals.AddObject(font);
        return font;
    }

    public static PdfDictionary Dict(PdfDocument document, params (string Key, PdfItem Value)[] entries)
    {
        var dictionary = new PdfDictionary(document);
        foreach (var (key, value) in entries) dictionary.Elements[key] = value;
        return dictionary;
    }

    public static PdfArray Array(params double[] values)
    {
        var array = new PdfArray();
        foreach (var value in values) array.Elements.Add(new PdfReal(value));
        return array;
    }

    /// <summary>An annotation on the page at x0, y0 to x1, y1, printed and shown.</summary>
    public static PdfDictionary Annotation(PdfDocument document, PdfPage page, string subtype, double x0, double y0, double x1, double y1)
    {
        var annotation = Dict(document,
            ("/Type", new PdfName("/Annot")),
            ("/Subtype", new PdfName(subtype)),
            ("/Rect", Rect(x0, y0, x1, y1)),
            ("/F", new PdfInteger(4)),
            ("/P", page.Reference!));
        document.Internals.AddObject(annotation);
        if (page.Elements.GetArray("/Annots") is not { } annotations)
            page.Elements["/Annots"] = annotations = new PdfArray(document);
        annotations.Elements.Add(annotation.Reference!);
        return annotation;
    }

    /// <summary>A form XObject width by height, drawn by <paramref name="content"/>.</summary>
    public static PdfDictionary Form(PdfDocument document, double width, double height, string content, PdfDictionary? resources = null)
    {
        var form = Dict(document,
            ("/Type", new PdfName("/XObject")),
            ("/Subtype", new PdfName("/Form")),
            ("/BBox", Rect(0, 0, width, height)));
        if (resources is not null) form.Elements["/Resources"] = resources;
        form.CreateStream(Encoding.ASCII.GetBytes(content));
        document.Internals.AddObject(form);
        return form;
    }

    public static void Appearance(PdfDocument document, PdfDictionary annotation, double width, double height, string content, PdfDictionary? resources = null) =>
        annotation.Elements["/AP"] = Dict(document, ("/N", Form(document, width, height, content, resources).Reference!));

    public static PdfDictionary Stream(PdfDocument document, string text)
    {
        var stream = new PdfDictionary(document);
        stream.CreateStream(Encoding.UTF8.GetBytes(text));
        document.Internals.AddObject(stream);
        return stream;
    }

    /// <summary>Draws a picture at x, y, width by height on the page; stencil masks are painted in <paramref name="colour"/>, such as "1 0 0 rg".</summary>
    public static void Draw(PdfPage page, PdfDictionary picture, double x, double y, double width, double height, string colour = "")
    {
        var pictures = page.Resources.Elements.GetDictionary("/XObject");
        if (pictures is null)
        {
            pictures = new PdfDictionary(page.Owner);
            page.Resources.Elements["/XObject"] = pictures;
        }
        var name = pictures.Elements.FirstOrDefault(e => e.Value is PdfReference r && r.Value == picture).Key;
        if (name is null)
        {
            name = $"/RawIm{pictures.Elements.Count}";
            pictures.Elements[name] = picture.Reference;
        }
        Content(page, $"q {colour} {Number(width)} 0 0 {Number(height)} {Number(x)} {Number(y)} cm {name} Do Q");
    }

    /// <summary>
    /// One picture of each kind, by name, each a width the others don't have: "jpeg" (1200 × 900 RGB JPEG),
    /// "flate-photo" (640 × 480, a noisy photo kept losslessly), "screenshot" (400 × 300, few colours, lossless),
    /// "cmyk" (560 × 420 CMYK JPEG), "tiny" (10 × 10), "soft-mask" (200 × 200, a green disc on a soft mask),
    /// "one-bit" (408 × 200, black and white), "stencil" (96 × 96 stencil mask) and, when ImageMagick can write it,
    /// "jpeg2000" (300 × 225).
    /// </summary>
    public static Dictionary<string, PdfDictionary> Pictures(PdfDocument document)
    {
        var pictures = new Dictionary<string, PdfDictionary>();
        using (var photo = Noisy(1200, 900))
        {
            photo.Quality = 95;
            pictures["jpeg"] = Picture(document, photo.ToByteArray(MagickFormat.Jpeg), 1200, 900, "/DeviceRGB", 8, "/DCTDecode");
        }
        using (var photo = Noisy(640, 480))
            pictures["flate-photo"] = Picture(document, Zlib(photo.GetPixels().ToByteArray(PixelMapping.RGB)!), 640, 480, "/DeviceRGB", 8, "/FlateDecode");
        using (var screenshot = new MagickImage(MagickColors.White, 400, 300))
        {
            new ImageMagick.Drawing.Drawables()
                .FillColor(new MagickColor("#3A7BD5")).Rectangle(0, 0, 400, 40)
                .FillColor(new MagickColor("#F97A3D")).Rectangle(20, 60, 180, 280)
                .FillColor(new MagickColor("#2B2B2B")).Rectangle(200, 70, 380, 80).Rectangle(200, 100, 360, 110).Rectangle(200, 130, 370, 140)
                .Draw(screenshot);
            pictures["screenshot"] = Picture(document, Zlib(screenshot.GetPixels().ToByteArray(PixelMapping.RGB)!), 400, 300, "/DeviceRGB", 8, "/FlateDecode");
        }
        using (var cmyk = Noisy(560, 420))
        {
            cmyk.ColorSpace = ColorSpace.CMYK;
            cmyk.Quality = 95;
            // ImageMagick writes CMYK JPEGs inverted, as Photoshop does, which PDFs undo with /Decode.
            pictures["cmyk"] = Picture(document, cmyk.ToByteArray(MagickFormat.Jpeg), 560, 420, "/DeviceCMYK", 8, "/DCTDecode",
                ("/Decode", Array(1, 0, 1, 0, 1, 0, 1, 0)));
        }
        pictures["tiny"] = Picture(document, Zlib(Enumerable.Repeat((byte)200, 10 * 10 * 3).ToArray()), 10, 10, "/DeviceRGB", 8, "/FlateDecode");
        using (var disc = new MagickImage(MagickColors.Black, 200, 200))
        {
            new ImageMagick.Drawing.Drawables().FillColor(MagickColors.White).Circle(100, 100, 100, 10).Draw(disc);
            var mask = Picture(document, Zlib(disc.GetPixels().ToByteArray(PixelMapping.RGB)!.Where((_, i) => i % 3 == 0).ToArray()), 200, 200, "/DeviceGray", 8, "/FlateDecode");
            var green = Enumerable.Range(0, 200 * 200).SelectMany(_ => new byte[] { 30, 160, 60 }).ToArray();
            pictures["soft-mask"] = Picture(document, Zlib(green), 200, 200, "/DeviceRGB", 8, "/FlateDecode", ("/SMask", mask.Reference!));
        }
        pictures["one-bit"] = Picture(document, Zlib(Bits(408, 200, (x, y) => (x / 24 + y / 24) % 2 == 0)), 408, 200, "/DeviceGray", 1, "/FlateDecode");
        pictures["stencil"] = Picture(document, Zlib(Bits(96, 96, (x, y) => (x - 48) * (x - 48) + (y - 48) * (y - 48) < 40 * 40)), 96, 96, null, 1, "/FlateDecode",
            ("/ImageMask", new PdfBoolean(true)));
        try
        {
            using var photo = Noisy(300, 225);
            pictures["jpeg2000"] = Picture(document, photo.ToByteArray(MagickFormat.Jp2), 300, 225, "/DeviceRGB", 8, "/JPXDecode");
        }
        catch (MagickException)
        {
            // This build of ImageMagick has no JPEG 2000 writer.
        }
        return pictures;
    }

    static MagickImage Noisy(int width, int height)
    {
        var image = new MagickImage("plasma:orange-steelblue", (uint)width, (uint)height);
        image.AddNoise(NoiseType.Gaussian);
        return image;
    }

    /// <summary>Rows of 1-bit pixels, each row padded to a whole byte; 1 is white.</summary>
    static byte[] Bits(int width, int height, Func<int, int, bool> black)
    {
        var stride = (width + 7) / 8;
        var bits = new byte[stride * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (!black(x, y)) bits[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
        return bits;
    }

    static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal))
            zlib.Write(data);
        return output.ToArray();
    }

    static PdfDictionary Picture(PdfDocument document, byte[] data, int width, int height, string? colourSpace, int bits, string filter,
        params (string Key, PdfItem Value)[] extra)
    {
        var picture = Dict(document,
            ("/Type", new PdfName("/XObject")),
            ("/Subtype", new PdfName("/Image")),
            ("/Width", new PdfInteger(width)),
            ("/Height", new PdfInteger(height)),
            ("/BitsPerComponent", new PdfInteger(bits)),
            ("/Filter", new PdfName(filter)));
        if (colourSpace is not null) picture.Elements["/ColorSpace"] = new PdfName(colourSpace);
        foreach (var (key, value) in extra) picture.Elements[key] = value;
        picture.CreateStream(data);
        document.Internals.AddObject(picture);
        return picture;
    }

    static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

using ImageMagick;

namespace Tanjelo.Core.Converters;

public sealed class ImageConverter(Toolbox tools) : IConverter
{
    static readonly Target[] Outputs =
    [
        new("png", Strings.PNG, Strings.LosslessKeepsTransparency, Section.Image),
        new("jpg", Strings.JPG, Strings.SmallPhotosNoTransparency, Section.Image),
        new("webp", Strings.WebP, Strings.ModernWebFormat, Section.Image),
        new("avif", Strings.AVIF, Strings.SmallestFilesNewerAppsOnly, Section.Image),
        new("gif", Strings.GIF, Strings.N256ColoursKeepsAnimation, Section.Image),
        new("bmp", Strings.BMP, Strings.UncompressedBitmap, Section.Image),
        new("tiff", Strings.TIFF, Strings.PrintAndArchiving, Section.Image),
        new("ico", Strings.ICO, Strings.WindowsIcon16To256Px, Section.Image),
        new("pdf", Strings.PDF, Strings.ImageAsAPDFPage, Section.Document),
    ];

    static readonly Target Heic = new("heic", Strings.HEIC, Strings.SmallPhotosForIPhoneAndMac, Section.Image);
    static readonly Target HeicStill = Heic with { Description = Strings.FirstFrameOnlyForIPhoneAndMac };

    static readonly HashSet<string> Animated = ["gif", "webp"];

    public IEnumerable<Target> TargetsFor(string input)
    {
        var key = Formats.Key(input);
        if (!Formats.Images.Contains(key)) return [];
        // HEIC sits with the other photo formats, after WebP and AVIF.
        return tools.Heic ? [.. Outputs[..4], key == "gif" ? HeicStill : Heic, .. Outputs[4..]] : Outputs;
    }

    // Only for photos, where HEIC is what people would look for; not on every picture's menu.
    public string? HintFor(string input) => !tools.Heic && Formats.Key(input) is "jpg" or "heic" ? HeicEncoder.Missing : null;

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => target.Key == "heic"
        ? ToHeicAsync(input, ctx)
        : Task.Run<IReadOnlyList<string>>(() =>
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        var inputKey = Formats.Key(input);
        var output = OutputPaths.Beside(input, target.Key);

        using var frames = Read(input);
        try
        {
            if (frames.Count > 1 && Animated.Contains(inputKey) && Animated.Contains(target.Key))
            {
                frames.Coalesce();
                if (target.Key == "gif") frames.Optimize();
                frames.Write(output, FormatFor(target.Key));
            }
            else if (frames.Count > 1 && inputKey == "tiff" && target.Key == "pdf")
            {
                foreach (var page in frames) PrepareForPdf(page, inputKey);
                frames.Write(output, MagickFormat.Pdf);
            }
            else
            {
                var image = frames[0];
                image.AutoOrient();
                Prepare(image, target.Key, inputKey, ctx.Options);
                image.Write(output, FormatFor(target.Key));
            }
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        ctx.Progress.Report(1);
        return [output];
    }, ctx.Cancellation);

    /// <summary>The first frame, upright, through Windows' HEIF encoder, keeping the date taken, the camera and the location.</summary>
    static async Task<IReadOnlyList<string>> ToHeicAsync(string input, JobContext ctx)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        var output = OutputPaths.Beside(input, "heic");
        using var frames = Read(input);
        try
        {
            await HeicEncoder.WriteAsync(frames[0], output, ctx.Options.HeicQuality);
        }
        catch (Exception ex) when (ex is not (ConversionException or OperationCanceledException))
        {
            OutputPaths.TryDelete(output);
            throw new ConversionException(string.Format(Strings.WindowsCouldnTWriteTheHEIC, ex.Message.Split('\n')[0].Trim()), ex);
        }
        ctx.Progress.Report(1);
        return [output];
    }

    internal static MagickImageCollection Read(string input)
    {
        var settings = new MagickReadSettings();
        if (Formats.Key(input) == "svg")
        {
            settings.Density = new Density(192);
            settings.BackgroundColor = MagickColors.None;
        }
        var frames = new MagickImageCollection();
        try
        {
            frames.Read(input, settings);
        }
        catch (MagickException ex)
        {
            frames.Dispose();
            throw new ConversionException(string.Format(Strings.ThisDoesnTLookLikeAValid, Reason(ex)), ex);
        }
        if (frames.Count == 0)
        {
            frames.Dispose();
            throw new ConversionException(Strings.TheImageHasNoFrames);
        }
        return frames;
    }

    /// <summary>"improper image header `C:\x.png' @ error/png.c/ReadPNGImage/3956" becomes "improper image header".</summary>
    internal static string Reason(MagickException ex)
    {
        var message = ex.Message.Split('\n')[0];
        var cut = message.IndexOfAny(['`', '@']);
        return (cut > 0 ? message[..cut] : message).Trim().TrimEnd('.', ':');
    }

    static void Prepare(IMagickImage<byte> image, string targetKey, string inputKey, ConverterOptions options)
    {
        switch (targetKey)
        {
            case "jpg":
                Flatten(image);
                image.Quality = (uint)options.JpegQuality;
                break;
            case "webp":
                image.Quality = (uint)options.WebpQuality;
                break;
            case "avif":
                image.Quality = 70;
                break;
            case "ico":
                // Icons must be square and at most 256 px; ImageMagick then writes every standard size.
                var side = Math.Max(image.Width, image.Height);
                image.BackgroundColor = MagickColors.Transparent;
                image.Extent(side, side, Gravity.Center);
                if (side > 256) image.Resize(256, 256);
                image.Settings.SetDefine(MagickFormat.Icon, "auto-resize", "256,128,64,48,32,16");
                break;
            case "tiff":
                image.Settings.Compression = CompressionMethod.LZW;
                break;
            case "pdf":
                PrepareForPdf(image, inputKey);
                break;
        }
    }

    /// <summary>Sizes the page so the long edge is ~11 inches and keeps photos JPEG-compressed inside the PDF.</summary>
    internal static void PrepareForPdf(IMagickImage<byte> image, string inputKey)
    {
        var dpi = Math.Max(72, Math.Max(image.Width, image.Height) / 11.0);
        image.Density = new Density(dpi, dpi, DensityUnit.PixelsPerInch);
        if (inputKey is "jpg" or "heic" or "avif" or "webp" && !image.HasAlpha)
        {
            image.Settings.Compression = CompressionMethod.JPEG;
            image.Quality = 90;
        }
        else
        {
            image.Settings.Compression = CompressionMethod.Zip;
        }
    }

    static void Flatten(IMagickImage<byte> image)
    {
        if (!image.HasAlpha) return;
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaAction.Remove);
    }

    /// <summary>
    /// The format for a changed copy, such as a cropped picture: the input's own when Tanjelo can write it,
    /// otherwise PNG, or JPG for an opaque photo (from HEIC, say).
    /// </summary>
    internal static string ChangedCopyKey(string input, bool alpha) =>
        Formats.Key(input) is "png" or "jpg" or "webp" or "avif" or "gif" or "bmp" or "tiff" ? Formats.Key(input) : alpha ? "png" : "jpg";

    internal static MagickFormat FormatFor(string key) => key switch
    {
        "png" => MagickFormat.Png,
        "jpg" => MagickFormat.Jpeg,
        "webp" => MagickFormat.WebP,
        "avif" => MagickFormat.Avif,
        "gif" => MagickFormat.Gif,
        "bmp" => MagickFormat.Bmp,
        "tiff" => MagickFormat.Tiff,
        "ico" => MagickFormat.Ico,
        "pdf" => MagickFormat.Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };
}

/// <summary>Several images become the pages of one PDF.</summary>
public sealed class CombinePdfOperation : IBatchOperation
{
    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        if (inputs.Count >= 2 && inputs.All(i => Formats.KindOf(i) == FileKind.Image))
            yield return new Target("combine-pdf", Strings.OnePDF, string.Format(Strings.CombineImagesAsPages, inputs.Count), Section.Document)
            {
                IsBatch = true,
                Running = Strings.CombiningIntoAPDF,
                Done = Strings.CombinedIntoOnePDF,
            };
    }

    public Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var output = OutputPaths.Unique(OutputPaths.DirectoryOf(inputs[0]), "Combined", "pdf");
        using var pages = new MagickImageCollection();
        for (var i = 0; i < inputs.Count; i++)
        {
            ctx.Cancellation.ThrowIfCancellationRequested();
            using var frames = ImageConverter.Read(inputs[i]);
            var page = (IMagickImage<byte>)frames[0].Clone();
            page.AutoOrient();
            ImageConverter.PrepareForPdf(page, Formats.Key(inputs[i]));
            pages.Add(page);
            ctx.Progress.Report((i + 1.0) / (inputs.Count + 1));
        }
        try
        {
            pages.Write(output, MagickFormat.Pdf);
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        return [output];
    }, ctx.Cancellation);
}

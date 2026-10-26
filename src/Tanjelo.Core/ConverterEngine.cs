using System.Runtime.InteropServices;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core;

public sealed record BatchProgress(int Index, int Count, string Current, double Fraction);
public sealed record Failure(string Input, string Message);
/// <param name="Skipped">Inputs that were already in the target format.</param>
public sealed record BatchResult(IReadOnlyList<string> Outputs, IReadOnlyList<Failure> Failures, IReadOnlyList<string> Skipped, bool Canceled)
{
    /// <summary>Things worth knowing about the results, such as pages read with OCR, each naming its file when several were selected.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed class ConverterEngine
{
    readonly IConverter[] _converters;
    readonly IBatchOperation[] _batchOperations;

    public Toolbox Tools { get; }
    public ConverterOptions Options { get; }

    /// <param name="desktop">What the app does with windows (Share, Float); without it those actions say they need the app.</param>
    public ConverterEngine(Toolbox tools, ConverterOptions? options = null, IDesktopActions? desktop = null)
        // Order matters: when two converters offer the same key for a file, the earlier one wins.
        : this(tools, options,
            [
                new ImageConverter(tools),
                new PictureTextConverter(tools),
                new SubtitleConverter(),
                new MarkdownConverter(tools),
                new PandocConverter(tools),
                new PdfConverter(),
                new PdfTextConverter(),
                new PdfSlidesConverter(),
                new SplitPdfTool(),
                new OfficeConverter(tools),
                new LibreOfficeConverter(tools),
                new MediaConverter(tools),
                new MediaTools(tools),
                new CompressTool(tools),
                new MetadataTool(tools),
                new CropImageTool(),
                new CropVideoTool(tools),
                new VideoMomentsTool(tools),
                new VisualizerTool(tools),
                new ChannelVolumeTool(tools),
                new BackgroundTool(),
                new ArchiveConverter(tools),
                new MoveToFolderAction(),
            ],
            [
                new CombinePdfOperation(),
                new CombinePptxOperation(),
                new CollageOperation(),
                new JoinVideosOperation(tools),
                new MergePdfOperation(),
                new CompressOperation(tools),
                new ClipboardActions(new WindowsClipboard()),
                new OpenTerminalAction(tools),
                new DesktopActions(desktop),
            ])
    {
    }

    internal ConverterEngine(Toolbox tools, ConverterOptions? options, IConverter[] converters, IBatchOperation[] batchOperations)
    {
        Tools = tools;
        Options = options ?? new ConverterOptions();
        _converters = converters;
        _batchOperations = batchOperations;
    }

    /// <summary>Formats every selected file can become, plus whole-selection operations.</summary>
    public MenuModel GetMenu(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
            return new MenuModel([], []);

        // Converters list every output they can write, including the input's own format. A file that is
        // already in a format just gets skipped, so "PNG" stays on offer for a mixed PNG + JPG selection.
        // Tools and actions have to be offered for every file.
        var perFile = inputs
            .Select(input => (Own: Formats.Key(input), Keys: PerFileTargets(input).Select(x => x.Target.Key).ToHashSet()))
            .ToList();
        var common = inputs
            .SelectMany(input => PerFileTargets(input).Select(x => x.Target))
            .DistinctBy(t => t.Key)
            .Where(t => t.Kind == TargetKind.Convert
                ? perFile.All(f => f.Own == t.Key || f.Keys.Contains(t.Key)) && perFile.Any(f => f.Own != t.Key)
                : perFile.All(f => f.Keys.Contains(t.Key)));

        var primary = PrimarySection(inputs[0]);
        var targets = common
            .Concat(_batchOperations.SelectMany(b => Safe(() => b.TargetsFor(inputs))))
            .Select((target, index) => (target, index))
            .OrderBy(x => x.target.Section == primary ? -1 : (int)x.target.Section)
            // Actions on the whole selection (copy, open) come before the one that changes each item (move).
            .ThenBy(x => x.target.Kind == TargetKind.Action && !x.target.IsBatch)
            .ThenBy(x => x.index)
            .Select(x => x.target)
            .ToList();

        var hints = inputs
            .SelectMany(input => _converters.Select(c => Safe(() => c.HintFor(input))))
            .OfType<string>()
            .Distinct()
            .ToList();

        return new MenuModel(targets, hints);
    }

    /// <summary>Runs a menu entry over the selection. The entry's parameter values are used as given.</summary>
    public async Task<BatchResult> RunAsync(IReadOnlyList<string> inputs, Target target, IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        using var ctx = new JobContext { Tools = Tools, Options = Options, Cancellation = ct };
        var outputs = new List<string>();
        var failures = new List<Failure>();
        var skipped = new List<string>();
        var notes = new List<string>();

        try
        {
            if (target.IsBatch)
            {
                var label = inputs.Count == 1 ? Path.GetFileName(inputs[0]) : $"{inputs.Count} items";
                var (op, own) = _batchOperations
                    .SelectMany(b => b.TargetsFor(inputs).Where(t => t.Key == target.Key).Select(t => (Op: b, Target: t)))
                    .First();
                ctx.Progress = new ActionProgress<double>(f => progress?.Report(new BatchProgress(0, 1, label, Math.Clamp(f, 0, 1))));
                progress?.Report(new BatchProgress(0, 1, label, double.NaN));
                try
                {
                    outputs.AddRange(await op.RunAsync(inputs, own.WithParameters(target.Parameters), ctx));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(new Failure(label, Describe(ex)));
                }
                notes.AddRange(ctx.NotesSince(0));
            }
            else
            {
                for (var i = 0; i < inputs.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var input = inputs[i];
                    var index = i;
                    var name = Path.GetFileName(input);
                    progress?.Report(new BatchProgress(index, inputs.Count, name, double.NaN));
                    ctx.Progress = new ActionProgress<double>(f =>
                        progress?.Report(new BatchProgress(index, inputs.Count, name, (index + Math.Clamp(f, 0, 1)) / inputs.Count)));

                    if (target.Kind == TargetKind.Convert && Formats.Key(input) == target.Key)
                    {
                        skipped.Add(input);
                        continue;
                    }
                    var match = PerFileTargets(input).FirstOrDefault(x => x.Target.Key == target.Key);
                    if (match.Converter is null)
                    {
                        failures.Add(new Failure(input, target.Kind == TargetKind.Convert
                            ? $"Can't convert this file to {target.Label}."
                            : $"{target.Label} doesn't work on this file."));
                        continue;
                    }
                    var noted = ctx.NoteCount;
                    try
                    {
                        outputs.AddRange(await match.Converter.ConvertAsync(input, match.Target.WithParameters(target.Parameters), ctx));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures.Add(new Failure(input, Describe(ex)));
                    }
                    notes.AddRange(ctx.NotesSince(noted).Select(note => inputs.Count > 1 ? $"{name}: {note}" : note));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new BatchResult(outputs, failures, skipped, Canceled: true) { Notes = notes };
        }
        return new BatchResult(outputs, failures, skipped, Canceled: false) { Notes = notes };
    }

    /// <summary>Whether the entry can show its result before it runs, in the options dialog.</summary>
    public bool CanPreview(IReadOnlyList<string> inputs, Target target) => inputs.Count > 0 && Find(inputs, target).Previewer is not null;

    /// <summary>
    /// Shows what the entry would do with these parameter values, without writing anything beside the inputs.
    /// Null when the entry has no preview. Problems with the values come back as <see cref="ConversionException"/>.
    /// </summary>
    public async Task<Preview?> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, CancellationToken ct = default)
    {
        if (inputs.Count == 0) return null;
        var (previewer, own) = Find(inputs, target);
        if (previewer is null) return null;
        using var ctx = new JobContext { Tools = Tools, Options = Options, Cancellation = ct };
        try
        {
            return await previewer.PreviewAsync(inputs, own!.WithParameters(target.Parameters), request, ctx);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ConversionException))
        {
            throw new ConversionException(Describe(ex), ex);
        }
    }

    /// <summary>The previewer behind an entry and its own copy of the entry; per-file entries are looked up on the first input.</summary>
    (IPreviewer? Previewer, Target? Own) Find(IReadOnlyList<string> inputs, Target target)
    {
        if (target.IsBatch)
        {
            foreach (var op in _batchOperations)
                if (Safe(() => op.TargetsFor(inputs)).FirstOrDefault(t => t.Key == target.Key) is { } own)
                    return (op as IPreviewer, own);
            return (null, null);
        }
        var match = PerFileTargets(inputs[0]).FirstOrDefault(x => x.Target.Key == target.Key);
        return (match.Converter as IPreviewer, match.Target);
    }

    IEnumerable<(Target Target, IConverter Converter)> PerFileTargets(string input)
    {
        var folder = Directory.Exists(input);
        var seen = new HashSet<string>();
        foreach (var converter in _converters.Where(c => !folder || c.HandlesFolders))
            foreach (var target in Safe(() => converter.TargetsFor(input).ToList()))
                if (seen.Add(target.Key))
                    yield return (target, converter);
    }

    static Section PrimarySection(string input) => Formats.KindOf(input) switch
    {
        FileKind.Image => Section.Image,
        FileKind.Video => Section.Video,
        FileKind.Audio => Section.Audio,
        FileKind.Pdf or FileKind.WordDocument or FileKind.Presentation or FileKind.Spreadsheet or FileKind.Subtitle or FileKind.Markdown or FileKind.Ebook or FileKind.WebPage => Section.Document,
        _ => Section.Archive,
    };

    static string Describe(Exception ex) => ex switch
    {
        ConversionException => ex.Message,
        ImageMagick.MagickException magick => $"Image error: {ImageConverter.Reason(magick)}.",
        COMException com => $"Office reported an error: {com.Message}",
        UnauthorizedAccessException => "Access denied. The folder may be read-only.",
        _ => ex.Message.Split('\n')[0].Trim(),
    };

    // A converter that throws while building the menu should lose its entries, not break the menu.
    static string? Safe(Func<string?> get)
    {
        try { return get(); }
        catch { return null; }
    }

    static IEnumerable<Target> Safe(Func<IEnumerable<Target>> get)
    {
        try { return get().ToList(); }
        catch { return []; }
    }
}

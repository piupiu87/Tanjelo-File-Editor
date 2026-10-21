using System.Runtime.InteropServices;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core;

public sealed record BatchProgress(int Index, int Count, string Current, double Fraction);
public sealed record Failure(string Input, string Message);
/// <param name="Skipped">Inputs that were already in the target format.</param>
public sealed record BatchResult(IReadOnlyList<string> Outputs, IReadOnlyList<Failure> Failures, IReadOnlyList<string> Skipped, bool Canceled);

public sealed class ConverterEngine
{
    readonly IConverter[] _converters;
    readonly IBatchOperation[] _batchOperations;

    public Toolbox Tools { get; }
    public ConverterOptions Options { get; }

    public ConverterEngine(Toolbox tools, ConverterOptions? options = null)
    {
        Tools = tools;
        Options = options ?? new ConverterOptions();
        // Order matters: when two converters offer the same key for a file, the earlier one wins.
        _converters =
        [
            new ImageConverter(),
            new PdfConverter(),
            new OfficeConverter(tools),
            new LibreOfficeConverter(tools),
            new MediaConverter(tools),
            new ArchiveConverter(tools),
        ];
        _batchOperations = [new CombinePdfOperation(), new CompressOperation(tools)];
    }

    /// <summary>Formats every selected file can become, plus whole-selection operations.</summary>
    public MenuModel GetMenu(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
            return new MenuModel([], []);

        // Converters list every output they can write, including the input's own format. A file that is
        // already in a format just gets skipped, so "PNG" stays on offer for a mixed PNG + JPG selection.
        var perFile = inputs
            .Select(input => (Own: Formats.Key(input), Keys: PerFileTargets(input).Select(x => x.Target.Key).ToHashSet()))
            .ToList();
        var common = inputs
            .SelectMany(input => PerFileTargets(input).Select(x => x.Target))
            .DistinctBy(t => t.Key)
            .Where(t => perFile.All(f => f.Own == t.Key || f.Keys.Contains(t.Key)) && perFile.Any(f => f.Own != t.Key));

        var primary = PrimarySection(inputs[0]);
        var targets = common
            .Concat(_batchOperations.SelectMany(b => Safe(() => b.TargetsFor(inputs))))
            .Select((target, index) => (target, index))
            .OrderBy(x => x.target.Section == primary ? -1 : (int)x.target.Section)
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

    public async Task<BatchResult> RunAsync(IReadOnlyList<string> inputs, Target target, IProgress<BatchProgress>? progress = null, CancellationToken ct = default)
    {
        using var ctx = new JobContext { Tools = Tools, Options = Options, Cancellation = ct };
        var outputs = new List<string>();
        var failures = new List<Failure>();
        var skipped = new List<string>();

        try
        {
            if (target.IsBatch)
            {
                var label = inputs.Count == 1 ? Path.GetFileName(inputs[0]) : $"{inputs.Count} items";
                var op = _batchOperations.First(b => b.TargetsFor(inputs).Any(t => t.Key == target.Key));
                ctx.Progress = new ActionProgress<double>(f => progress?.Report(new BatchProgress(0, 1, label, Math.Clamp(f, 0, 1))));
                progress?.Report(new BatchProgress(0, 1, label, double.NaN));
                try
                {
                    outputs.AddRange(await op.RunAsync(inputs, target, ctx));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add(new Failure(label, Describe(ex)));
                }
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

                    if (Formats.Key(input) == target.Key)
                    {
                        skipped.Add(input);
                        continue;
                    }
                    var match = PerFileTargets(input).FirstOrDefault(x => x.Target.Key == target.Key);
                    if (match.Converter is null)
                    {
                        failures.Add(new Failure(input, $"Can't convert this file to {target.Label}."));
                        continue;
                    }
                    try
                    {
                        outputs.AddRange(await match.Converter.ConvertAsync(input, match.Target, ctx));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures.Add(new Failure(input, Describe(ex)));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new BatchResult(outputs, failures, skipped, Canceled: true);
        }
        return new BatchResult(outputs, failures, skipped, Canceled: false);
    }

    IEnumerable<(Target Target, IConverter Converter)> PerFileTargets(string input)
    {
        if (Directory.Exists(input))
            yield break;
        var seen = new HashSet<string>();
        foreach (var converter in _converters)
            foreach (var target in Safe(() => converter.TargetsFor(input).ToList()))
                if (seen.Add(target.Key))
                    yield return (target, converter);
    }

    static Section PrimarySection(string input) => Formats.KindOf(input) switch
    {
        FileKind.Image => Section.Image,
        FileKind.Video => Section.Video,
        FileKind.Audio => Section.Audio,
        FileKind.Pdf or FileKind.WordDocument or FileKind.Presentation or FileKind.Spreadsheet => Section.Document,
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

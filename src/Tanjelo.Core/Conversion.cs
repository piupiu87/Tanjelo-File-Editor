namespace Tanjelo.Core;

/// <summary>Groups menu entries by what the output is, not what the input was.</summary>
public enum Section { Image, Video, Audio, Document, Archive }

/// <summary>Which tab of the menu an entry is on.</summary>
public enum TargetKind
{
    /// <summary>Writes a copy in another format.</summary>
    Convert,

    /// <summary>Writes a changed copy, usually in the same format: mute a video, split a PDF.</summary>
    Tool,

    /// <summary>Works with the selected files themselves, such as copying their paths.</summary>
    Action,
}

/// <summary>
/// One entry in the format menu. <see cref="Key"/> names the output ("png", "mp4", "extract") and is
/// what multi-file selections are intersected on, so different converters can serve the same key.
/// </summary>
public sealed record Target(string Key, string Label, string Description, Section Section)
{
    static readonly IReadOnlyDictionary<string, string> NoParameters = new Dictionary<string, string>();

    /// <summary>Runs once over the whole selection (e.g. "compress to ZIP") instead of once per file.</summary>
    public bool IsBatch { get; init; }

    public TargetKind Kind { get; init; } = TargetKind.Convert;

    /// <summary>
    /// Settings the operation reads, with their default values (e.g. parts=3 for a split). Menu entries are
    /// presets; front ends may override the values, as tanj does with <c>--parts=4</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = NoParameters;

    /// <summary>Progress heading such as "Muting…"; conversions get "Converting to PNG…".</summary>
    public string? Running { get; init; }

    /// <summary>Result heading such as "Muted"; conversions get "Converted to PNG".</summary>
    public string? Done { get; init; }

    public string RunningText => Running ?? $"Converting to {Label}…";
    public string DoneText => Done ?? $"Converted to {Label}";

    public string Parameter(string name) =>
        Parameters.TryGetValue(name, out var value) ? value : throw new ConversionException($"{Label} needs a value for '{name}'.");

    public double Number(string name, double min, double max)
    {
        var text = Parameter(name);
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
            throw new ConversionException($"'{name}' must be a number from {min} to {max}, not '{text}'.");
        return value;
    }

    /// <summary>A copy whose parameters are these defaults overlaid with <paramref name="values"/>.</summary>
    public Target WithParameters(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0) return this;
        var merged = new Dictionary<string, string>(Parameters);
        foreach (var (name, value) in values)
            merged[name] = value;
        return this with { Parameters = merged };
    }
}

public sealed record MenuModel(IReadOnlyList<Target> Targets, IReadOnlyList<string> Hints);

/// <summary>Per-file work: a conversion, or a tool that writes a changed copy (see <see cref="Target.Kind"/>).</summary>
public interface IConverter
{
    IEnumerable<Target> TargetsFor(string input);

    /// <summary>Explains why nothing is offered for a file this converter would normally handle, e.g. a missing tool.</summary>
    string? HintFor(string input) => null;

    /// <summary>Writes the output next to the input and returns the created paths.</summary>
    Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx);
}

/// <summary>Work done once over the whole selection, such as compressing it or copying its paths.</summary>
public interface IBatchOperation
{
    IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs);
    Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx);
}

/// <summary>A failure whose message is fit to show the user as-is.</summary>
public sealed class ConversionException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ConverterOptions
{
    public int JpegQuality { get; set; } = 90;
    public int WebpQuality { get; set; } = 90;
    public int PdfRenderDpi { get; set; } = 200;

    /// <summary>How long one Office conversion may take before the Office app we started is shut down.</summary>
    public TimeSpan OfficeTimeout { get; set; } = TimeSpan.FromMinutes(3);
}

public sealed class JobContext : IDisposable
{
    readonly Dictionary<Type, IDisposable> _sessions = [];

    public required Toolbox Tools { get; init; }
    public required ConverterOptions Options { get; init; }
    public CancellationToken Cancellation { get; init; }

    /// <summary>Progress of the current item, 0..1.</summary>
    public IProgress<double> Progress { get; set; } = new ActionProgress<double>(_ => { });

    /// <summary>Returns a resource shared by every item in the batch (e.g. a running Word instance), disposed when the batch ends.</summary>
    public T Session<T>(Func<T> create) where T : class, IDisposable
    {
        lock (_sessions)
        {
            if (!_sessions.TryGetValue(typeof(T), out var session))
                _sessions[typeof(T)] = session = create();
            return (T)session;
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            try { session.Dispose(); } catch { /* best effort: never mask the batch result */ }
        }
        _sessions.Clear();
    }
}

/// <summary>Reports synchronously on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
public sealed class ActionProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

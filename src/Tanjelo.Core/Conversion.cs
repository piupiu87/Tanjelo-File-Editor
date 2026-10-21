namespace Tanjelo.Core;

/// <summary>Groups menu entries by what the output is, not what the input was.</summary>
public enum Section { Image, Video, Audio, Document, Archive }

/// <summary>
/// One entry in the format menu. <see cref="Key"/> names the output ("png", "mp4", "extract") and is
/// what multi-file selections are intersected on, so different converters can serve the same key.
/// </summary>
public sealed record Target(string Key, string Label, string Description, Section Section)
{
    /// <summary>Runs once over the whole selection (e.g. "compress to ZIP") instead of once per file.</summary>
    public bool IsBatch { get; init; }
}

public sealed record MenuModel(IReadOnlyList<Target> Targets, IReadOnlyList<string> Hints);

public interface IConverter
{
    IEnumerable<Target> TargetsFor(string input);

    /// <summary>Explains why nothing is offered for a file this converter would normally handle, e.g. a missing tool.</summary>
    string? HintFor(string input) => null;

    /// <summary>Writes the output next to the input and returns the created paths.</summary>
    Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx);
}

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

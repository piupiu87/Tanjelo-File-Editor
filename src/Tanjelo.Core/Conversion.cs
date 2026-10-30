namespace Tanjelo.Core;

/// <summary>Groups menu entries by what the output is, not what the input was.</summary>
public enum Section { Image, Video, Audio, Document, Archive, File }

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

/// <summary>The editors the app has, for tools that set their parameters by hand (see <see cref="Target.Editor"/>).</summary>
public enum EditorKind { OrganizePdf, Redact, Annotate, Photo, Trim, Bleep, RedactVideo }

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

    /// <summary>
    /// The parameters the user is asked for before the entry runs; the app shows an options dialog when there are
    /// any. Set them with <see cref="WithOptions"/>, which puts their defaults in <see cref="Parameters"/>.
    /// </summary>
    public IReadOnlyList<Option> Options { get; init; } = [];

    /// <summary>The options dialog's button, such as "Save"; by default the label without its ellipsis.</summary>
    public string? Confirm { get; init; }

    /// <summary>Progress heading such as "Muting…"; conversions get "Converting to PNG…".</summary>
    public string? Running { get; init; }

    /// <summary>Result heading such as "Muted"; conversions get "Converted to PNG".</summary>
    public string? Done { get; init; }

    /// <summary>When set, the front end asks for a folder with this prompt and passes it as the "folder" parameter.</summary>
    public string? FolderPrompt { get; init; }

    /// <summary>For actions that show their own window, like the Share sheet: no progress card, only an error if it fails.</summary>
    public bool Quiet { get; init; }

    /// <summary>
    /// For tools worked by hand, such as putting pages in order or drawing on a photo: the app opens this editor
    /// instead of the options dialog, and saving fills in the parameters. tanj takes the same parameters as settings.
    /// </summary>
    public EditorKind? Editor { get; init; }

    public string RunningText => Running ?? string.Format(Strings.ConvertingTo, Label);
    public string DoneText => Done ?? string.Format(Strings.ConvertedTo, Label);
    public string ConfirmText => Confirm ?? Label.TrimEnd('…');

    public string Parameter(string name) =>
        Parameters.TryGetValue(name, out var value) ? value : throw new ConversionException(string.Format(Strings.NeedsAValueFor, Label, name));

    public double Number(string name, double min, double max)
    {
        var text = Parameter(name);
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
            throw new ConversionException(string.Format(Strings.MustBeANumberFromToNot, name, min, max, text));
        return value;
    }

    /// <summary>A number option's value, checked against its range.</summary>
    public double Number(string name) => OptionValues.TryNumber(Checked(name, OptionKind.Number), out var value) ? value : 0;

    public string Choice(string name) => Checked(name, OptionKind.Choice);

    public bool Flag(string name) => OptionValues.TryFlag(Checked(name, OptionKind.Toggle), out var value) && value;

    public string Text(string name) => Checked(name, OptionKind.Text);

    public Colour Colour(string name) => Core.Colour.TryParse(Checked(name, OptionKind.Colour), out var value) ? value : default;

    /// <summary>A file option's full path, or null when it was left empty.</summary>
    public string? File(string name) => Checked(name, OptionKind.File) is { Length: > 0 } path ? Path.GetFullPath(path) : null;

    /// <summary>A time range option in seconds; the end is null when it runs to the end.</summary>
    public (double Start, double? End) TimeRange(string name) =>
        OptionValues.TryRange(Checked(name, OptionKind.TimeRange), out var range) ? range : (0, null);

    /// <summary>A times option in seconds, in order.</summary>
    public IReadOnlyList<double> Times(string name) => OptionValues.TryTimes(Checked(name, OptionKind.Times), out var times) ? times : [];

    /// <summary>The selection in the chosen order, checking the order lists each of <paramref name="inputs"/> once.</summary>
    public IReadOnlyList<string> Ordered(string name, IReadOnlyList<string> inputs)
    {
        OptionValues.TryOrder(Checked(name, OptionKind.Order), out var order);
        if (order.Count == 0) return inputs;
        if (order.Count != inputs.Count)
            throw new ConversionException(string.Format(Strings.ListsFilesButAreSelected, OptionLabel(name), order.Count, inputs.Count));
        return order.Select(i => inputs[i]).ToList();
    }

    /// <summary>The value of an option, checked against its kind and limits, with an error that names the option.</summary>
    string Checked(string name, OptionKind kind)
    {
        var option = Options.FirstOrDefault(o => o.Name == name);
        if (option is null || option.Kind != kind)
            throw new InvalidOperationException($"{Key} has no {kind} option '{name}'.");
        var value = Parameter(name).Trim();
        if (option.Problem(value) is { } problem)
            throw new ConversionException($"{option.Label} {problem}" + (value.Length > 0 ? string.Format(Strings.Not, value) : "."));
        return value;
    }

    string OptionLabel(string name) => Options.FirstOrDefault(o => o.Name == name)?.Label ?? name;

    /// <summary>A copy that asks for these options, with their defaults added to the parameters.</summary>
    public Target WithOptions(params Option[] options)
    {
        var parameters = new Dictionary<string, string>(Parameters);
        foreach (var option in options)
            parameters[option.Name] = option.Default;
        return this with { Options = options, Parameters = parameters };
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

    /// <summary>Whether <see cref="TargetsFor"/> should be asked about folders too; most converters only take files.</summary>
    bool HandlesFolders => false;

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
    public int HeicQuality { get; set; } = 90;
    public int PdfRenderDpi { get; set; } = 300;

    /// <summary>How long one Office conversion may take before the Office app we started is shut down.</summary>
    public TimeSpan OfficeTimeout { get; set; } = TimeSpan.FromMinutes(3);
}

public sealed class JobContext : IDisposable
{
    readonly Dictionary<Type, IDisposable> _sessions = [];
    readonly List<string> _notes = [];

    public required Toolbox Tools { get; init; }
    public required ConverterOptions Options { get; init; }
    public CancellationToken Cancellation { get; init; }

    /// <summary>Progress of the current item, 0..1.</summary>
    public IProgress<double> Progress { get; set; } = new ActionProgress<double>(_ => { });

    /// <summary>Something worth knowing about a finished item, such as "2 pages read with OCR", shown with the result.</summary>
    public void Note(string note)
    {
        lock (_notes) _notes.Add(note);
    }

    /// <summary>The notes added since <paramref name="start"/>, an earlier <see cref="NoteCount"/>.</summary>
    internal IReadOnlyList<string> NotesSince(int start)
    {
        lock (_notes) return _notes.Skip(start).ToList();
    }

    internal int NoteCount
    {
        get { lock (_notes) return _notes.Count; }
    }

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

using System.Globalization;

namespace Tanjelo.Core;

/// <summary>What kind of value an <see cref="Option"/> holds, which decides how the options dialog asks for it.</summary>
public enum OptionKind
{
    /// <summary>A number from <see cref="Option.Min"/> to <see cref="Option.Max"/>.</summary>
    Number,

    /// <summary>One of <see cref="Option.Choices"/>, by value.</summary>
    Choice,

    /// <summary>"true" or "false".</summary>
    Toggle,

    Text,

    /// <summary>#RRGGBB, or #RRGGBBAA with transparency.</summary>
    Colour,

    /// <summary>The path of an existing file.</summary>
    File,

    /// <summary>A stretch of a video or audio file, such as "0:30-1:45"; empty for all of it.</summary>
    TimeRange,

    /// <summary>Moments in a video or audio file, such as "0:05, 1:12.5".</summary>
    Times,

    /// <summary>The selected files in a new order, by their 1-based place in the selection ("2,1,3"); empty keeps the selection order.</summary>
    Order,
}

public sealed record Choice(string Value, string Label);

/// <summary>Shows an option only while another option has one of these values, or while <see cref="Or"/> holds.</summary>
public sealed record Condition(string Name, params string[] Values)
{
    public Condition? Or { get; init; }

    public bool Holds(IReadOnlyDictionary<string, string> values) =>
        (values.TryGetValue(Name, out var value) && Values.Contains(value)) || Or?.Holds(values) == true;
}

/// <summary>
/// A setting a tool asks for before it runs: the options dialog shows a field for it, and tanj takes it as
/// <c>--name=value</c>. Values are text, like everything in <see cref="Target.Parameters"/>.
/// </summary>
public sealed record Option(string Name, string Label, OptionKind Kind, string Default)
{
    public double Min { get; init; }
    public double Max { get; init; } = double.MaxValue;

    /// <summary>For numbers: whether only whole numbers make sense, as with pixels.</summary>
    public bool Whole { get; init; }

    /// <summary>For numbers: shown after the field, such as "%" or "px".</summary>
    public string? Unit { get; init; }

    /// <summary>For numbers with a small range, such as a percentage: the dialog adds a slider.</summary>
    public bool Slider { get; init; }

    public IReadOnlyList<Choice> Choices { get; init; } = [];

    /// <summary>A short explanation, shown with the field.</summary>
    public string? Hint { get; init; }

    /// <summary>For files: the Open dialog's filter, e.g. "Images|*.png;*.jpg".</summary>
    public string? FileFilter { get; init; }

    /// <summary>For text, files and times: whether the value may be left empty.</summary>
    public bool Optional { get; init; }

    public Condition? ShownWhen { get; init; }

    /// <summary>Whether the dialog starts from the value used last time. Off for values that only fit one file, such as times in a video.</summary>
    public bool Remember { get; init; } = true;

    /// <summary>Whether this option applies given the other values; a hidden option keeps its value but isn't used.</summary>
    public bool IsShown(IReadOnlyDictionary<string, string> values) => ShownWhen?.Holds(values) ?? true;

    /// <summary>What's wrong with <paramref name="value"/>, phrased to follow the option's label ("must be…"), or null if it's fine.</summary>
    public string? Problem(string value)
    {
        value = value.Trim();
        switch (Kind)
        {
            case OptionKind.Number:
                var range = $"{OptionValues.Format(Min)} to {OptionValues.Format(Max)}";
                if (!OptionValues.TryNumber(value, out var number) || number < Min || number > Max)
                    return Whole ? $"must be a whole number from {range}" : $"must be a number from {range}";
                return Whole && number != Math.Floor(number) ? $"must be a whole number from {range}" : null;
            case OptionKind.Choice:
                return Choices.Any(c => c.Value == value) ? null : "must be one of " + string.Join(", ", Choices.Select(c => c.Value));
            case OptionKind.Toggle:
                return OptionValues.TryFlag(value, out _) ? null : "must be true or false";
            case OptionKind.Colour:
                return Colour.TryParse(value, out _) ? null : "must be a colour such as #FF8800";
            case OptionKind.File:
                if (value.Length == 0) return Optional ? null : "is needed";
                return File.Exists(value) ? null : "must be a file that exists";
            case OptionKind.TimeRange:
                return OptionValues.TryRange(value, out _) ? null : "must be a time range such as 0:30-1:45";
            case OptionKind.Times:
                if (!OptionValues.TryTimes(value, out var times)) return "must be times such as 0:05, 1:12.5";
                return times.Count == 0 && !Optional ? "needs at least one time" : null;
            case OptionKind.Order:
                return OptionValues.TryOrder(value, out _) ? null : "must list each file once, such as 2,1,3";
            default:
                return value.Length == 0 && !Optional ? "is needed" : null;
        }
    }
}

/// <summary>Reads and writes option values: numbers, flags, times and orders, always with invariant formatting.</summary>
public static class OptionValues
{
    public static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static bool TryFlag(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "on" or "1":
                value = true;
                return true;
            case "false" or "no" or "off" or "0":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    /// <summary>Seconds from "75", "1:15", "1:15.5" or "1:02:03".</summary>
    public static bool TryTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length > 3 || parts.Any(p => p.Trim().Length == 0)) return false;
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            if (!double.TryParse(parts[i].Trim(), last ? NumberStyles.AllowDecimalPoint : NumberStyles.None, CultureInfo.InvariantCulture, out var part))
                return false;
            if (i > 0 && part >= 60) return false; // "1:75" is a typo, not 2:15
            seconds = seconds * 60 + part;
        }
        return true;
    }

    /// <summary>"0:05", "1:15.5", "1:02:03": minutes and seconds, with hours only when needed.</summary>
    public static string FormatTime(double seconds)
    {
        var milliseconds = (long)Math.Round(Math.Max(0, seconds) * 1000);
        var whole = milliseconds / 1000;
        var text = whole >= 3600
            ? $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}"
            : $"{whole / 60}:{whole % 60:00}";
        return milliseconds % 1000 == 0 ? text : text + "." + (milliseconds % 1000).ToString("000", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    /// <summary>Times separated by commas, semicolons or spaces, sorted and without repeats.</summary>
    public static bool TryTimes(string text, out IReadOnlyList<double> times)
    {
        var list = new SortedSet<double>();
        times = [];
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryTime(part, out var time)) return false;
            list.Add(Math.Round(time, 3));
        }
        times = list.ToList();
        return true;
    }

    public static string FormatTimes(IEnumerable<double> times) => string.Join(", ", times.Order().Select(FormatTime));

    /// <summary>"0:30-1:45", "0:30-" (to the end), "-1:45" (from the start) or empty (all of it).</summary>
    public static bool TryRange(string text, out (double Start, double? End) range)
    {
        range = (0, null);
        text = text.Trim();
        if (text.Length == 0) return true;
        var dash = text.IndexOf('-');
        if (dash < 0) return false;
        var (from, to) = (text[..dash].Trim(), text[(dash + 1)..].Trim());
        double start = 0, end = 0;
        if (from.Length > 0 && !TryTime(from, out start)) return false;
        if (to.Length > 0 && (!TryTime(to, out end) || end <= start)) return false;
        range = (start, to.Length > 0 ? end : null);
        return true;
    }

    public static string FormatRange(double start, double? end) =>
        start <= 0 && end is null ? "" : $"{(start > 0 ? FormatTime(start) : "")}-{(end is { } e ? FormatTime(e) : "")}";

    /// <summary>"2,1,3" as 0-based places: [1, 0, 2]. Empty is the selection order, given as an empty list.</summary>
    public static bool TryOrder(string text, out IReadOnlyList<int> order)
    {
        order = [];
        var places = new List<int>();
        foreach (var part in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var place) || place < 1) return false;
            places.Add(place - 1);
        }
        if (places.Count > 0 && !places.Order().SequenceEqual(Enumerable.Range(0, places.Count))) return false;
        order = places;
        return true;
    }
}

/// <summary>A colour with transparency, written as #RRGGBB or #RRGGBBAA.</summary>
public readonly record struct Colour(byte R, byte G, byte B, byte A = 255)
{
    public static bool TryParse(string text, out Colour colour)
    {
        colour = default;
        var hex = text.Trim().TrimStart('#');
        if (text.Trim().Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            colour = new Colour(0, 0, 0, 0);
            return true;
        }
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return false;
        if (hex.Length == 6) value = value << 8 | 0xFF;
        colour = new Colour((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public override string ToString() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    /// <summary>For ffmpeg filters, which read "0xRRGGBBAA".</summary>
    public string ToFfmpeg() => $"0x{R:X2}{G:X2}{B:X2}{A:X2}";
}

public sealed record Fact(string Label, string Value);

/// <summary>What the options dialog shows next to the settings: a picture of the result and facts such as its size.</summary>
public sealed record Preview(byte[]? Image, IReadOnlyList<Fact> Facts)
{
    /// <summary>The input's length in seconds, so the dialog can offer a scrubber for times; 0 when it has none.</summary>
    public double Duration { get; init; }

    /// <summary>A box people can drag on the picture, such as a crop; null when there's none.</summary>
    public PreviewBox? Box { get; init; }
}

/// <summary>
/// A rectangle drawn over the preview picture that can be moved, resized or drawn anew. Sizes are in the input's own
/// pixels; the preview picture shows the whole input, scaled. Dragging writes the box into the four named options.
/// </summary>
/// <param name="SourceWidth">The input's width in pixels, which the picture's width stands for.</param>
public sealed record PreviewBox(int SourceWidth, int SourceHeight, int X, int Y, int Width, int Height)
{
    public required string XOption { get; init; }
    public required string YOption { get; init; }
    public required string WidthOption { get; init; }
    public required string HeightOption { get; init; }

    /// <summary>Width over height to keep while resizing; null for any shape.</summary>
    public double? Ratio { get; init; }

    /// <summary>Other options to set as a drag starts, so the tool goes on to use the dragged box, such as mode=free.</summary>
    public IReadOnlyDictionary<string, string> OnDrag { get; init; } = new Dictionary<string, string>();
}

/// <param name="MaxSize">The longest side of the picture, in pixels.</param>
public sealed record PreviewRequest(int MaxSize = 640)
{
    /// <summary>For tools that take times: the moment to show, in seconds.</summary>
    public double? At { get; init; }
}

/// <summary>
/// A converter or batch operation that can show what it's about to do, for the options dialog. It gets the same
/// target and values a run would, and should take a fraction of a run's time.
/// </summary>
public interface IPreviewer
{
    /// <param name="inputs">The selection; tools that work per file preview the first.</param>
    Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx);
}

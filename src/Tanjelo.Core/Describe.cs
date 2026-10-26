using System.Globalization;

namespace Tanjelo.Core;

/// <summary>Sizes, lengths and rates worded for people, as previews and messages show them.</summary>
public static class Describe
{
    /// <summary>Like File Explorer: 1 KB is 1024 bytes.</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0} KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(CultureInfo.InvariantCulture, "{0:0.#} MB", bytes / 1024.0 / 1024),
        _ => string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", bytes / 1024.0 / 1024 / 1024),
    };

    /// <summary>"0:05", "2:30" or "1:02:03", to the second.</summary>
    public static string Length(double seconds) => OptionValues.FormatTime(Math.Round(seconds));

    public static string Pixels(int width, int height) => $"{width} × {height}";

    public static string Rate(double bitsPerSecond) => string.Format(CultureInfo.InvariantCulture, "{0:N0} kbps", bitsPerSecond / 1000);

    /// <summary>"52.3676° N, 4.9041° E".</summary>
    public static string Place(double latitude, double longitude) => string.Create(CultureInfo.InvariantCulture,
        $"{Math.Abs(latitude):0.####}° {(latitude < 0 ? "S" : "N")}, {Math.Abs(longitude):0.####}° {(longitude < 0 ? "W" : "E")}");
}

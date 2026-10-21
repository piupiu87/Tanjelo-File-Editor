namespace Tanjelo.Core;

/// <summary>Outputs go beside the original and never overwrite anything: "photo.png", then "photo (2).png"...</summary>
public static class OutputPaths
{
    public static string Beside(string input, string ext) => Unique(DirectoryOf(input), Formats.BaseName(input), ext);

    /// <summary>For tools: "talk.mp4" with "muted" becomes "talk-muted.mp4", keeping the extension unless another is given.</summary>
    public static string Suffixed(string input, string suffix, string? ext = null) =>
        Unique(DirectoryOf(input), Formats.BaseName(input) + "-" + suffix, ext ?? Formats.Ext(input));

    /// <param name="ext">Extension without the dot; empty for a folder.</param>
    public static string Unique(string directory, string baseName, string ext)
    {
        var suffix = ext.Length == 0 ? "" : "." + ext;
        var candidate = Path.Combine(directory, baseName + suffix);
        for (var i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(directory, $"{baseName} ({i}){suffix}");
        return candidate;
    }

    public static string DirectoryOf(string input) =>
        Path.GetDirectoryName(Path.GetFullPath(input.TrimEnd('\\', '/')))!;

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A scratch folder in %TEMP%, deleted on dispose.</summary>
    public static TempDirectory CreateTemp() => new();

    public sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tanjelo-" + Guid.NewGuid().ToString("N")[..12]);

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => TryDelete(Path);
    }
}

using System.Globalization;
using Tanjelo.Core;
using Tanjelo.Core.Converters;

namespace Tanj;

/// <summary>
/// <c>--clipboard</c>: the files copied to the clipboard, or the picture on it, saved in a scratch folder so the engine
/// can read it. The picture's results are moved into the current folder, since running tanj there is asking for them
/// there, and the scratch folder goes when tanj ends. The clipboard itself is left as it is.
/// </summary>
sealed class ClipboardRun(IClipboard clipboard) : IDisposable
{
    public const string Switch = "--clipboard";

    OutputPaths.TempDirectory? _scratch;

    /// <summary>The picture, when the input is one rather than copied files.</summary>
    public ClipboardPicture? Picture { get; private set; }

    /// <summary>The scratch folder the picture was saved in; null for copied files.</summary>
    public string? Scratch => _scratch?.Path;

    /// <summary>The arguments without <c>--clipboard</c>, wherever it was, so it isn't read as a tool's setting; and whether it was there.</summary>
    public static (string[] Others, bool FromClipboard) Take(string[] args) =>
        (args.Where(a => a != Switch).ToArray(), args.Contains(Switch));

    /// <summary>The copied files, or the picture saved in the scratch folder; null, with the reason, when there's neither.</summary>
    public IReadOnlyList<string>? Read(out string? problem)
    {
        problem = null;
        var content = ClipboardPicture.Choose(clipboard.Read());
        if (content.Files.Count > 0) return content.Files.Select(Path.GetFullPath).ToList();
        if (content.Picture is not { } picture)
        {
            problem = content.Problem ?? Strings.NoPictureOrFilesOnTheClipboard;
            return null;
        }
        _scratch = OutputPaths.CreateTemp();
        var file = Path.Combine(_scratch.Path, string.Format(CultureInfo.InvariantCulture, Strings.ScreenshotFileName, DateTime.Now) + ".png");
        File.WriteAllBytes(file, picture.Png);
        Picture = picture;
        return [file];
    }

    /// <summary>
    /// Moves what was written in <paramref name="scratch"/> into <paramref name="destination"/>, under names not taken
    /// there, and returns where each output is now. A folder of results moves whole; outputs elsewhere, such as Save
    /// to…'s copy, stay where they are.
    /// </summary>
    public static List<string> MoveOut(IReadOnlyList<string> outputs, string scratch, string destination)
    {
        var root = Path.GetFullPath(scratch).TrimEnd('\\') + "\\";
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var output in outputs.Select(Path.GetFullPath))
        {
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(output);
                continue;
            }
            // What sits right in the scratch folder: the output, or the folder it was written in.
            var top = root + output[root.Length..].Split('\\')[0];
            if (!moved.TryGetValue(top, out var to))
                moved[top] = to = MoveToFolderAction.MoveInto(top, destination);
            result.Add(to + output[top.Length..]);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Dispose() => _scratch?.Dispose();
}

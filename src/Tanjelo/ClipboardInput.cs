using Tanjelo.Core;

namespace Tanjelo;

/// <summary>
/// What the menu is for: the selected files, or, with nothing selected, the files copied to the clipboard or the picture
/// on it. <see cref="SaveHere"/> is the folder in front, for the picture's Save in "Folder".
/// </summary>
sealed record ClipboardInput(IReadOnlyList<string> Files, ClipboardPicture? Picture, string? SaveHere, string? Problem)
{
    public bool IsEmpty => Files.Count == 0 && Picture is null;

    /// <param name="selection">What's selected in Explorer or on the desktop; it wins over the clipboard.</param>
    /// <param name="folderInFront">The folder of the Explorer window or the desktop in front, if it's a real one.</param>
    /// <param name="data">What's on the clipboard; only read when nothing is selected.</param>
    public static ClipboardInput Choose(IReadOnlyList<string> selection, string? folderInFront, ClipboardData? data)
    {
        if (selection.Count > 0) return new(selection, null, null, null);
        var content = data is null ? ClipboardContent.Nothing : ClipboardPicture.Choose(data);
        if (content.Files.Count > 0) return new(content.Files, null, null, null);
        return new([], content.Picture, content.Picture is null ? null : folderInFront, content.Problem);
    }
}

using Microsoft.Win32;
using Tanjelo.Core;
using Tanjelo.Core.Converters;

namespace Tanjelo;

/// <summary>
/// Save as… and Save to… for files on the shelf and results on a card: they copy, and the file Tanjelo holds stays where
/// it is until it's taken off the shelf.
/// </summary>
static class SaveCopies
{
    /// <summary>Where to save a copy of one file, asked with Windows' save dialog, starting in Pictures; null when canceled.</summary>
    public static string? AskFileName(string file)
    {
        var ext = Path.GetExtension(file);
        var dialog = new SaveFileDialog
        {
            Title = Strings.SaveACopy,
            FileName = Path.GetFileName(file),
            DefaultExt = ext,
            Filter = string.Format(Strings.FileOfThisType, ext.TrimStart('.').ToUpperInvariant(), ext),
            // Windows starts where a copy was saved last time, and in Pictures the first time.
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            ClientGuid = new Guid("8C2D6E1F-3B4A-4F5E-9A7C-2E1D0B3C4A5F"),
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>Which folder to save copies in, asked with Windows' folder dialog; null when canceled.</summary>
    public static string? AskFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Tanjelo.Core.Strings.SaveToWhichFolder,
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            ClientGuid = new Guid("8C2D6E1F-3B4A-4F5E-9A7C-2E1D0B3C4A60"),
            Multiselect = false,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    /// <summary>
    /// Asks where, then copies: one file under a name chosen in the save dialog (replacing what's there, which the dialog
    /// asked about), anything else into a chosen folder, where names already taken get " (2)". Returns the copies, or
    /// none when the dialog was canceled.
    /// </summary>
    public static IReadOnlyList<string> Save(IReadOnlyList<string> paths, Func<string, string?> askFileName, Func<string?> askFolder)
    {
        if (paths is [var one] && File.Exists(one))
        {
            if (askFileName(one) is not { } chosen) return [];
            if (!string.Equals(Path.GetFullPath(chosen), Path.GetFullPath(one), StringComparison.OrdinalIgnoreCase))
                File.Copy(one, chosen, overwrite: true);
            return [chosen];
        }
        if (paths.Count == 0 || askFolder() is not { } folder) return [];
        return paths.Select(p => SaveToFolderAction.CopyInto(p, folder)).ToList();
    }
}

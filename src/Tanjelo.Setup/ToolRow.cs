using System.ComponentModel;
using System.Windows;

namespace Tanjelo.Setup;

/// <summary>
/// One row of the Optional tools page: the tool, what it's for in Tanjelo, its size and notes, and whether it's ticked.
/// A tool already there shows as installed; without winget, the rows can't be ticked and link to each tool's site.
/// </summary>
sealed class ToolRow(OptionalTool tool, bool installed, bool hasWinget) : INotifyPropertyChanged
{
    bool _ticked = !installed && (hasWinget || tool.StorePages.Length > 0) && tool.TickedWhenMissing;

    public event PropertyChangedEventHandler? PropertyChanged;

    public OptionalTool Tool => tool;
    public string Name => tool.Name;
    public string Purpose => tool.Purpose;
    public bool Installed => installed;

    /// <summary>Whether setup can get it: with winget, or from the Store.</summary>
    public bool CanTick => !installed && (hasWinget || tool.StorePages.Length > 0);

    public bool Ticked
    {
        get => _ticked;
        set
        {
            _ticked = value && CanTick;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ticked)));
        }
    }

    public string Details => installed ? Strings.Installed : $"{tool.Size} · {tool.Note}";

    public Visibility LicenceVisibility => installed ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The way to get it when setup can't.</summary>
    public Visibility SiteVisibility => CanTick || installed ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>A tool that wasn't installed, with why, for the Done page.</summary>
sealed record ToolProblem(string Name, string Reason, string Site);

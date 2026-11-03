using System.ComponentModel;
using System.Windows;

namespace Tanjelo.Setup;

enum StepState { Waiting, Working, Done, Failed, Skipped }

/// <summary>
/// One line of the Installing page: a step, its state as a glyph and in words for a screen reader, and how much of the
/// whole bar it's worth.
/// </summary>
sealed class StepRow(string label, double weight) : INotifyPropertyChanged
{
    StepState _state;
    string _detail = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; } = label;

    /// <summary>Its share of the progress bar, against the other steps'.</summary>
    public double Weight { get; } = weight;

    /// <summary>How far through it is, 0 to 1, while it's working.</summary>
    public double Fraction { get; set; }

    public StepState State
    {
        get => _state;
        set
        {
            _state = value;
            Changed(nameof(State));
            Changed(nameof(Glyph));
            Changed(nameof(Spoken));
        }
    }

    /// <summary>A line under the step, such as why it failed.</summary>
    public string Detail
    {
        get => _detail;
        set
        {
            _detail = value;
            Changed(nameof(Detail));
            Changed(nameof(DetailVisibility));
            Changed(nameof(Spoken));
        }
    }

    public Visibility DetailVisibility => _detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Segoe's glyphs: a ring to do, an arrow working, a tick done, a cross failed, a dash skipped.</summary>
    public string Glyph => _state switch
    {
        StepState.Working => "",
        StepState.Done => "",
        StepState.Failed => "",
        StepState.Skipped => "",
        _ => "",
    };

    public string Spoken => string.Format(_state switch
    {
        StepState.Working => Strings.StepWorking,
        StepState.Done => Strings.StepDone,
        StepState.Failed => Strings.StepFailed,
        StepState.Skipped => Strings.StepSkipped,
        _ => Strings.StepWaiting,
    }, Label) + (_detail.Length == 0 ? "" : ". " + _detail);

    /// <summary>How much of the bar it fills: all of it once it's over, part of it while it works.</summary>
    public double Filled => _state is StepState.Done or StepState.Failed or StepState.Skipped ? Weight : _state == StepState.Working ? Weight * Fraction : 0;

    public override string ToString() => Spoken;

    void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

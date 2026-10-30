using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Tanjelo.Core;

namespace Tanjelo;

/// <summary>The bottom-right card that shows a conversion's progress and then its result.</summary>
public partial class ProgressToast : Window
{
    readonly DispatcherTimer _autoClose = new();
    bool _finished;
    Action? _primary, _secondary;

    /// <summary>The user pressed Cancel (or closed the card) while work was running.</summary>
    public event Action? CancelRequested;

    public ProgressToast()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.StyleWindow(new WindowInteropHelper(this).Handle, Theme.IsDark);
        CloseButton.Click += (_, _) =>
        {
            if (!_finished) CancelRequested?.Invoke();
            Close();
        };
        PrimaryButton.Click += (_, _) => _primary?.Invoke();
        SecondaryButton.Click += (_, _) => _secondary?.Invoke();
        _autoClose.Tick += (_, _) =>
        {
            if (IsMouseOver) return; // keep it while the user is reading or reaching for a button
            _autoClose.Stop();
            Close();
        };
    }

    public void ShowRunning(Target target, int count)
    {
        Heading.Text = target.RunningText;
        Detail.Text = count == 1 ? "" : string.Format(Strings.Items, count);
        Speech.Announce(Card, Detail.Text.Length > 0 ? $"{Heading.Text} {Detail.Text}" : Heading.Text);
        SetButtons(primary: null, secondary: (Strings.Cancel, () => CancelRequested?.Invoke()));
    }

    public void Report(BatchProgress progress)
    {
        if (_finished) return; // progress callbacks are posted, so stragglers can arrive after the result
        Detail.Text = progress.Count > 1 ? string.Format(Strings.Of4, progress.Current, progress.Index + 1, progress.Count) : progress.Current;
        Bar.IsIndeterminate = double.IsNaN(progress.Fraction);
        if (!Bar.IsIndeterminate) Bar.Value = progress.Fraction;
    }

    public void ShowResult(Target target, IReadOnlyList<string> inputs, BatchResult result)
    {
        var outputs = result.Outputs;
        var failures = result.Failures;
        var inputCount = inputs.Count;

        if (result.Canceled)
        {
            Finish(Mood.Info, Strings.Canceled, outputs.Count > 0 ? string.Format(Strings.FinishedBeforeYouCanceled, Describe.Count(outputs.Count, Tanjelo.Core.Strings.OneFile, Tanjelo.Core.Strings.NFiles)) : Strings.NothingWasChanged,
                outputs.Count > 0 ? Reveal(outputs) : null, null, TimeSpan.FromSeconds(4));
        }
        else if (failures.Count == 0 && outputs.Count == 0 && target.Kind != TargetKind.Action)
        {
            Finish(Mood.Info, Strings.NothingToDo, string.Format(Strings.Already, target.Label), null, null, TimeSpan.FromSeconds(4));
        }
        else if (failures.Count == 0 && outputs.Count == 0)
        {
            // Actions such as "Copy path" work on the selection itself and make no files.
            var detail = inputCount == 1 ? Path.GetFileName(inputs[0].TrimEnd('\\')) : Describe.Count(inputCount, Tanjelo.Core.Strings.OneItem, Tanjelo.Core.Strings.NItems);
            Finish(Mood.Success, target.DoneText, detail, null, null, TimeSpan.FromSeconds(4));
        }
        else if (failures.Count == 0)
        {
            var detail = outputs.Count == 1 ? Path.GetFileName(outputs[0]) : Describe.Count(outputs.Count, Tanjelo.Core.Strings.OneFile, Tanjelo.Core.Strings.NFiles);
            if (result.Skipped.Count > 0) detail += string.Format(Strings.Already2, result.Skipped.Count, target.Label);
            foreach (var note in result.Notes) detail += " · " + note;
            (string, Action)? open = outputs.Count == 1 ? (Strings.Open, () => Open(outputs[0])) : null;
            Finish(Mood.Success, target.DoneText, detail, Reveal(outputs), open, TimeSpan.FromSeconds(7));
        }
        else
        {
            var first = failures[0];
            var what = inputCount == 1 ? Path.GetFileName(first.Input) : Describe.Count(inputCount, Tanjelo.Core.Strings.OneItem, Tanjelo.Core.Strings.NItems);
            var heading = failures.Count == inputCount
                ? target.Kind == TargetKind.Convert ? string.Format(Strings.CouldnTConvert, what) : string.Format(Strings.DidnTWorkOn, target.Label.TrimEnd('…'), what)
                : string.Format(Strings.OfFailed, failures.Count, inputCount);
            var detail = inputCount == 1 ? first.Message : $"{Path.GetFileName(first.Input)}: {first.Message}";
            var report = string.Join(Environment.NewLine, failures.Select(f => $"{f.Input}: {f.Message}"));
            (string, Action) copy = (Strings.CopyDetails, () => Clipboard.SetText(report));
            Finish(Mood.Failure, heading, detail, outputs.Count > 0 ? Reveal(outputs) : null, copy, autoClose: null);
        }
    }

    /// <summary>A short notice that closes itself, e.g. "Select some files first".</summary>
    public void ShowMessage(string heading, string detail)
    {
        Finish(Mood.Info, heading, detail, null, null, TimeSpan.FromSeconds(5));
    }

    public void ShowError(string heading, string detail)
    {
        Finish(Mood.Failure, heading, detail, null, null, autoClose: null);
    }

    enum Mood { Info, Success, Failure }

    void Finish(Mood mood, string heading, string detail, (string, Action)? primary, (string, Action)? secondary, TimeSpan? autoClose)
    {
        _finished = true;
        Heading.Text = heading;
        Detail.Text = detail;
        Bar.Visibility = Visibility.Collapsed;
        Speech.Announce(Card, $"{heading}. {detail}");
        (Badge.Fill, Glyph.Foreground, Glyph.Text) = mood switch
        {
            Mood.Success => (Brush("SuccessSoft"), Brush("Success"), ""),
            Mood.Failure => (Brush("DangerSoft"), Brush("Danger"), ""),
            _ => (Brush("AccentSoft"), Brush("Accent"), ""),
        };
        SetButtons(primary, secondary);
        if (autoClose is { } delay)
        {
            _autoClose.Interval = delay;
            _autoClose.Start();
        }
    }

    void SetButtons((string Label, Action Action)? primary, (string Label, Action Action)? secondary)
    {
        _primary = primary?.Action;
        _secondary = secondary?.Action;
        PrimaryButton.Content = primary?.Label;
        PrimaryButton.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Content = secondary?.Label;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        Actions.Visibility = primary is null && secondary is null ? Visibility.Collapsed : Visibility.Visible;
    }

    (string, Action) Reveal(IReadOnlyList<string> outputs) => (Strings.ShowInFolder, () =>
    {
        Native.RevealInExplorer(outputs);
        Close();
    });

    void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        Close();
    }


    System.Windows.Media.Brush Brush(string key) => (System.Windows.Media.Brush)FindResource(key);
}

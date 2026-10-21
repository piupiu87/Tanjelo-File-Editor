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
        Heading.Text = target.Key switch
        {
            "extract" => "Extracting…",
            "compress-zip" or "compress-7z" => "Compressing…",
            "combine-pdf" => "Combining into a PDF…",
            _ => $"Converting to {target.Label}…",
        };
        Detail.Text = count == 1 ? "" : $"{count} items";
        SetButtons(primary: null, secondary: ("Cancel", () => CancelRequested?.Invoke()));
    }

    public void Report(BatchProgress progress)
    {
        if (_finished) return; // progress callbacks are posted, so stragglers can arrive after the result
        Detail.Text = progress.Count > 1 ? $"{progress.Current} · {progress.Index + 1} of {progress.Count}" : progress.Current;
        Bar.IsIndeterminate = double.IsNaN(progress.Fraction);
        if (!Bar.IsIndeterminate) Bar.Value = progress.Fraction;
    }

    public void ShowResult(Target target, int inputCount, BatchResult result)
    {
        var outputs = result.Outputs;
        var failures = result.Failures;

        if (result.Canceled)
        {
            Finish(Mood.Info, "Canceled", outputs.Count > 0 ? $"{Count(outputs.Count, "file")} finished before you canceled." : "Nothing was changed.",
                outputs.Count > 0 ? Reveal(outputs) : null, null, TimeSpan.FromSeconds(4));
        }
        else if (failures.Count == 0 && outputs.Count == 0)
        {
            Finish(Mood.Info, "Nothing to do", $"Already {target.Label}.", null, null, TimeSpan.FromSeconds(4));
        }
        else if (failures.Count == 0)
        {
            var heading = target.Key switch
            {
                "extract" => "Extracted",
                "compress-zip" or "compress-7z" => $"Compressed to {target.Label}",
                "combine-pdf" => "Combined into one PDF",
                _ => $"Converted to {target.Label}",
            };
            var detail = outputs.Count == 1 ? Path.GetFileName(outputs[0]) : Count(outputs.Count, "file");
            if (result.Skipped.Count > 0) detail += $" · {result.Skipped.Count} already {target.Label}";
            (string, Action)? open = outputs.Count == 1 ? ("Open", () => Open(outputs[0])) : null;
            Finish(Mood.Success, heading, detail, Reveal(outputs), open, TimeSpan.FromSeconds(7));
        }
        else
        {
            var first = failures[0];
            var heading = failures.Count == inputCount
                ? inputCount == 1 ? $"Couldn't convert {Path.GetFileName(first.Input)}" : $"Couldn't convert {Count(inputCount, "item")}"
                : $"{failures.Count} of {inputCount} failed";
            var detail = inputCount == 1 ? first.Message : $"{Path.GetFileName(first.Input)}: {first.Message}";
            var report = string.Join(Environment.NewLine, failures.Select(f => $"{f.Input}: {f.Message}"));
            (string, Action) copy = ("Copy details", () => Clipboard.SetText(report));
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

    (string, Action) Reveal(IReadOnlyList<string> outputs) => ("Show in folder", () =>
    {
        Native.RevealInExplorer(outputs);
        Close();
    });

    void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        Close();
    }

    static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    System.Windows.Media.Brush Brush(string key) => (System.Windows.Media.Brush)FindResource(key);
}

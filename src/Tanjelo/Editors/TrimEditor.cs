using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tanjelo.Core;
using Tanjelo.Core.Converters;

namespace Tanjelo.Editors;

/// <summary>What the trim editor saves: where the part kept starts and ends, in seconds; in video, both between frames.</summary>
sealed record TrimState(double Start, double End);

/// <summary>
/// Trim: drag the start and end handles on the timeline, type the times, or press I and O at the playhead. In video
/// the cuts sit between frames, and the panel says whether the copy will be lossless, which needs both cuts on
/// keyframes (the marks under the thumbnails); Snap to keyframes keeps dragged handles there. Sound is cut to the millisecond.
/// </summary>
sealed class TrimEditor : TimelineEditor<TrimState>
{
    readonly StackPanel _panel = CreatePanel();

    readonly TextBox _from, _to;
    readonly TextBlock _length = Panels.Hint("", 8);
    readonly TextBlock _saving = Panels.Hint("", 0);
    readonly TextBlock _kept;
    readonly CheckBox _snap;

    enum Handle { None, Start, End }
    Handle _dragging;
    bool _committed;

    public TrimEditor(string file, Target target, Toolbox tools) : base(file, target, tools)
    {
        _from = Field(Strings.Start, Strings.WhereThePartKeptStartsEnterTo);
        _to = Field(Strings.End, Strings.WhereThePartKeptEndsEnterTo);
        _panel.Children.Add(Panels.Heading(Strings.Keep));
        _panel.Children.Add(Row(Strings.From, _from, Panels.Button("", Strings.StartHere, Strings.StartAtThePlayheadI, () => SetStart(Playhead))));
        _panel.Children.Add(Row(Strings.To, _to, Panels.Button("", Strings.EndHere, Video ? Strings.EndAfterTheFrameAtThePlayhead : Strings.EndAtThePlayheadO, SetEndHere)));
        _panel.Children.Add(_length);
        _snap = Panels.Themed(new CheckBox
        {
            Content = Strings.SnapToKeyframes,
            Margin = new Thickness(0, 10, 0, 0),
            ToolTip = Strings.DraggedHandlesStopAtKeyframesSoThe,
        }, FrameworkElement.StyleProperty, "Check");
        if (Video)
        {
            _panel.Children.Add(Panels.Heading(Strings.Saving, 20));
            _panel.Children.Add(_saving);
            _panel.Children.Add(_snap);
        }
        _panel.Children.Add(Panels.Heading(Strings.Keys, 20));
        _panel.Children.Add(Panels.Keys([(Strings.IO, Video ? Strings.StartAtThePlayheadOrEndAfter : Strings.StartOrEndAtThePlayhead), .. TimelineKeys()]));
        _panel.Children.Add(Panels.Hint(Strings.DragTheHandlesAtEitherEndOf, 4));

        _kept = Panels.Themed(new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, "TextSecondary");
        AddToTransport(_kept);
    }

    TextBox Field(string name, string tip)
    {
        var box = Panels.Themed(new TextBox { Width = 112, ToolTip = tip, VerticalContentAlignment = VerticalAlignment.Center }, FrameworkElement.StyleProperty, "FieldBox");
        System.Windows.Automation.AutomationProperties.SetName(box, name);
        System.Windows.Automation.AutomationProperties.SetHelpText(box, tip);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Typed(box);
            else if (e.Key == Key.Escape && Started) Show(State);
            else return;
            e.Handled = true;
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (Started) Show(State);
        };
        return box;
    }

    /// <summary>The settings beside the timeline; its rows share the width of their labels.</summary>
    static StackPanel CreatePanel()
    {
        var panel = new StackPanel();
        Grid.SetIsSharedSizeScope(panel, true);
        return panel;
    }

    static Grid Row(string label, TextBox box, Button button)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        // As wide as the longer label, in every row, so the boxes line up whatever the language.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 40, SharedSizeGroup = "TrimLabel" });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(box, 1);
        button.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(button, 2);
        row.Children.Add(text);
        row.Children.Add(box);
        row.Children.Add(button);
        return row;
    }

    public override FrameworkElement Panel => _panel;

    internal TrimState Current => State;
    internal TextBox FromBox => _from;
    internal TextBox ToBox => _to;
    internal CheckBox SnapBox => _snap;
    internal string Saving => _saving.Text;
    internal string Kept => _kept.Text;

    protected override TrimState Initial() => new(0, Duration);

    /// <summary>Keeping all of it would make the same file, so it doesn't count as a change.</summary>
    public override bool Changed => base.Changed && !KeepsAll(State);

    bool KeepsAll(TrimState state) => state.Start <= 0.0005 && state.End >= Duration - 0.0005;

    /// <summary>Whether both cuts are on keyframes, so the copy can be made without re-encoding; null while that isn't known.</summary>
    internal bool? Lossless
    {
        get
        {
            if (Frames is not { Estimated: false } frames) return null;
            return frames.IsKey(frames.At(State.Start)) && (State.End >= Duration - 0.0005 || frames.IsKey(frames.At(State.End)));
        }
    }

    protected override string EditorStatus => !Started ? "" : !Video ? "" : Lossless switch
    {
        null => "",
        true => Strings.WillBeSavedWithoutReEncoding,
        false => Strings.WillBeReEncodedSoTheCuts,
    };

    public override Target Result()
    {
        if (KeepsAll(State)) throw new ConversionException(Strings.MoveTheStartOrTheEndFirst);
        var parameters = new Dictionary<string, string>
        {
            ["range"] = OptionValues.FormatRange(State.Start, State.End >= Duration - 0.0005 ? null : State.End),
        };
        if (Video) parameters["mode"] = "auto";
        return Entry.WithParameters(parameters);
    }

    protected override void Show(TrimState state)
    {
        if (!_from.IsKeyboardFocusWithin) _from.Text = Timecode.Format(state.Start);
        if (!_to.IsKeyboardFocusWithin) _to.Text = Timecode.Format(state.End);
        var length = state.End - state.Start;
        var frames = Frames is { } index ? index.At(state.End - 1e-6) - index.At(state.Start) + 1 : 0;
        _kept.Text = Video ? string.Format(Strings.Keeps, Timecode.Format(length), Describe.Count(frames, Tanjelo.Core.Strings.OneFrame, Tanjelo.Core.Strings.NFrames)) : string.Format(Strings.Keeps2, Timecode.Format(length));
        _length.Text = string.Format(Strings.Of2, Timecode.Format(length), Timecode.Format(Duration)) + (Video ? $", {Describe.Count(frames, Tanjelo.Core.Strings.OneFrame, Tanjelo.Core.Strings.NFrames)}" : "");
        _saving.Text = Lossless switch
        {
            null => Strings.FindingTheKeyframes,
            true => Strings.BothCutsAreOnKeyframesSoThe,
            false => Strings.ThePartKeptIsReEncodedSo,
        };
        Timeline.InvalidateVisual();
    }

    protected override void FramesLoaded()
    {
        // Cuts placed on the estimated frames move to the real ones.
        if (Frames is null || KeepsAll(State)) Show(State);
        else Amend(new TrimState(SnapCut(State.Start), State.End >= Duration - 0.0005 ? Duration : SnapCut(State.End)));
    }

    // ---- Setting the cuts

    void SetStart(double seconds)
    {
        var start = SnapCut(seconds);
        if (start >= Duration - 0.0005) return;
        var end = start < State.End ? State.End : Duration;
        if (new TrimState(start, end) != State) Commit(new TrimState(start, end));
    }

    /// <summary>In video, the frame at the playhead is the last one kept; in sound, the part ends at the playhead.</summary>
    void SetEndHere() => SetEnd(Frames is { } frames ? frames.Stop(Frame) : Playhead);

    void SetEnd(double seconds)
    {
        var end = SnapCut(seconds);
        if (end <= 0.0005) return;
        var start = end > State.Start ? State.Start : 0;
        if (new TrimState(start, end) != State) Commit(new TrimState(start, end));
    }

    void Typed(TextBox box)
    {
        if (!Started || !Timecode.TryParse(box.Text, out var seconds))
        {
            if (Started) Show(State);
            return;
        }
        if (box == _from) SetStart(seconds);
        else SetEnd(seconds);
        Show(State);
    }

    protected override bool OnTimelineKey(Key key, ModifierKeys modifiers, bool repeat)
    {
        if (modifiers != ModifierKeys.None || !Started) return false;
        switch (key)
        {
            case Key.I:
                SetStart(Playhead);
                return true;
            case Key.O:
                SetEndHere();
                return true;
            default:
                return false;
        }
    }

    // ---- Dragging the handles

    /// <summary>A cut moved to the nearest keyframe, or to the start or end, for Snap to keyframes.</summary>
    double NearestKey(double seconds)
    {
        if (Frames is not { Estimated: false } frames || frames.Keyframes.Count == 0) return SnapCut(seconds);
        var best = frames.Keyframes.Select(frames.Start).Append(Duration).MinBy(t => Math.Abs(t - seconds));
        return best;
    }

    /// <summary>The shortest part there can be: a frame, or a hundredth of a second of sound.</summary>
    double Shortest => Frames is { } frames ? frames.Stop(frames.At(State.Start)) - frames.Start(frames.At(State.Start)) : 0.01;

    public override bool Down(TimelinePoint at, ModifierKeys modifiers)
    {
        if (!Started || at.Lane is not (TimelineLane.Pictures or TimelineLane.Sound)) return false;
        double toStart = Math.Abs(at.Time - State.Start), toEnd = Math.Abs(at.Time - State.End);
        _dragging = Math.Min(toStart, toEnd) > at.Grip ? Handle.None : toStart <= toEnd ? Handle.Start : Handle.End;
        _committed = false;
        return _dragging != Handle.None;
    }

    public override void Move(TimelinePoint at, ModifierKeys modifiers)
    {
        if (_dragging == Handle.None) return;
        var snap = _snap.IsChecked == true && Video;
        var time = snap ? NearestKey(at.Time) : SnapCut(at.Time);
        TrimState next;
        if (_dragging == Handle.Start)
        {
            var latest = Frames is { } frames ? frames.Start(frames.At(State.End - 1e-6)) : State.End - 0.01;
            next = State with { Start = Math.Min(time, latest) };
        }
        else
        {
            var earliest = Frames is { } frames ? frames.Stop(frames.At(State.Start)) : State.Start + 0.01;
            next = State with { End = Math.Max(time, earliest) };
        }
        if (next == State) return;
        if (_committed) Amend(next);
        else Commit(next);
        _committed = true;
        // Show the frame at the cut: the first one kept, or the last.
        MoveTo(_dragging == Handle.Start ? next.Start : Frames is not null ? next.End - 1e-6 : next.End);
    }

    public override void Up(TimelinePoint at) => _dragging = Handle.None;

    public override Cursor? CursorAt(TimelinePoint at) =>
        Started && at.Lane is TimelineLane.Pictures or TimelineLane.Sound
        && Math.Min(Math.Abs(at.Time - State.Start), Math.Abs(at.Time - State.End)) <= at.Grip ? Cursors.SizeWE : null;

    public override void Draw(DrawingContext dc, TimelineView view)
    {
        if (!Started) return;
        var (top, _) = view.ShowPictures ? view.Band(TimelineLane.Pictures) : view.Band(TimelineLane.Sound);
        var (soundTop, soundHeight) = view.Band(TimelineLane.Sound);
        var bottom = soundTop + soundHeight;
        double x0 = view.XAt(State.Start), x1 = view.XAt(State.End);
        // What's cut away is faded.
        dc.PushOpacity(0.72);
        dc.DrawRectangle(view.Lane, null, new Rect(new Point(Math.Min(x0, -1), top), new Point(Math.Max(-1, x0), bottom)));
        dc.DrawRectangle(view.Lane, null, new Rect(new Point(Math.Max(x1, 0), top), new Point(Math.Max(x1, view.ActualWidth + 1), bottom)));
        dc.Pop();
        var edge = new Pen(view.Accent, 2);
        dc.DrawLine(edge, new Point(x0, top - 1), new Point(x1, top - 1));
        dc.DrawLine(edge, new Point(x0, bottom + 1), new Point(x1, bottom + 1));
        foreach (var (x, start) in new[] { (x0, true), (x1, false) })
        {
            dc.DrawRectangle(view.Accent, null, new Rect(x - (start ? 0 : 3), top - 2, 3, bottom - top + 4));
            // A grip on the inside of each handle.
            var grip = new Rect(start ? x : x - 10, top + (bottom - top) / 2 - 14, 10, 28);
            dc.DrawRoundedRectangle(view.Accent, null, grip, 3, 3);
            var line = new Pen(Brushes.White, 1);
            dc.DrawLine(line, new Point(grip.Left + 3.5, grip.Top + 8), new Point(grip.Left + 3.5, grip.Bottom - 8));
            dc.DrawLine(line, new Point(grip.Left + 6.5, grip.Top + 8), new Point(grip.Left + 6.5, grip.Bottom - 8));
        }
    }
}

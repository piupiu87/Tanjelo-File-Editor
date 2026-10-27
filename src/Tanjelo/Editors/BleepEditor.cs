using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tanjelo.Core;
using Tanjelo.Core.Converters;

namespace Tanjelo.Editors;

/// <summary>What the bleep editor saves: the stretches to cover, in seconds, and what covers them.</summary>
sealed record BleepState(ImmutableList<(double Start, double End)> Ranges, string Sound, int Pitch);

/// <summary>
/// Bleep: drag on the shape of the sound to mark a stretch, then move it or pull its ends; each is covered with a tone
/// or silence. After each change the sound is made again in the background and plays with the bleeps in.
/// </summary>
sealed class BleepEditor : TimelineEditor<BleepState>
{
    /// <summary>The shortest stretch a drag makes, in seconds; shorter is a click, which moves the playhead.</summary>
    const double Shortest = 0.02;

    readonly StackPanel _panel = new();
    readonly TextBlock _count = Panels.Hint("", 8);
    readonly TextBlock _hearing = Panels.Hint("", 8);
    readonly StackPanel _pitchRow = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly Slider _pitch;
    readonly TextBlock _pitchValue;
    readonly Toolbox _tools;
    Action<string> _showSound = _ => { };
    int? _selected;

    // A drag on the sound: what it does, from where, and whether it has changed anything yet.
    enum Grab { None, Draw, Move, Start, End }
    Grab _grab;
    double _from;
    (double Start, double End) _original;
    bool _committed;

    // The sound playing: made again after each change, the newest wins.
    int _version;

    public BleepEditor(string file, Target target, Toolbox tools) : base(file, target, tools)
    {
        _tools = tools;
        _panel.Children.Add(Panels.Heading("Cover with"));
        var (chips, showSound) = Panels.Chips(BleepTool.Sounds, "tone", sound => Commit(State with { Sound = sound }));
        _showSound = showSound;
        _panel.Children.Add(chips);
        _pitch = Panels.Themed(new Slider { Minimum = 200, Maximum = 4000, SmallChange = 50, LargeChange = 250, IsSnapToTickEnabled = true, TickFrequency = 50 },
            FrameworkElement.StyleProperty, "ThinSlider");
        _pitchValue = Panels.Themed(new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right }, TextBlock.ForegroundProperty, "TextSecondary");
        var top = new Grid();
        top.Children.Add(new TextBlock { Text = "Pitch", FontSize = 12.5 });
        top.Children.Add(_pitchValue);
        _pitchRow.Children.Add(top);
        _pitchRow.Children.Add(_pitch);
        // A drag along the slider is one step to undo.
        var dragging = false;
        _pitch.PreviewMouseLeftButtonUp += (_, _) => dragging = false;
        _pitch.ValueChanged += (_, e) =>
        {
            _pitchValue.Text = $"{e.NewValue:0} Hz";
            if (!Started || (int)e.NewValue == State.Pitch) return;
            var next = State with { Pitch = (int)e.NewValue };
            if (dragging) Amend(next);
            else Commit(next);
            dragging = Mouse.LeftButton == MouseButtonState.Pressed;
        };
        _panel.Children.Add(_pitchRow);
        _panel.Children.Add(Panels.Heading("Stretches", 20));
        _panel.Children.Add(Panels.Hint("Drag on the shape of the sound to mark a stretch. Drag one to move it or its ends to resize it; Delete removes the selected one.", 0));
        _panel.Children.Add(_count);
        _panel.Children.Add(_hearing);
        _panel.Children.Add(Panels.Heading("Keys", 20));
        _panel.Children.Add(Panels.Keys([("I, O", "Start or end the selected stretch at the playhead, or mark a new one"), ("Delete", "Remove the selected stretch"), .. TimelineKeys()]));
    }

    public override FrameworkElement Panel => _panel;

    internal BleepState Current => State;
    internal int? Selected => _selected;
    internal string Hearing => _hearing.Text;

    /// <summary>Completes when the sound playing has the current bleeps in.</summary>
    internal Task SoundReady => _updated.Task;
    TaskCompletionSource _updated = NewDone();

    static TaskCompletionSource NewDone()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    protected override BleepState Initial() => new([], "tone", 1000);

    protected override string EditorStatus => !Started ? "" :
        (State.Ranges.Count == 1 ? "1 stretch" : $"{State.Ranges.Count} stretches") + (_selected is not null ? " · one selected" : "");

    public override Target Result()
    {
        if (State.Ranges.Count == 0) throw new ConversionException("Mark a stretch to bleep first: drag on the shape of the sound.");
        return Entry.WithParameters(new Dictionary<string, string>
        {
            ["ranges"] = BleepTool.FormatRanges(State.Ranges),
            ["sound"] = State.Sound,
            ["pitch"] = State.Pitch.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    protected override void Show(BleepState state)
    {
        if (_selected >= state.Ranges.Count) _selected = null;
        _showSound(state.Sound);
        _pitchRow.Visibility = state.Sound == "tone" ? Visibility.Visible : Visibility.Collapsed;
        if ((int)_pitch.Value != state.Pitch) _pitch.Value = state.Pitch;
        _pitchValue.Text = $"{state.Pitch} Hz";
        _count.Text = state.Ranges.Count == 0 ? "None yet." : string.Join("\n", state.Ranges.Select((r, i) => $"{i + 1}.  {Timecode.Format(r.Start)} to {Timecode.Format(r.End)}"));
        Timeline.InvalidateVisual();
        _ = UpdateSoundAsync();
    }

    /// <summary>Makes the sound again with the bleeps in, a moment after the last change, and plays it from the same place.</summary>
    async Task UpdateSoundAsync()
    {
        var version = ++_version;
        if (_updated.Task.IsCompleted) _updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = _updated;
        await Task.Delay(300);
        if (version != _version || Preview is not FramePlayer player) return;
        _hearing.Text = "Making the sound with the bleeps in…";
        try
        {
            var state = State;
            var source = PlayingFrom;
            var folder = TempFolder;
            var made = await Task.Run(() => BleepTool.PreviewAsync(_tools, source, state.Ranges, state.Sound, state.Pitch, folder, CancellationToken.None));
            if (version != _version) return;
            await player.SwapAsync(made, CancellationToken.None);
            _hearing.Text = state.Ranges.Count == 0 ? "" : "What plays has the bleeps in.";
        }
        catch (ConversionException ex)
        {
            _hearing.Text = ex.Message;
        }
        finally
        {
            if (version == _version) done.TrySetResult();
        }
    }

    // ---- Marking stretches on the sound

    public override bool Down(TimelinePoint at, ModifierKeys modifiers)
    {
        if (!Started || at.Lane != TimelineLane.Sound) return false;
        _committed = false;
        _from = Math.Round(at.Time, 3);
        for (var i = 0; i < State.Ranges.Count; i++)
        {
            var range = State.Ranges[i];
            var grab = Math.Abs(at.Time - range.Start) <= at.Grip ? Grab.Start
                : Math.Abs(at.Time - range.End) <= at.Grip ? Grab.End
                : at.Time > range.Start && at.Time < range.End ? Grab.Move : Grab.None;
            if (grab == Grab.None) continue;
            (_selected, _grab, _original) = (i, grab, range);
            Timeline.InvalidateVisual();
            RaiseUpdated();
            return true;
        }
        (_selected, _grab) = (null, Grab.Draw);
        return true;
    }

    public override void Move(TimelinePoint at, ModifierKeys modifiers)
    {
        var time = Math.Clamp(Math.Round(at.Time, 3), 0, Duration);
        (double Start, double End) range;
        switch (_grab)
        {
            case Grab.Draw:
                if (Math.Abs(time - _from) < Math.Max(Shortest, at.Grip / 2)) return;
                range = (Math.Min(_from, time), Math.Max(_from, time));
                break;
            case Grab.Move:
                var by = Math.Clamp(time - _from, -_original.Start, Duration - _original.End);
                range = (Math.Round(_original.Start + by, 3), Math.Round(_original.End + by, 3));
                break;
            case Grab.Start:
                range = (Math.Min(time, _original.End - Shortest), _original.End);
                break;
            case Grab.End:
                range = (_original.Start, Math.Max(time, _original.Start + Shortest));
                break;
            default:
                return;
        }
        ImmutableList<(double Start, double End)> ranges;
        if (_grab == Grab.Draw && !_committed)
        {
            ranges = State.Ranges.Add(range);
            _selected = ranges.Count - 1;
        }
        else
        {
            ranges = State.Ranges.SetItem(_selected!.Value, range);
        }
        var next = State with { Ranges = ranges };
        if (_committed) Amend(next);
        else Commit(next);
        _committed = true;
    }

    public override void Up(TimelinePoint at)
    {
        var grab = _grab;
        _grab = Grab.None;
        if (grab == Grab.Draw && !_committed)
        {
            // A click without a drag moves the playhead, as anywhere else on the timeline.
            MoveTo(at.Time);
            return;
        }
        if (!_committed) return;
        // Stretches that now overlap become one, as they would be bleeped anyway.
        var selected = _selected is { } s ? State.Ranges[s] : default;
        var joined = BleepTool.Join(State.Ranges).ToImmutableList();
        if (joined.Count != State.Ranges.Count || !joined.SequenceEqual(State.Ranges))
        {
            _selected = joined.FindIndex(r => r.Start <= selected.Start && r.End >= selected.End);
            if (_selected < 0) _selected = null;
            Amend(State with { Ranges = joined });
        }
    }

    public override Cursor? CursorAt(TimelinePoint at)
    {
        if (!Started || at.Lane != TimelineLane.Sound) return null;
        foreach (var range in State.Ranges)
        {
            if (Math.Abs(at.Time - range.Start) <= at.Grip || Math.Abs(at.Time - range.End) <= at.Grip) return Cursors.SizeWE;
            if (at.Time > range.Start && at.Time < range.End) return Cursors.SizeAll;
        }
        return Cursors.Cross;
    }

    protected override bool OnTimelineKey(Key key, ModifierKeys modifiers, bool repeat)
    {
        if (!Started || modifiers != ModifierKeys.None) return false;
        switch (key)
        {
            case Key.Delete or Key.Back when _selected is { } s:
                _selected = null;
                Commit(State with { Ranges = State.Ranges.RemoveAt(s) });
                return true;
            case Key.Escape when _selected is not null:
                _selected = null;
                Timeline.InvalidateVisual();
                RaiseUpdated();
                return true;
            case Key.I or Key.O:
                SetEnd(key == Key.I, Math.Round(Playhead, 3));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Starts or ends the selected stretch at a time; with none selected, marks a second's stretch from or to there.</summary>
    void SetEnd(bool start, double time)
    {
        if (_selected is { } s)
        {
            var range = State.Ranges[s];
            range = start ? (Math.Min(time, range.End - Shortest), range.End) : (range.Start, Math.Max(time, range.Start + Shortest));
            Commit(State with { Ranges = State.Ranges.SetItem(s, range) });
            return;
        }
        var made = start ? (time, Math.Min(Duration, time + 1)) : (Math.Max(0, time - 1), time);
        if (made.Item2 - made.Item1 < Shortest) return;
        _selected = State.Ranges.Count;
        Commit(State with { Ranges = State.Ranges.Add(made) });
    }

    public override void Draw(DrawingContext dc, TimelineView view)
    {
        if (!Started) return;
        var (soundTop, soundHeight) = view.Band(TimelineLane.Sound);
        var (top, _) = view.ShowPictures ? view.Band(TimelineLane.Pictures) : (soundTop, 0.0);
        var bottom = soundTop + soundHeight;
        var edge = new Pen(view.Accent, 1.5);
        for (var i = 0; i < State.Ranges.Count; i++)
        {
            var (start, end) = State.Ranges[i];
            double x0 = view.XAt(start), x1 = Math.Max(view.XAt(end), x0 + 2);
            dc.PushOpacity(i == _selected ? 0.55 : 0.35);
            dc.DrawRectangle(view.Accent, null, new Rect(x0, soundTop, x1 - x0, soundHeight));
            dc.Pop();
            dc.PushOpacity(0.18);
            dc.DrawRectangle(view.Accent, null, new Rect(x0, top, x1 - x0, soundTop - top));
            dc.Pop();
            dc.DrawLine(edge, new Point(x0, top), new Point(x0, bottom));
            dc.DrawLine(edge, new Point(x1, top), new Point(x1, bottom));
            if (i != _selected) continue;
            foreach (var x in new[] { x0, x1 })
                dc.DrawRoundedRectangle(view.Accent, null, new Rect(x - 3, soundTop + soundHeight / 2 - 10, 6, 20), 2, 2);
        }
    }
}

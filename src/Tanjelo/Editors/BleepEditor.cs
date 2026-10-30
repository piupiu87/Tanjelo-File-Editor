using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tanjelo.Core;
using Tanjelo.Core.Converters;

namespace Tanjelo.Editors;

/// <summary>What the bleep editor saves: the stretches, each with what covers it, and the cover new ones get.</summary>
sealed record BleepState(ImmutableList<Bleep> Bleeps, bool Tone, int Pitch);

/// <summary>
/// Bleep: drag on the shape of the sound to mark a stretch, then move it or pull its ends. Each stretch is covered with a
/// tone of its own pitch or with silence, and the timeline shows it that way: a wave for a tone, a flat line for silence,
/// with what it is written on it. Picking a stretch shows its cover in the panel, to change, and Hear it plays it with
/// a second either side. After each change the sound is made again in the background, so what plays has the bleeps in.
/// </summary>
sealed class BleepEditor : TimelineEditor<BleepState>
{
    /// <summary>The shortest stretch a drag makes, in seconds; shorter is a click, which moves the playhead.</summary>
    const double Shortest = 0.02;

    /// <summary>How much is played either side of a stretch by Hear it, in seconds.</summary>
    const double Around = 1;

    readonly Toolbox _tools;
    readonly StackPanel _panel = new();
    readonly TextBlock _coverHeading = Panels.Heading("");
    readonly TextBlock _selectedTimes = Panels.Hint("", 0);
    readonly StackPanel _pitchRow = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly Slider _pitch;
    readonly TextBlock _pitchValue;
    readonly WrapPanel _actions = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock _coverHint = Panels.Hint("", 10);
    readonly StackPanel _list = new() { Margin = new Thickness(0, 2, 0, 0) };
    readonly TextBlock _hearing = Panels.Hint("", 8);
    Action<string> _showSound = _ => { };
    bool _showing;
    int? _selected;

    // A drag on the sound: what it does, from where, and whether it has changed anything yet.
    enum Grab { None, Draw, Move, Start, End }
    Grab _grab;
    double _from;
    Bleep _original;
    bool _committed;
    bool _pitchDragging;

    // The sound playing: made again after each change, the newest wins.
    int _version;
    TaskCompletionSource _updated = Done();

    public BleepEditor(string file, Target target, Toolbox tools) : base(file, target, tools)
    {
        _tools = tools;
        _panel.Children.Add(_coverHeading);
        _panel.Children.Add(_selectedTimes);
        var (chips, showSound) = Panels.Chips(BleepTool.Sounds, "tone", sound =>
            SetCover(sound == "tone", _selected is { } s ? State.Bleeps[s].Pitch : State.Pitch, gesture: false));
        _showSound = showSound;
        chips.Margin = new Thickness(0, 8, 0, -6);
        _panel.Children.Add(chips);

        _pitch = Panels.Themed(new Slider
        {
            Minimum = BleepTool.LowestPitch,
            Maximum = BleepTool.HighestPitch,
            SmallChange = 50,
            LargeChange = 250,
            IsSnapToTickEnabled = true,
            TickFrequency = 50,
        }, FrameworkElement.StyleProperty, "ThinSlider");
        System.Windows.Automation.AutomationProperties.SetName(_pitch, Strings.Pitch);
        _pitchValue = Panels.Themed(new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right }, TextBlock.ForegroundProperty, "TextSecondary");
        var top = new Grid();
        top.Children.Add(new TextBlock { Text = Strings.Pitch, FontSize = 12.5 });
        top.Children.Add(_pitchValue);
        _pitchRow.Children.Add(top);
        _pitchRow.Children.Add(_pitch);
        // A drag along the slider is one step to undo.
        _pitch.PreviewMouseLeftButtonUp += (_, _) => _pitchDragging = false;
        _pitch.ValueChanged += (_, e) =>
        {
            _pitchValue.Text = string.Format(Strings.Hz, e.NewValue);
            if (_showing || !Started) return;
            SetCover(true, (int)e.NewValue, gesture: _pitchDragging);
            _pitchDragging = Mouse.LeftButton == MouseButtonState.Pressed;
        };
        _panel.Children.Add(_pitchRow);

        _actions.Children.Add(Panels.Button("", Strings.HearIt, Strings.PlayThisStretchWithASecondEither, () => _ = HearAsync()));
        _actions.Children.Add(Panels.Button("", Strings.Remove, Strings.RemoveThisStretchDelete, RemoveSelected));
        _panel.Children.Add(_actions);
        _panel.Children.Add(_coverHint);

        _panel.Children.Add(Panels.Heading(Strings.Stretches, 20));
        _panel.Children.Add(_list);
        _panel.Children.Add(_hearing);
        _panel.Children.Add(Panels.Heading(Strings.Keys, 20));
        _panel.Children.Add(Panels.Keys(
        [
            ("H", Strings.HearTheSelectedStretch),
            (Strings.IO, Strings.StartOrEndTheSelectedStretchAt),
            (Strings.InsertN, Strings.MarkASecondFromThePlayhead),
            (Strings.Tab, Strings.SelectTheNextStretch),
            (Strings.Alt, Strings.MoveItAFrameWithShiftA),
            (Strings.Ctrl, Strings.MoveItsEnd),
            (Strings.Delete, Strings.RemoveTheSelectedStretch),
            .. TimelineKeys(),
        ]));
    }

    static TaskCompletionSource Done()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    public override FrameworkElement Panel => _panel;

    internal BleepState Current => State;
    internal int? Selected => _selected;
    internal string Hearing => _hearing.Text;
    internal string CoverHeading => _coverHeading.Text;

    /// <summary>Completes when the sound playing has the current bleeps in.</summary>
    internal Task SoundReady => _updated.Task;

    protected override BleepState Initial() => new([], true, 1000);

    /// <summary>Without stretches there's nothing to save, whatever the cover.</summary>
    public override bool Changed => base.Changed && State.Bleeps.Count > 0;

    protected override string EditorStatus => !Started ? "" :
        (State.Bleeps.Count == 1 ? Strings.N1Stretch : string.Format(Strings.Stretches2, State.Bleeps.Count)) + Panels.Which(_selected, State.Bleeps.Count);

    public override Target Result()
    {
        if (State.Bleeps.Count == 0) throw new ConversionException(Strings.MarkAStretchToBleepFirstDrag);
        return Entry.WithParameters(new Dictionary<string, string>
        {
            ["ranges"] = BleepTool.FormatBleeps(State.Bleeps),
            ["sound"] = State.Tone ? "tone" : "silence",
            ["pitch"] = State.Pitch.ToString(CultureInfo.InvariantCulture),
        });
    }

    protected override void Show(BleepState state)
    {
        if (_selected >= state.Bleeps.Count) _selected = null;
        ShowPanel();
        Timeline.InvalidateVisual();
        _ = UpdateSoundAsync();
    }

    /// <summary>The panel for the selected stretch, or for the ones still to be marked.</summary>
    void ShowPanel()
    {
        _showing = true;
        var (tone, pitch) = _selected is { } s ? (State.Bleeps[s].Tone, State.Bleeps[s].Pitch) : (State.Tone, State.Pitch);
        if (_selected is { } i)
        {
            var bleep = State.Bleeps[i];
            _coverHeading.Text = string.Format(Strings.Stretch, i + 1);
            _selectedTimes.Text = string.Format(Strings.ToSCoveredWith, Timecode.Format(bleep.Start), Timecode.Format(bleep.End), bleep.End - bleep.Start);
            _coverHint.Text = Strings.NewStretchesAreCoveredTheSameWay;
        }
        else
        {
            _coverHeading.Text = Strings.NewStretches;
            _selectedTimes.Text = Strings.CoveredWith;
            _coverHint.Text = State.Bleeps.Count == 0
                ? Strings.DragOnTheShapeOfTheSound
                : Strings.ClickAStretchToChangeItsCover;
        }
        _showSound(tone ? "tone" : "silence");
        _pitchRow.Visibility = tone ? Visibility.Visible : Visibility.Collapsed;
        _pitch.Value = pitch;
        _pitchValue.Text = string.Format(Strings.Hz2, pitch);
        _actions.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        ShowList();
        _showing = false;
    }

    /// <summary>Each stretch as a row that selects it: its times and its cover.</summary>
    void ShowList()
    {
        _list.Children.Clear();
        if (State.Bleeps.Count == 0)
        {
            _list.Children.Add(Panels.Hint(Strings.NoneYet, 0));
            return;
        }
        for (var i = 0; i < State.Bleeps.Count; i++)
        {
            var bleep = State.Bleeps[i];
            var index = i;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var number = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 12 };
            var times = new TextBlock { Text = $"{OptionValues.FormatTime(bleep.Start)} – {OptionValues.FormatTime(bleep.End)}", FontSize = 12 };
            var cover = Panels.Themed(new TextBlock { Text = bleep.Cover, FontSize = 12 }, TextBlock.ForegroundProperty, "TextSecondary");
            Grid.SetColumn(times, 1);
            Grid.SetColumn(cover, 2);
            row.Children.Add(number);
            row.Children.Add(times);
            row.Children.Add(cover);
            var item = new Border { Child = row, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand };
            if (i == _selected) item.SetResourceReference(Border.BackgroundProperty, "Selected");
            else item.Background = Brushes.Transparent;
            item.ToolTip = Strings.SelectThisStretchAndGoToIt;
            item.MouseLeftButtonUp += (_, _) =>
            {
                Select(index);
                MoveTo(State.Bleeps[index].Start);
            };
            _list.Children.Add(item);
        }
    }

    void Select(int? index)
    {
        _selected = index;
        ShowPanel();
        Timeline.InvalidateVisual();
        RaiseUpdated();
    }

    /// <summary>Covers the selected stretch with a tone or silence, and new ones the same way.</summary>
    void SetCover(bool tone, int pitch, bool gesture)
    {
        var bleeps = State.Bleeps;
        if (_selected is { } s) bleeps = bleeps.SetItem(s, bleeps[s] with { Tone = tone, Pitch = pitch });
        var next = new BleepState(bleeps, tone, pitch);
        if (next == State) return;
        if (gesture) Amend(next);
        else Commit(next);
    }

    /// <summary>Picks tone or silence, as the chips do.</summary>
    internal void SetSound(bool tone) => SetCover(tone, _selected is { } s ? State.Bleeps[s].Pitch : State.Pitch, gesture: false);

    /// <summary>Sets the pitch, as a click on the slider does.</summary>
    internal void SetPitch(int pitch) => SetCover(true, pitch, gesture: false);

    void RemoveSelected()
    {
        if (_selected is not { } s) return;
        _selected = null;
        Commit(State with { Bleeps = State.Bleeps.RemoveAt(s) });
    }

    // ---- What plays

    /// <summary>
    /// Puts the bleeps into the sound that plays, a moment after the last change. The file's sound is decoded once, in
    /// the background as the window opens; after that each change only writes a copy of it with the bleeps in, which
    /// takes moments, and plays that from the same place.
    /// </summary>
    async Task UpdateSoundAsync()
    {
        var version = ++_version;
        if (_updated.Task.IsCompleted) _updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = _updated;
        if (Preview is FramePlayer) _decoded ??= DecodeAsync();
        await Task.Delay(150);
        if (version != _version) return;
        try
        {
            if (Preview is not FramePlayer || _decoded is null)
            {
                _hearing.Text = State.Bleeps.Count == 0 ? "" :
                    Strings.WindowsCanTPlayThisFileHere;
                return;
            }
            var bleeps = State.Bleeps;
            if (bleeps.Count == 0)
            {
                _hearing.Text = "";
                await UseSoundAsync(null);
                return;
            }
            if (!_decoded.IsCompleted) _hearing.Text = Strings.GettingTheSoundReadyToBleep;
            var sound = await _decoded;
            if (version != _version) return;
            var made = await Task.Run(() => sound.Write(bleeps));
            if (version != _version)
            {
                OutputPaths.TryDelete(made);
                return;
            }
            var playing = await UseSoundAsync(made);
            // The one before is let go of now; the window's folder goes when it closes anyway.
            if (_made is { } before) _ = Task.Delay(1000).ContinueWith(_ => OutputPaths.TryDelete(before), TaskScheduler.Default);
            _made = made;
            _hearing.Text = playing
                ? Strings.ReadyWhatPlaysHasTheBleepsIn
                : Strings.CouldnTPlayTheSoundWithThe;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not ConversionException) Log.Write(ex);
            _hearing.Text = Strings.CouldnTMakeTheSoundWithThe + ex.Message;
        }
        finally
        {
            if (version == _version) done.TrySetResult();
        }
    }

    /// <summary>The file's sound as plain samples, decoded once, from the original (what's saved is made from it too).</summary>
    Task<BleepSound>? _decoded;

    /// <summary>The sound with the bleeps in that plays now.</summary>
    string? _made;

    Task<BleepSound> DecodeAsync()
    {
        var (file, folder, closing) = (File, TempFolder, Closing);
        return Task.Run(() => BleepSound.DecodeAsync(_tools, file, folder, closing), closing);
    }

    /// <summary>Plays the selected stretch with a second either side, once the sound with the bleeps in is ready.</summary>
    internal async Task HearAsync()
    {
        if (_selected is not { } s || !Started) return;
        var bleep = State.Bleeps[s];
        await SoundReady;
        if (!PlayBetween(bleep.Start - Around, bleep.End + Around))
            _hearing.Text = Strings.WindowsCanTPlayThisFileHere;
    }

    // ---- Marking stretches on the sound

    public override bool Down(TimelinePoint at, ModifierKeys modifiers)
    {
        if (!Started || at.Lane != TimelineLane.Sound) return false;
        _committed = false;
        _from = Math.Round(at.Time, 3);
        // The selected stretch first, as it's drawn over the rest.
        var order = Enumerable.Range(0, State.Bleeps.Count).OrderByDescending(i => i == _selected);
        foreach (var i in order)
        {
            var bleep = State.Bleeps[i];
            var grab = Math.Abs(at.Time - bleep.Start) <= at.Grip ? Grab.Start
                : Math.Abs(at.Time - bleep.End) <= at.Grip ? Grab.End
                : at.Time > bleep.Start && at.Time < bleep.End ? Grab.Move : Grab.None;
            if (grab == Grab.None) continue;
            (_grab, _original) = (grab, bleep);
            Select(i);
            return true;
        }
        _grab = Grab.Draw;
        return true;
    }

    public override void Move(TimelinePoint at, ModifierKeys modifiers)
    {
        var time = Math.Clamp(Math.Round(at.Time, 3), 0, Duration);
        Bleep bleep;
        switch (_grab)
        {
            case Grab.Draw:
                if (Math.Abs(time - _from) < Math.Max(Shortest, at.Grip / 2)) return;
                bleep = new Bleep(Math.Min(_from, time), Math.Max(_from, time), State.Tone, State.Pitch);
                break;
            case Grab.Move:
                var by = Math.Clamp(time - _from, -_original.Start, Duration - _original.End);
                bleep = _original with { Start = Math.Round(_original.Start + by, 3), End = Math.Round(_original.End + by, 3) };
                break;
            case Grab.Start:
                bleep = _original with { Start = Math.Min(time, _original.End - Shortest) };
                break;
            case Grab.End:
                bleep = _original with { End = Math.Max(time, _original.Start + Shortest) };
                break;
            default:
                return;
        }
        ImmutableList<Bleep> bleeps;
        if (_grab == Grab.Draw && !_committed)
        {
            bleeps = State.Bleeps.Add(bleep);
            _selected = bleeps.Count - 1;
        }
        else
        {
            bleeps = State.Bleeps.SetItem(_selected!.Value, bleep);
        }
        var next = State with { Bleeps = bleeps };
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
            Select(null);
            MoveTo(at.Time);
            return;
        }
        if (!_committed || _selected is not { } s) return;
        var (bleeps, kept) = Settle(State.Bleeps, s);
        _selected = kept;
        if (!bleeps.SequenceEqual(State.Bleeps)) Amend(State with { Bleeps = bleeps });
        else ShowPanel();
    }

    /// <summary>
    /// The stretches in order, with the one just drawn or moved having its way where it overlaps others: covered alike they
    /// become one, otherwise the others give way.
    /// </summary>
    static (ImmutableList<Bleep> Bleeps, int Kept) Settle(ImmutableList<Bleep> bleeps, int moved)
    {
        var mine = bleeps[moved];
        var others = new List<Bleep>();
        for (var i = 0; i < bleeps.Count; i++)
        {
            if (i == moved) continue;
            var other = bleeps[i];
            if (other.End < mine.Start || other.Start > mine.End || (!other.SameCover(mine) && (other.End == mine.Start || other.Start == mine.End)))
                others.Add(other);
            else if (other.SameCover(mine))
                mine = mine with { Start = Math.Min(mine.Start, other.Start), End = Math.Max(mine.End, other.End) };
            else
            {
                // What's left of it on either side, if it's long enough to keep.
                if (mine.Start - other.Start >= Shortest) others.Add(other with { End = mine.Start });
                if (other.End - mine.End >= Shortest) others.Add(other with { Start = mine.End });
            }
        }
        var all = others.Append(mine).OrderBy(b => b.Start).ToImmutableList();
        return (all, all.IndexOf(mine));
    }

    public override Cursor? CursorAt(TimelinePoint at)
    {
        if (!Started || at.Lane != TimelineLane.Sound) return null;
        foreach (var bleep in State.Bleeps)
        {
            if (Math.Abs(at.Time - bleep.Start) <= at.Grip || Math.Abs(at.Time - bleep.End) <= at.Grip) return Cursors.SizeWE;
            if (at.Time > bleep.Start && at.Time < bleep.End) return Cursors.SizeAll;
        }
        return Cursors.Cross;
    }

    protected override bool OnTimelineKey(Key key, ModifierKeys modifiers, bool repeat)
    {
        if (!Started) return false;
        switch (key)
        {
            case Key.Delete or Key.Back when _selected is not null && modifiers == ModifierKeys.None:
                RemoveSelected();
                return true;
            case Key.Escape when _selected is not null:
                Select(null);
                return true;
            case Key.Tab when State.Bleeps.Count > 0:
                // Past the last stretch (or the first) none is selected and Tab moves on to the next control.
                var by = modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                var next = _selected is { } current ? current + by : by > 0 ? 0 : State.Bleeps.Count - 1;
                Select(next >= 0 && next < State.Bleeps.Count ? next : null);
                return _selected is not null;
            case Key.Insert or Key.N when modifiers == ModifierKeys.None:
                Select(null);
                SetEnd(true, Math.Round(Playhead, 3));
                return true;
            case Key.Left or Key.Right when _selected is { } s && (modifiers.HasFlag(ModifierKeys.Alt) || modifiers.HasFlag(ModifierKeys.Control)):
                // Plain arrows move the playhead, so a stretch moves with Alt and the arrows, and its end with Ctrl.
                Shift(s, key == Key.Left ? -1 : 1, modifiers.HasFlag(ModifierKeys.Shift), endOnly: modifiers.HasFlag(ModifierKeys.Control), repeat);
                return true;
            case Key.H when modifiers == ModifierKeys.None && _selected is not null:
                _ = HearAsync();
                return true;
            case Key.I or Key.O when modifiers == ModifierKeys.None:
                SetEnd(key == Key.I, Math.Round(Playhead, 3));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Moves a stretch, or only its end, by a frame (a hundredth of a second in sound) or by a second, from the keyboard.</summary>
    internal void Shift(int index, int direction, bool bySecond, bool endOnly, bool repeat = false)
    {
        var bleep = State.Bleeps[index];
        var step = bySecond ? 1 : Frames is { } frames ? frames.Stop(frames.At(bleep.Start)) - frames.Start(frames.At(bleep.Start)) : 0.01;
        var by = direction * step;
        var moved = endOnly
            ? bleep with { End = Math.Clamp(SnapCut(bleep.End + by), bleep.Start + Shortest, Duration) }
            : bleep with { Start = SnapCut(Math.Clamp(bleep.Start + by, 0, Duration - (bleep.End - bleep.Start))), End = SnapCut(Math.Clamp(bleep.End + by, bleep.End - bleep.Start, Duration)) };
        if (moved == bleep) return;
        var (settled, kept) = Settle(State.Bleeps.SetItem(index, moved), index);
        _selected = kept;
        if (repeat) Amend(State with { Bleeps = settled });
        else Commit(State with { Bleeps = settled });
    }

    /// <summary>Starts or ends the selected stretch at a time; with none selected, marks a second's stretch from or to there.</summary>
    void SetEnd(bool start, double time)
    {
        if (_selected is { } s)
        {
            var bleep = State.Bleeps[s];
            bleep = start ? bleep with { Start = Math.Min(time, bleep.End - Shortest) } : bleep with { End = Math.Max(time, bleep.Start + Shortest) };
            var (settled, kept) = Settle(State.Bleeps.SetItem(s, bleep), s);
            _selected = kept;
            Commit(State with { Bleeps = settled });
            return;
        }
        var made = start ? new Bleep(time, Math.Min(Duration, time + 1), State.Tone, State.Pitch) : new Bleep(Math.Max(0, time - 1), time, State.Tone, State.Pitch);
        if (made.End - made.Start < Shortest) return;
        var (bleeps, index) = Settle(State.Bleeps.Add(made), State.Bleeps.Count);
        _selected = index;
        Commit(State with { Bleeps = bleeps });
    }

    // ---- How the stretches look: as they'll sound

    public override void Draw(DrawingContext dc, TimelineView view)
    {
        if (!Started) return;
        var (soundTop, soundHeight) = view.Band(TimelineLane.Sound);
        var middle = soundTop + soundHeight / 2;
        var (picturesTop, picturesHeight) = view.Band(TimelineLane.Pictures);
        var dpi = VisualTreeHelper.GetDpi(view).PixelsPerDip;
        var icons = new Typeface((FontFamily)(view.TryFindResource("IconFont") ?? new FontFamily("Segoe MDL2 Assets")), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var words = new Typeface((FontFamily)(view.TryFindResource("UiFont") ?? new FontFamily("Segoe UI")), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        for (var i = 0; i < State.Bleeps.Count; i++)
        {
            var bleep = State.Bleeps[i];
            var selected = i == _selected;
            double x0 = view.XAt(bleep.Start), x1 = Math.Max(view.XAt(bleep.End), x0 + 2);
            var area = new Rect(x0, soundTop, x1 - x0, soundHeight);
            // The sound that was there, faded under what replaces it.
            dc.PushOpacity(0.82);
            dc.DrawRectangle(view.Lane, null, area);
            dc.Pop();
            dc.PushOpacity(selected ? 0.28 : 0.16);
            dc.DrawRectangle(view.Accent, null, area);
            if (picturesHeight > 0) dc.DrawRectangle(view.Accent, null, new Rect(x0, picturesTop, x1 - x0, picturesHeight));
            dc.Pop();

            dc.PushClip(new RectangleGeometry(area));
            if (bleep.Tone)
            {
                // A wave as tall as the tone is loud, closer together the higher it is.
                var wave = new StreamGeometry();
                var period = Math.Clamp(24000.0 / bleep.Pitch, 6, 60);
                var amplitude = soundHeight * 0.3;
                using (var g = wave.Open())
                {
                    g.BeginFigure(new Point(x0, middle), false, false);
                    var points = new List<Point>();
                    for (var x = x0; x <= x1; x += 1)
                        points.Add(new Point(x, middle - amplitude * Math.Sin((x - x0) / period * 2 * Math.PI)));
                    g.PolyLineTo(points, true, true);
                }
                wave.Freeze();
                dc.DrawGeometry(null, new Pen(view.Accent, 1.5), wave);
            }
            else
            {
                dc.DrawLine(new Pen(view.Accent, 2), new Point(x0, middle), new Point(x1, middle));
            }
            // What covers it, written on it where there's room, or just its sign.
            var glyph = new FormattedText(bleep.Tone ? "" : "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, icons, 12, view.Accent, dpi);
            var label = new FormattedText(bleep.Tone ? string.Format(Strings.Hz2, bleep.Pitch) : Strings.Silence, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, words, 11, view.Accent, dpi);
            if (x1 - x0 >= glyph.Width + label.Width + 14)
            {
                dc.DrawText(glyph, new Point(x0 + 5, soundTop + 4));
                dc.DrawText(label, new Point(x0 + 9 + glyph.Width, soundTop + 4));
            }
            else if (x1 - x0 >= glyph.Width + 6)
            {
                dc.DrawText(glyph, new Point(x0 + 3, soundTop + 4));
            }
            dc.Pop();

            var edge = new Pen(view.Accent, selected ? 2 : 1);
            dc.DrawRectangle(null, edge, area);
            if (!selected) continue;
            foreach (var x in new[] { x0, x1 })
                dc.DrawRoundedRectangle(view.Accent, null, new Rect(x - 3, middle - 10, 6, 20), 2, 2);
        }
    }
}

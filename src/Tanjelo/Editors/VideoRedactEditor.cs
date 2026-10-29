using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Tanjelo.Core;
using Tanjelo.Core.Converters;
using Shapes = System.Windows.Shapes;

namespace Tanjelo.Editors;

/// <summary>What the video redaction editor saves: the boxes with their stretches, and how they're hidden.</summary>
sealed record VideoRedactState(ImmutableList<VideoBox> Boxes, string Style, Colour Colour);

/// <summary>
/// Redact video: draw boxes on the picture, each hidden from the frame it was drawn on to the end, and narrow each one's
/// stretch on its own bar under the timeline (drag its ends, or press I and O). Find faces adds a box over each face
/// on the frame showing; Follow face moves the selected box with its face through its stretch, ending it where the
/// face is lost. Each box is drawn for the frame in the picture, playing or paused, hidden as the saved copy will have
/// it there.
/// </summary>
sealed class VideoRedactEditor : TimelineEditor<VideoRedactState>, ICanvasTool
{
    static readonly string[] Colours = ["#000000", "#FFFFFF", "#5F5F5F", "#C2410C", "#3A7BD5"];

    /// <summary>How near an edge, in screen pixels, still counts as on it.</summary>
    const double Grip = 7;

    readonly Toolbox _tools;
    readonly StackPanel _panel = new();
    readonly TextBlock _found = Panels.Hint("", 8);
    readonly TextBlock _count = Panels.Hint("", 10);
    readonly Button _faces, _follow;
    readonly StackPanel _colourRow = new();
    readonly CancellationTokenSource _closing = new();
    Action<string> _showStyle = _ => { };
    Action<Colour> _showColour = _ => { };
    int? _selected;
    bool _busy;

    // A drag on the picture: what it does, and whether it has changed anything yet.
    BoxDrag? _drag;
    int _dragging = -1;
    bool _committed;

    // A drag on a box's bar under the timeline.
    enum Grab { None, Move, From, To }
    Grab _grab;
    double _grabAt;
    VideoBox? _grabbed;

    public VideoRedactEditor(string file, Target target, Toolbox tools) : base(file, target, tools)
    {
        _tools = tools;
        CanvasPart.Tool = this;
        CanvasPart.ViewChanged += DrawFrames;

        _faces = Panels.Button("", Strings.FindFaces, Strings.AddABoxOverEachFaceOn, () => _ = FindFacesAsync());
        _follow = Panels.Button("", Strings.FollowFace, Strings.MoveTheSelectedBoxWithTheFace, () => _ = FollowAsync());
        _faces.IsEnabled = Redaction.CanFindFaces;
        _follow.IsEnabled = false;
        ToolTipService.SetShowOnDisabled(_follow, true);
        if (!_faces.IsEnabled) _faces.ToolTip = Strings.WindowsCanTLookForFacesOn;
        var find = new WrapPanel();
        find.Children.Add(_faces);
        find.Children.Add(_follow);
        _panel.Children.Add(Panels.Heading(Strings.Find));
        _panel.Children.Add(find);
        _panel.Children.Add(_found);
        _panel.Children.Add(Panels.Heading(Strings.HideWith, 20));
        var (chips, showStyle) = Panels.Chips(Redaction.Styles, "solid", style => Commit(State with { Style = style }));
        _showStyle = showStyle;
        _panel.Children.Add(chips);
        var (swatches, showColour) = Panels.Swatches(Colours, new Colour(0, 0, 0), colour =>
        {
            if (colour != State.Colour) Commit(State with { Colour = colour });
        });
        _showColour = showColour;
        _colourRow.Margin = new Thickness(0, 12, 0, 0);
        _colourRow.Children.Add(swatches);
        _panel.Children.Add(_colourRow);
        _panel.Children.Add(Panels.Heading(Strings.Areas, 20));
        _panel.Children.Add(Panels.Hint(Strings.DragOnThePictureToDrawA2, 0));
        _panel.Children.Add(_count);
        _panel.Children.Add(Panels.Heading(Strings.Keys, 20));
        _panel.Children.Add(Panels.Keys(
        [
            (Strings.InsertN, Strings.AddABoxInTheMiddleFrom),
            (Strings.IO, Strings.StartTheSelectedBoxHereOrEnd),
            (Strings.Tab, Strings.SelectTheNextBox),
            (Strings.AltArrows, Strings.MoveItAPixelWithShift10),
            (Strings.CtrlArrows, Strings.MakeItBiggerOrSmaller),
            (Strings.Delete, Strings.RemoveTheSelectedBox),
            .. TimelineKeys(),
        ]));
        _panel.Children.Add(Panels.Hint(Strings.TheCopyIsReEncodedAndLoses, 14));
    }

    ZoomCanvas CanvasPart => Canvas!;

    public override FrameworkElement Panel => _panel;

    internal VideoRedactState Current => State;
    internal int? Selected => _selected;
    internal string Found => _found.Text;
    internal bool CanFollowNow => _follow.IsEnabled;

    /// <summary>Picks a style, as its chip does.</summary>
    internal void Restyle(string style) => Commit(State with { Style = style });

    protected override VideoRedactState Initial() => new([], "solid", new Colour(0, 0, 0));

    /// <summary>With no boxes there's nothing to save, whatever the style.</summary>
    public override bool Changed => base.Changed && State.Boxes.Count > 0;

    protected override string EditorStatus => !Started ? "" : Describe.Count(State.Boxes.Count, Tanjelo.Core.Strings.OneArea, Tanjelo.Core.Strings.NAreas) + Panels.Which(_selected, State.Boxes.Count);

    public override Target Result()
    {
        if (State.Boxes.Count == 0) throw new ConversionException(Strings.DrawABoxOverSomethingToHide2);
        return Entry.WithParameters(new Dictionary<string, string>
        {
            ["spec"] = VideoBoxSpec.Write(State.Boxes),
            ["style"] = State.Style,
            ["colour"] = State.Colour.ToString(),
        });
    }

    protected override void Show(VideoRedactState state)
    {
        if (_selected >= state.Boxes.Count) _selected = null;
        _showStyle(state.Style);
        _showColour(state.Colour);
        _colourRow.Visibility = state.Style == "solid" ? Visibility.Visible : Visibility.Collapsed;
        _count.Text = state.Boxes.Count == 0 ? Strings.NoAreasYet : Describe.Count(state.Boxes.Count, Tanjelo.Core.Strings.OneArea, Tanjelo.Core.Strings.NAreas) + Strings.ToHide;
        ShowFollow();
        Timeline.Rows = Math.Max(1, state.Boxes.Count);
        DrawCovers();
        Timeline.InvalidateVisual();
    }

    // What's drawn follows the picture, not the playhead: while playing, the playhead moves on with the clock a moment
    // before each frame is copied out and shows, and boxes moved with it would be a frame ahead of what they cover.
    protected override void PictureShown() => DrawCovers();

    // ---- What shows on the picture

    /// <summary>
    /// The boxes hidden on the frame showing, in the picture's own pixels, each as the copy will have it: pixelated or
    /// blurred ones made from that frame, anew for every frame as it plays. The covers are changed where they are, not
    /// taken off and put on again: with new ones for every frame, Windows now and then showed a frame of the picture
    /// with no cover on it, about once every half a minute of playing.
    /// </summary>
    void DrawCovers()
    {
        if (!Started) return;
        var covers = CanvasPart.World.Children;
        var now = Showing;
        var areas = State.Boxes.Where(b => b.Covers(now + 1e-6)).Select(b => b.At(now).Within(Media.Width, Media.Height)).OfType<RedactBox>().ToList();
        while (covers.Count > areas.Count) covers.RemoveAt(covers.Count - 1);
        for (var i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            var cover = Cover(i < covers.Count ? covers[i] : null, area);
            if (i == covers.Count) covers.Add(cover);
            else if (covers[i] != cover)
            {
                covers.RemoveAt(i);
                covers.Insert(i, cover);
            }
            (cover.Width, cover.Height) = (area.Width, area.Height);
            System.Windows.Controls.Canvas.SetLeft(cover, area.X);
            System.Windows.Controls.Canvas.SetTop(cover, area.Y);
        }
        DrawFrames();
    }

    /// <summary>
    /// What goes over an area of the frame showing, made in <paramref name="old"/> when that's of the right kind: the
    /// colour, or the frame's own pixels there pixelated or blurred, written into the cover's picture as the frame is.
    /// </summary>
    FrameworkElement Cover(UIElement? old, RedactBox area)
    {
        if (State.Style == "solid" || Preview?.Picture is not { } picture)
        {
            var colour = State.Style == "solid" ? Color.FromRgb(State.Colour.R, State.Colour.G, State.Colour.B) : Color.FromRgb(120, 120, 120);
            var rectangle = old as Shapes.Rectangle ?? new Shapes.Rectangle();
            if (rectangle.Fill is not SolidColorBrush brush || brush.Color != colour) rectangle.Fill = new SolidColorBrush(colour);
            return rectangle;
        }
        // The area in the picture, which can be a smaller copy of the frame.
        var (sx, sy) = (picture.PixelWidth / (double)Media.Width, picture.PixelHeight / (double)Media.Height);
        int left = (int)Math.Floor(area.X * sx), top = (int)Math.Floor(area.Y * sy);
        int width = Math.Max(1, Math.Min(picture.PixelWidth, (int)Math.Ceiling((area.X + area.Width) * sx)) - left);
        int height = Math.Max(1, Math.Min(picture.PixelHeight, (int)Math.Ceiling((area.Y + area.Height) * sy)) - top);
        var source = picture.Format == PixelFormats.Bgr32 || picture.Format == PixelFormats.Bgra32 || picture.Format == PixelFormats.Pbgra32
            ? picture : new FormatConvertedBitmap(picture, PixelFormats.Bgr32, null, 0);
        var pixels = new byte[width * height * 4];
        source.CopyPixels(new Int32Rect(left, top, width, height), pixels, width * 4, 0);
        VideoRedactTool.PreviewCover(pixels, width, height, area.Width, area.Height, State.Style);
        var image = old as Image ?? new Image { Stretch = Stretch.Fill };
        if (image.Source is not WriteableBitmap made || made.PixelWidth != width || made.PixelHeight != height)
            image.Source = made = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        made.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        return image;
    }

    /// <summary>The outlines last drawn: a frame that moves none of them leaves them as they are, as with the covers.</summary>
    List<(Rect Rect, bool Selected, bool Active)> _framed = [];

    /// <summary>The boxes' outlines, in screen units so they stay thin at any zoom; the selected one has handles, and is dashed when it isn't hidden now.</summary>
    void DrawFrames()
    {
        var framed = new List<(Rect Rect, bool Selected, bool Active)>();
        var now = Showing;
        for (var i = 0; Started && i < State.Boxes.Count; i++)
        {
            var box = State.Boxes[i];
            var (active, selected) = (box.Covers(now + 1e-6), i == _selected);
            if (!active && !selected) continue;
            var at = box.At(now);
            framed.Add((CanvasPart.View.ToCanvas(new Rect(at.X, at.Y, at.Width, at.Height)), selected, active));
        }
        if (framed.SequenceEqual(_framed)) return;
        _framed = framed;
        var overlay = CanvasPart.Overlay;
        overlay.Children.Clear();
        foreach (var (rect, selected, active) in framed)
        {
            AddFrame(rect, Color.FromArgb(150, 0, 0, 0), 3.5, null);
            AddFrame(rect, selected ? Colors.White : Color.FromArgb(220, 255, 255, 255), 1.5, selected && active ? null : new DoubleCollection { 4, 3 });
            if (!selected || !active) continue;
            foreach (var spot in new[] { rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft })
            {
                var handle = new Shapes.Rectangle { Width = 9, Height = 9, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), StrokeThickness = 1 };
                System.Windows.Controls.Canvas.SetLeft(handle, spot.X - 4.5);
                System.Windows.Controls.Canvas.SetTop(handle, spot.Y - 4.5);
                overlay.Children.Add(handle);
            }
        }
    }

    void AddFrame(Rect rect, Color colour, double thickness, DoubleCollection? dashes)
    {
        var frame = new Shapes.Rectangle { Width = rect.Width, Height = rect.Height, Stroke = new SolidColorBrush(colour), StrokeThickness = thickness, StrokeDashArray = dashes };
        System.Windows.Controls.Canvas.SetLeft(frame, rect.X);
        System.Windows.Controls.Canvas.SetTop(frame, rect.Y);
        CanvasPart.Overlay.Children.Add(frame);
    }

    void Select(int? index)
    {
        _selected = index;
        ShowFollow();
        DrawFrames();
        Timeline.InvalidateVisual();
        RaiseUpdated();
    }

    // ---- Drawing, moving and resizing boxes on the picture

    Size Bounds => new(Media.Width, Media.Height);

    double Tolerance => Grip / CanvasPart.View.Scale;

    static Rect ToRect(RedactBox box) => new(box.X, box.Y, box.Width, box.Height);

    /// <summary>The box showing at a point, the selected one first since it's drawn over the rest, then the last drawn.</summary>
    int? BoxAt(Point at)
    {
        var now = Showing;
        if (_selected is { } s && State.Boxes[s].Covers(now + 1e-6)
            && BoxDrag.Hit(at, ToRect(State.Boxes[s].At(now)), Tolerance, cornersOnly: false) != BoxDrag.Edges.None) return s;
        for (var i = State.Boxes.Count - 1; i >= 0; i--)
            if (State.Boxes[i].Covers(now + 1e-6) && ToRect(State.Boxes[i].At(now)).Contains(at)) return i;
        return null;
    }

    public void Down(Point at, ModifierKeys modifiers, int clicks)
    {
        if (!Started) return;
        Pause();
        _committed = false;
        if (BoxAt(at) is { } index)
        {
            Select(index);
            _dragging = index;
            _drag = BoxDrag.Start(at, ToRect(State.Boxes[index].At(Showing)), Bounds, null, Tolerance);
        }
        else
        {
            // Started away from every box: a new one, from this frame to the end.
            Select(null);
            _dragging = -1;
            _drag = BoxDrag.Start(at, new Rect(-1e6, -1e6, 0, 0), Bounds, null, Tolerance);
        }
    }

    public void Move(Point at, ModifierKeys modifiers)
    {
        if (_drag is null) return;
        var rect = _drag.To(at);
        var (x, y, w, h) = ((int)Math.Round(rect.X), (int)Math.Round(rect.Y), Math.Max(1, (int)Math.Round(rect.Width)), Math.Max(1, (int)Math.Round(rect.Height)));
        if (_dragging < 0)
        {
            // A new box counts once it's big enough to see, so a stray click doesn't add one.
            if (rect.Width * CanvasPart.View.Scale < 4 && rect.Height * CanvasPart.View.Scale < 4) return;
            _dragging = State.Boxes.Count;
            Commit(State with { Boxes = State.Boxes.Add(new VideoBox(x, y, w, h, Playhead)) });
            Select(_dragging);
            _committed = true;
            return;
        }
        // A box moved or resized by hand stays where it's put: one that followed a face stops following.
        var box = State.Boxes[_dragging] with { X = x, Y = y, Width = w, Height = h, Track = null };
        if (box == State.Boxes[_dragging]) return;
        var next = State with { Boxes = State.Boxes.SetItem(_dragging, box) };
        if (_committed) Amend(next);
        else Commit(next);
        _committed = true;
    }

    public void Up(Point at)
    {
        if (_drag is { What: BoxDrag.Kind.Draw } && _committed && _dragging >= 0)
        {
            var box = State.Boxes[_dragging];
            if (box.Width * CanvasPart.View.Scale < 4 || box.Height * CanvasPart.View.Scale < 4)
            {
                _selected = null;
                Retract();
            }
        }
        _drag = null;
        _dragging = -1;
    }

    public Cursor? CursorAt(Point at)
    {
        if (!Started) return null;
        if (_selected is { } s && State.Boxes[s].Covers(Showing + 1e-6))
        {
            switch (BoxDrag.Hit(at, ToRect(State.Boxes[s].At(Showing)), Tolerance, cornersOnly: false))
            {
                case BoxDrag.Edges.Left | BoxDrag.Edges.Top or BoxDrag.Edges.Right | BoxDrag.Edges.Bottom: return Cursors.SizeNWSE;
                case BoxDrag.Edges.Right | BoxDrag.Edges.Top or BoxDrag.Edges.Left | BoxDrag.Edges.Bottom: return Cursors.SizeNESW;
                case BoxDrag.Edges.Left or BoxDrag.Edges.Right: return Cursors.SizeWE;
                case BoxDrag.Edges.Top or BoxDrag.Edges.Bottom: return Cursors.SizeNS;
            }
        }
        return BoxAt(at) is not null ? Cursors.SizeAll : Cursors.Cross;
    }

    // ---- Each box's bar under the timeline

    double End(VideoBox box) => box.To ?? Duration;

    public override bool Down(TimelinePoint at, ModifierKeys modifiers)
    {
        if (!Started || at.Lane != TimelineLane.Rows || at.Row >= State.Boxes.Count) return false;
        var box = State.Boxes[at.Row];
        Select(at.Row);
        _committed = false;
        (_grabbed, _grabAt) = (box, at.Time);
        _grab = Math.Abs(at.Time - box.From) <= at.Grip ? Grab.From
            : Math.Abs(at.Time - End(box)) <= at.Grip ? Grab.To
            : at.Time > box.From && at.Time < End(box) ? Grab.Move : Grab.None;
        if (_grab == Grab.None)
        {
            // Beside the bar: the playhead goes there, as anywhere else.
            MoveTo(at.Time);
            return true;
        }
        return true;
    }

    public override void Move(TimelinePoint at, ModifierKeys modifiers)
    {
        if (_grabbed is not { } box || _grab == Grab.None || _selected is not { } index) return;
        var shortest = Frames is { } frames ? frames.Stop(0) - frames.Start(0) : 0.04;
        var end = End(box);
        VideoBox moved;
        switch (_grab)
        {
            case Grab.From:
                moved = box with { From = Math.Min(SnapCut(at.Time), end - shortest) };
                break;
            case Grab.To:
                var to = Math.Max(SnapCut(at.Time), box.From + shortest);
                moved = box with { To = to >= Duration - 0.0005 ? null : to };
                break;
            default:
                var by = Math.Clamp(at.Time - _grabAt, -box.From, Duration - end);
                var from = SnapCut(box.From + by);
                moved = box with { From = from, To = box.To is null ? null : from + (end - box.From), Track = Shifted(box.Track, from - box.From) };
                break;
        }
        if (moved == State.Boxes[index]) return;
        var next = State with { Boxes = State.Boxes.SetItem(index, moved) };
        if (_committed) Amend(next);
        else Commit(next);
        _committed = true;
        // The frame at the end being dragged, to see what it covers.
        MoveTo(_grab == Grab.To ? End(moved) - 1e-6 : moved.From);
    }

    static IReadOnlyList<BoxAt>? Shifted(IReadOnlyList<BoxAt>? track, double by) => track?.Select(p => p with { At = p.At + by }).ToList();

    public override void Up(TimelinePoint at) => (_grab, _grabbed) = (Grab.None, null);

    public override Cursor? CursorAt(TimelinePoint at)
    {
        if (!Started || at.Lane != TimelineLane.Rows || at.Row >= State.Boxes.Count) return null;
        var box = State.Boxes[at.Row];
        if (Math.Abs(at.Time - box.From) <= at.Grip || Math.Abs(at.Time - End(box)) <= at.Grip) return Cursors.SizeWE;
        return at.Time > box.From && at.Time < End(box) ? Cursors.SizeAll : null;
    }

    public override void Draw(DrawingContext dc, TimelineView view)
    {
        if (!Started) return;
        for (var i = 0; i < State.Boxes.Count; i++)
        {
            var box = State.Boxes[i];
            var (top, height) = view.Band(TimelineLane.Rows, i);
            double x0 = view.XAt(box.From), x1 = Math.Max(view.XAt(End(box)), x0 + 3);
            var bar = new Rect(x0, top + 3, x1 - x0, height - 6);
            dc.PushOpacity(i == _selected ? 1 : 0.55);
            dc.DrawRoundedRectangle(view.Accent, null, bar, 3, 3);
            dc.Pop();
            if (box.Track is { Count: > 1 } track)
                foreach (var point in track)
                    dc.DrawEllipse(Brushes.White, null, new Point(view.XAt(point.At), top + height / 2), 1.5, 1.5);
            if (i != _selected) continue;
            foreach (var x in new[] { x0, x1 })
                dc.DrawRoundedRectangle(Brushes.White, new Pen(view.Accent, 1), new Rect(x - 2.5, top + 2, 5, height - 4), 2, 2);
        }
    }

    // ---- Keys

    protected override bool OnTimelineKey(Key key, ModifierKeys modifiers, bool repeat)
    {
        if (!Started) return false;
        switch (key)
        {
            case Key.Delete or Key.Back when _selected is { } s && modifiers == ModifierKeys.None:
                _selected = null;
                Commit(State with { Boxes = State.Boxes.RemoveAt(s) });
                return true;
            case Key.Escape when _selected is not null:
                Select(null);
                return true;
            case Key.Tab when State.Boxes.Count > 0:
                // Past the last box (or the first) none is selected and Tab moves on to the next control.
                var by = modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                var next = _selected is { } current ? current + by : by > 0 ? 0 : State.Boxes.Count - 1;
                Select(next >= 0 && next < State.Boxes.Count ? next : null);
                return _selected is not null;
            case Key.Insert or Key.N when (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None:
                AddBox();
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when _selected is { } s && (modifiers.HasFlag(ModifierKeys.Alt) || modifiers.HasFlag(ModifierKeys.Control)):
                // Plain arrows step through the frames, so a box moves with Alt and the arrows and is sized with Ctrl.
                var step = modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                var (dx, dy) = key switch { Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), _ => (0, step) };
                Reshape(s, dx, dy, resize: modifiers.HasFlag(ModifierKeys.Control), repeat);
                return true;
            case Key.I when _selected is { } s && modifiers == ModifierKeys.None:
                var box = State.Boxes[s];
                if (Playhead < End(box)) Commit(State with { Boxes = State.Boxes.SetItem(s, box with { From = Playhead }) });
                return true;
            case Key.O when _selected is { } s && modifiers == ModifierKeys.None:
                var ending = State.Boxes[s];
                var to = Frames is { } frames ? frames.Stop(Frame) : Playhead;
                if (to > ending.From) Commit(State with { Boxes = State.Boxes.SetItem(s, ending with { To = to >= Duration - 0.0005 ? null : to }) });
                return true;
            default:
                return false;
        }
    }

    /// <summary>A box in the middle of the frame, a quarter of its size, from the playhead to the end, for the keyboard (Insert or N).</summary>
    internal void AddBox()
    {
        var (width, height) = (Media.Width, Media.Height);
        Commit(State with { Boxes = State.Boxes.Add(new VideoBox(width * 3 / 8, height * 3 / 8, Math.Max(1, width / 4), Math.Max(1, height / 4), Playhead)) });
        Select(State.Boxes.Count - 1);
    }

    /// <summary>Moves a box, or sizes it from its top-left corner, keeping it on the picture; a box that followed a face stops following.</summary>
    internal void Reshape(int index, int dx, int dy, bool resize, bool repeat = false)
    {
        var box = State.Boxes[index];
        var at = box.At(Playhead);
        var (width, height) = (Media.Width, Media.Height);
        var changed = resize
            ? box with { X = at.X, Y = at.Y, Width = Math.Clamp(at.Width + dx, 1, Math.Max(1, width - at.X)), Height = Math.Clamp(at.Height + dy, 1, Math.Max(1, height - at.Y)), Track = null }
            : box with { X = Math.Clamp(at.X + dx, 0, Math.Max(0, width - at.Width)), Y = Math.Clamp(at.Y + dy, 0, Math.Max(0, height - at.Height)), Width = at.Width, Height = at.Height, Track = null };
        if (changed == box) return;
        var next = State with { Boxes = State.Boxes.SetItem(index, changed) };
        if (repeat) Amend(next);
        else Commit(next);
    }

    // ---- Finding and following faces

    internal async Task FindFacesAsync()
    {
        if (!Started || _busy || Frames is not { } frames) return;
        Pause();
        _busy = true;
        _faces.IsEnabled = _follow.IsEnabled = false;
        _found.Text = Strings.LookingForFaces;
        try
        {
            var at = Frame;
            var png = await Media.FrameAsync(frames, at, 0, _closing.Token);
            using var picture = new MagickImage(png);
            var found = await Task.Run(() => Redaction.FindFacesAsync(picture));
            _found.Text = found.Count == 0 ? Strings.NoFacesOnThisFrame : string.Format(Strings.FoundHiddenFromThisFrameToThe, Describe.Count(found.Count, Tanjelo.Core.Strings.OneFace, Tanjelo.Core.Strings.NFaces));
            if (found.Count > 0)
            {
                Commit(State with { Boxes = State.Boxes.AddRange(found.Select(f => new VideoBox(f.X, f.Y, f.Width, f.Height, frames.Start(at)))) });
                Select(State.Boxes.Count - found.Count);
            }
        }
        catch (Exception ex) when (ex is ConversionException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _found.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _faces.IsEnabled = Redaction.CanFindFaces;
            ShowFollow();
        }
    }

    // Follow face is there only for a selected box with a face in it where its stretch starts, which is where following
    // starts from: looked for once for each place the box has there, once it has stayed put a moment (not at every step
    // of a drag), as finding a frame and its faces takes a moment.
    (int Frame, RedactBox Area)? _lookedAt;
    bool _faceThere;
    CancellationTokenSource? _looking;
    (int Frame, byte[] Png)? _shot;

    /// <summary>The last look for a face in the selected box, for tests to wait on.</summary>
    internal Task FaceLook { get; private set; } = Task.CompletedTask;

    void ShowFollow()
    {
        if (_selected is not { } index || _busy || !FaceFollow.CanFollow || Frames is not { } frames)
        {
            _follow.IsEnabled = false;
            return;
        }
        var box = State.Boxes[index];
        var place = (frames.At(box.From + 1e-6), box.At(box.From));
        if (_lookedAt == place)
        {
            _follow.IsEnabled = _faceThere;
            return;
        }
        _lookedAt = place;
        _faceThere = false;
        _follow.IsEnabled = false;
        _looking?.Cancel();
        _looking = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        FaceLook = LookForFaceAsync(frames, place, _looking.Token);
    }

    async Task LookForFaceAsync(FrameIndex frames, (int Frame, RedactBox Area) place, CancellationToken ct)
    {
        bool found;
        try
        {
            await Task.Delay(250, ct);
            // The frame is kept, so moving or resizing the box only looks again.
            if (_shot?.Frame != place.Frame) _shot = (place.Frame, await Media.FrameAsync(frames, place.Frame, 0, ct));
            var png = _shot.Value.Png;
            found = await Task.Run(() => FaceInAsync(png, place.Area), ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConversionException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            found = false;
        }
        if (ct.IsCancellationRequested || _lookedAt != place) return;
        _faceThere = found;
        ShowFollow();
    }

    /// <summary>
    /// Whether a face has its middle inside an area of a frame: looked for in the area and a margin around it, enlarged
    /// when small as Follow face does, so a face it finds here it finds too.
    /// </summary>
    static async Task<bool> FaceInAsync(byte[] png, RedactBox area)
    {
        using var frame = new MagickImage(png);
        var margin = Math.Max(area.Width, area.Height) / 2;
        if (new RedactBox(area.X - margin, area.Y - margin, area.Width + 2 * margin, area.Height + 2 * margin).Within((int)frame.Width, (int)frame.Height) is not { } part)
            return false;
        frame.Crop(new MagickGeometry(part.X, part.Y, (uint)part.Width, (uint)part.Height));
        frame.ResetPage();
        var scale = Math.Clamp(160.0 / Math.Max(1, area.Width), 1, 4);
        if (scale > 1) frame.Resize((uint)Math.Round(part.Width * scale), (uint)Math.Round(part.Height * scale));
        var faces = await Redaction.FindFacesAsync(frame);
        return faces.Any(f =>
        {
            var (x, y) = (part.X + (f.X + f.Width / 2.0) / scale, part.Y + (f.Y + f.Height / 2.0) / scale);
            return x >= area.X && x <= area.X + area.Width && y >= area.Y && y <= area.Y + area.Height;
        });
    }

    internal async Task FollowAsync()
    {
        if (!Started || _busy || _selected is not { } index) return;
        Pause();
        _busy = true;
        _faces.IsEnabled = _follow.IsEnabled = false;
        var box = State.Boxes[index];
        _found.Text = Strings.FollowingTheFace;
        try
        {
            var progress = new Progress<double>(f => _found.Text = string.Format(Strings.FollowingTheFace2, f));
            var start = box.At(box.From);
            var track = await Task.Run(() => FaceFollow.TrackAsync(_tools, File, start, box.From, End(box), progress, _closing.Token));
            if (track.Count < 2)
            {
                _found.Text = Strings.NoFaceToFollowInThisBox;
                return;
            }
            // Where the face was lost, the box stops, a frame after it was last seen.
            var lastSeen = track[^1].At;
            var stop = Frames is { } frames ? frames.Stop(frames.At(lastSeen)) : lastSeen + 0.04;
            var lost = stop < End(box) - 0.05;
            var followed = box with { Track = track, To = lost ? stop : box.To };
            var bounds = followed;
            Commit(State with { Boxes = State.Boxes.SetItem(index, bounds) });
            _found.Text = lost
                ? string.Format(Strings.FollowedToWhereTheFaceIsLost, Timecode.Format(stop))
                : Strings.FollowedThroughItsWholeStretch;
        }
        catch (Exception ex) when (ex is ConversionException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _found.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _faces.IsEnabled = Redaction.CanFindFaces;
            ShowFollow();
        }
    }

    public override void Close()
    {
        _closing.Cancel();
        base.Close();
    }
}

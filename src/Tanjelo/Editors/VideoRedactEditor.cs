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
/// face is lost. While paused, each box shows exactly what the saved copy will have there.
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
    readonly Dictionary<(int Frame, VideoBox Box, string Style, Colour Colour), ImageSource> _covers = [];
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

        _faces = Panels.Button("", "Find faces", "Add a box over each face on the frame showing, found by Windows on this PC", () => _ = FindFacesAsync());
        _follow = Panels.Button("", "Follow face", "Move the selected box with its face through its stretch, using Windows' face tracker", () => _ = FollowAsync());
        _faces.IsEnabled = Redaction.CanFindFaces;
        _follow.IsEnabled = false;
        if (!_faces.IsEnabled) _faces.ToolTip = "Windows can't look for faces on this PC";
        var find = new WrapPanel();
        find.Children.Add(_faces);
        find.Children.Add(_follow);
        _panel.Children.Add(Panels.Heading("Find"));
        _panel.Children.Add(find);
        _panel.Children.Add(_found);
        _panel.Children.Add(Panels.Heading("Hide with", 20));
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
        _panel.Children.Add(Panels.Heading("Areas", 20));
        _panel.Children.Add(Panels.Hint("Drag on the picture to draw a box, hidden from this frame to the end. Each box has a bar under the timeline: drag its ends to set when it starts and stops.", 0));
        _panel.Children.Add(_count);
        _panel.Children.Add(Panels.Heading("Keys", 20));
        _panel.Children.Add(Panels.Keys([("I, O", "Start the selected box here, or end it after this frame"), ("Delete", "Remove the selected box"), ("Tab", "Select the next box"), .. TimelineKeys()]));
        _panel.Children.Add(Panels.Hint("The copy is re-encoded, and loses its location, cover pictures, subtitles and data tracks such as GPS logs.", 14));
    }

    ZoomCanvas CanvasPart => Canvas!;

    public override FrameworkElement Panel => _panel;

    internal VideoRedactState Current => State;
    internal int? Selected => _selected;
    internal string Found => _found.Text;

    /// <summary>Picks a style, as its chip does.</summary>
    internal void Restyle(string style) => Commit(State with { Style = style });

    protected override VideoRedactState Initial() => new([], "solid", new Colour(0, 0, 0));

    /// <summary>With no boxes there's nothing to save, whatever the style.</summary>
    public override bool Changed => base.Changed && State.Boxes.Count > 0;

    protected override string EditorStatus => !Started ? "" : Describe.Count(State.Boxes.Count, "area") + (_selected is not null ? " · one selected" : "");

    public override Target Result()
    {
        if (State.Boxes.Count == 0) throw new ConversionException("Draw a box over something to hide first, or find faces.");
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
        _count.Text = state.Boxes.Count == 0 ? "No areas yet." : Describe.Count(state.Boxes.Count, "area") + " to hide.";
        _follow.IsEnabled = _selected is not null && FaceFollow.CanFollow && !_busy;
        Timeline.Rows = Math.Max(1, state.Boxes.Count);
        DrawCovers();
        Timeline.InvalidateVisual();
    }

    protected override void PlayheadMoved() => DrawCovers();
    protected override void PictureShown() => DrawCovers();

    // ---- What shows on the picture

    /// <summary>
    /// The boxes hidden at the playhead, in the picture's own pixels: while paused, exactly what the copy will have there,
    /// made from the frame showing; while playing, a plain cover, as working it out for every frame would be too slow.
    /// </summary>
    void DrawCovers()
    {
        if (!Started) return;
        var world = CanvasPart.World;
        world.Children.Clear();
        var now = Playhead;
        MagickImage? frame = null;
        try
        {
            foreach (var box in State.Boxes.Where(b => b.Covers(now + 1e-6)))
            {
                var area = box.At(now).Within(Media.Width, Media.Height);
                if (area is not { } a) continue;
                UIElement cover;
                if (State.Style == "solid" || Playing || Preview?.Picture is null)
                {
                    var colour = State.Style == "solid" ? Color.FromRgb(State.Colour.R, State.Colour.G, State.Colour.B) : Color.FromArgb(200, 120, 120, 120);
                    cover = new Shapes.Rectangle { Width = a.Width, Height = a.Height, Fill = new SolidColorBrush(colour) };
                }
                else
                {
                    var key = (Frame, box, State.Style, default(Colour));
                    if (!_covers.TryGetValue(key, out var made))
                    {
                        frame ??= ShownFrame();
                        if (_covers.Count > 200) _covers.Clear();
                        using var part = Redaction.Cover(frame, a, State.Style, State.Colour);
                        made = _covers[key] = Panels.Bitmap(part);
                    }
                    cover = new Image { Source = made, Width = a.Width, Height = a.Height, Stretch = Stretch.Fill };
                }
                System.Windows.Controls.Canvas.SetLeft(cover, a.X);
                System.Windows.Controls.Canvas.SetTop(cover, a.Y);
                world.Children.Add(cover);
            }
        }
        finally
        {
            frame?.Dispose();
        }
        DrawFrames();
    }

    /// <summary>The frame showing, at the picture's full size, for working out covers.</summary>
    MagickImage ShownFrame()
    {
        var picture = Preview!.Picture!;
        var converted = new FormatConvertedBitmap(picture, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[picture.PixelWidth * picture.PixelHeight * 4];
        converted.CopyPixels(pixels, picture.PixelWidth * 4, 0);
        var image = new MagickImage();
        image.ReadPixels(pixels, new PixelReadSettings((uint)picture.PixelWidth, (uint)picture.PixelHeight, StorageType.Char, PixelMapping.BGRA));
        image.Alpha(AlphaAction.Off);
        if (picture.PixelWidth != Media.Width || picture.PixelHeight != Media.Height)
            image.Resize(new MagickGeometry((uint)Media.Width, (uint)Media.Height) { IgnoreAspectRatio = true });
        return image;
    }

    /// <summary>The boxes' outlines, in screen units so they stay thin at any zoom; the selected one has handles, and is dashed when it isn't hidden now.</summary>
    void DrawFrames()
    {
        var overlay = CanvasPart.Overlay;
        overlay.Children.Clear();
        if (!Started) return;
        var now = Playhead;
        for (var i = 0; i < State.Boxes.Count; i++)
        {
            var box = State.Boxes[i];
            var active = box.Covers(now + 1e-6);
            var selected = i == _selected;
            if (!active && !selected) continue;
            var at = box.At(now);
            var rect = CanvasPart.View.ToCanvas(new Rect(at.X, at.Y, at.Width, at.Height));
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
        _follow.IsEnabled = _selected is not null && FaceFollow.CanFollow && !_busy;
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
        var now = Playhead + 1e-6;
        if (_selected is { } s && State.Boxes[s].Covers(now)
            && BoxDrag.Hit(at, ToRect(State.Boxes[s].At(Playhead)), Tolerance, cornersOnly: false) != BoxDrag.Edges.None) return s;
        for (var i = State.Boxes.Count - 1; i >= 0; i--)
            if (State.Boxes[i].Covers(now) && ToRect(State.Boxes[i].At(Playhead)).Contains(at)) return i;
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
            _drag = BoxDrag.Start(at, ToRect(State.Boxes[index].At(Playhead)), Bounds, null, Tolerance);
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
        if (_selected is { } s && State.Boxes[s].Covers(Playhead + 1e-6))
        {
            switch (BoxDrag.Hit(at, ToRect(State.Boxes[s].At(Playhead)), Tolerance, cornersOnly: false))
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
                var by = modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                Select(((_selected ?? (by > 0 ? -1 : 0)) + by + State.Boxes.Count) % State.Boxes.Count);
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

    // ---- Finding and following faces

    internal async Task FindFacesAsync()
    {
        if (!Started || _busy || Frames is not { } frames) return;
        Pause();
        _busy = true;
        _faces.IsEnabled = _follow.IsEnabled = false;
        _found.Text = "Looking for faces…";
        try
        {
            var at = Frame;
            var png = await Media.FrameAsync(frames, at, 0, _closing.Token);
            using var picture = new MagickImage(png);
            var found = await Task.Run(() => Redaction.FindFacesAsync(picture));
            _found.Text = found.Count == 0 ? "No faces on this frame." : $"{Describe.Count(found.Count, "face")} found, hidden from this frame to the end. Select one and press Follow face to have its box move with it.";
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
            _follow.IsEnabled = _selected is not null && FaceFollow.CanFollow;
        }
    }

    internal async Task FollowAsync()
    {
        if (!Started || _busy || _selected is not { } index) return;
        Pause();
        _busy = true;
        _faces.IsEnabled = _follow.IsEnabled = false;
        var box = State.Boxes[index];
        _found.Text = "Following the face…";
        try
        {
            var progress = new Progress<double>(f => _found.Text = $"Following the face… {f:0%}");
            var start = box.At(box.From);
            var track = await Task.Run(() => FaceFollow.TrackAsync(_tools, File, start, box.From, End(box), progress, _closing.Token));
            if (track.Count < 2)
            {
                _found.Text = "No face to follow in this box. Draw it around a face, or use Find faces.";
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
                ? $"Followed to {Timecode.Format(stop)}, where the face is lost; the box stops there. Drag the end of its bar to go on."
                : "Followed through its whole stretch.";
        }
        catch (Exception ex) when (ex is ConversionException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _found.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _faces.IsEnabled = Redaction.CanFindFaces;
            _follow.IsEnabled = _selected is not null && FaceFollow.CanFollow;
        }
    }

    public override void Close()
    {
        _closing.Cancel();
        base.Close();
    }
}

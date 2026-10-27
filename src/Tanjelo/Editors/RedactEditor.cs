using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageMagick;
using Tanjelo.Core;
using Tanjelo.Core.Converters;
using Shapes = System.Windows.Shapes;

namespace Tanjelo.Editors;

/// <summary>What the redaction editor saves: the areas, in the picture's pixels, and how they're hidden.</summary>
sealed record RedactState(ImmutableList<RedactBox> Boxes, string Style, Colour Colour);

/// <summary>
/// Redact: draw boxes over anything to hide, or have faces and text found, then move and resize them with the crop box's
/// drag. Each box shows exactly what the saved copy will have there, made by the engine from the full picture.
/// </summary>
sealed class RedactEditor : Editor<RedactState>, ICanvasTool
{
    static readonly string[] Colours = ["#000000", "#FFFFFF", "#5F5F5F", "#C2410C", "#3A7BD5"];

    /// <summary>How near an edge, in screen pixels, still counts as on it.</summary>
    const double Grip = 7;

    readonly string _file;
    readonly Target _target;
    readonly ZoomCanvas _canvas = new();
    readonly StackPanel _panel = new();
    readonly TextBlock _found = Panels.Hint("", 8);
    readonly TextBlock _count = Panels.Hint("", 10);
    readonly Button _faces, _text;
    readonly StackPanel _colourRow = new();
    readonly Dictionary<(RedactBox, string, Colour), ImageSource> _covers = [];
    Action<string> _showStyle = _ => { };
    Action<Colour> _showColour = _ => { };
    MagickImage? _picture;
    int? _selected;
    bool _busy;

    // A drag on the canvas: what it does, and whether it has changed anything yet.
    BoxDrag? _drag;
    int _dragging = -1;
    bool _committed;

    public RedactEditor(string file, Target target)
    {
        _file = file;
        _target = target;
        _canvas.Tool = this;
        _canvas.ViewChanged += DrawFrames;

        _faces = Panels.Button("", "Find faces", "Add a box over each face Windows finds, on this PC", () => _ = FindAsync(faces: true));
        _text = Panels.Button("", "Find text", "Add a box over each word Windows' OCR reads, on this PC", () => _ = FindAsync(faces: false));
        _faces.IsEnabled = Redaction.CanFindFaces;
        _text.IsEnabled = Env.Ocr;
        if (!_faces.IsEnabled) _faces.ToolTip = "Windows can't look for faces on this PC";
        if (!_text.IsEnabled) _text.ToolTip = "Finding text needs an OCR language in Windows' language settings";
        var find = new WrapPanel();
        find.Children.Add(_faces);
        find.Children.Add(_text);

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
        _panel.Children.Add(Panels.Hint("Solid is the safest. Pixelate and blur are strong here, but a light blur can sometimes be undone.", 8));
        _panel.Children.Add(Panels.Heading("Areas", 20));
        _panel.Children.Add(Panels.Hint("Drag on the picture to draw a box. Drag a box to move it or its edges to resize it; Delete removes the selected one, and the arrows nudge it.", 0));
        _panel.Children.Add(_count);
        _panel.Children.Add(Panels.Hint("The copy also loses its location, its XMP and the small preview cameras keep in EXIF.", 14));
    }

    /// <summary>Whether Windows has an OCR language; looked up once.</summary>
    static class Env
    {
        public static readonly bool Ocr = Redaction.CanFindText;
    }

    public override FrameworkElement Main => _canvas;
    public override FrameworkElement Panel => _panel;
    public override ZoomCanvas Canvas => _canvas;
    public override string Summary => _picture is null ? "" : Describe.Pixels((int)_picture.Width, (int)_picture.Height);
    public override string Status => _picture is null ? "" : Describe.Count(State.Boxes.Count, "area") + (_selected is not null ? " · one selected" : "");

    internal RedactState Current => State;
    internal int? Selected => _selected;

    public override async Task LoadAsync(CancellationToken cancel)
    {
        _picture = await Task.Run(() => Redaction.ReadUpright(_file), cancel);
        var bitmap = await Task.Run(() => Panels.Bitmap(_picture), cancel);
        _canvas.Show(bitmap, new Size(_picture.Width, _picture.Height));
        Begin(new RedactState([], "solid", new Colour(0, 0, 0)));
    }

    public override Target Result()
    {
        if (State.Boxes.Count == 0) throw new ConversionException("Draw a box over something to hide first, or find faces or text.");
        return _target.WithParameters(new Dictionary<string, string>
        {
            ["boxes"] = RedactBox.FormatAll(State.Boxes),
            ["faces"] = "false",
            ["text"] = "false",
            ["style"] = State.Style,
            ["colour"] = State.Colour.ToString(),
        });
    }

    protected override void Show(RedactState state)
    {
        if (_selected >= state.Boxes.Count) _selected = null;
        _showStyle(state.Style);
        _showColour(state.Colour);
        _colourRow.Visibility = state.Style == "solid" ? Visibility.Visible : Visibility.Collapsed;
        _count.Text = state.Boxes.Count == 0 ? "No areas yet." : Describe.Count(state.Boxes.Count, "area") + " to hide.";
        // Covers, in the picture's own pixels: exactly what the engine will put there.
        _canvas.World.Children.Clear();
        foreach (var box in state.Boxes)
        {
            var cover = new Image { Source = Cover(box, state), Width = box.Width, Height = box.Height, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.NearestNeighbor);
            System.Windows.Controls.Canvas.SetLeft(cover, box.X);
            System.Windows.Controls.Canvas.SetTop(cover, box.Y);
            _canvas.World.Children.Add(cover);
        }
        DrawFrames();
    }

    ImageSource Cover(RedactBox box, RedactState state)
    {
        var key = (box, state.Style, state.Style == "solid" ? state.Colour : default);
        if (_covers.TryGetValue(key, out var known)) return known;
        if (_covers.Count > 400) _covers.Clear();
        using var cover = Redaction.Cover(_picture!, box, state.Style, state.Colour);
        return _covers[key] = Panels.Bitmap(cover);
    }

    /// <summary>The boxes' outlines over the picture, in screen units so they stay thin at any zoom; the selected one has handles.</summary>
    void DrawFrames()
    {
        _canvas.Overlay.Children.Clear();
        if (_picture is null || !Started) return;
        for (var i = 0; i < State.Boxes.Count; i++)
        {
            var box = State.Boxes[i];
            var rect = _canvas.View.ToCanvas(new Rect(box.X, box.Y, box.Width, box.Height));
            var selected = i == _selected;
            AddFrame(rect, Color.FromArgb(150, 0, 0, 0), 3.5, null);
            AddFrame(rect, selected ? Colors.White : Color.FromArgb(220, 255, 255, 255), 1.5, selected ? null : new DoubleCollection { 4, 3 });
            if (!selected) continue;
            foreach (var spot in Handles(rect))
            {
                var handle = new Shapes.Rectangle
                {
                    Width = 9,
                    Height = 9,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                    StrokeThickness = 1,
                };
                System.Windows.Controls.Canvas.SetLeft(handle, spot.X - 4.5);
                System.Windows.Controls.Canvas.SetTop(handle, spot.Y - 4.5);
                _canvas.Overlay.Children.Add(handle);
            }
        }
    }

    void AddFrame(Rect rect, Color colour, double thickness, DoubleCollection? dashes)
    {
        var frame = new Shapes.Rectangle { Width = rect.Width, Height = rect.Height, Stroke = new SolidColorBrush(colour), StrokeThickness = thickness, StrokeDashArray = dashes };
        System.Windows.Controls.Canvas.SetLeft(frame, rect.X);
        System.Windows.Controls.Canvas.SetTop(frame, rect.Y);
        _canvas.Overlay.Children.Add(frame);
    }

    static IEnumerable<Point> Handles(Rect rect) =>
    [
        rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft,
        new(rect.Left + rect.Width / 2, rect.Top), new(rect.Right, rect.Top + rect.Height / 2),
        new(rect.Left + rect.Width / 2, rect.Bottom), new(rect.Left, rect.Top + rect.Height / 2),
    ];

    // ---- Finding faces and text

    internal async Task FindAsync(bool faces)
    {
        if (_picture is null || _busy) return;
        _busy = true;
        _faces.IsEnabled = _text.IsEnabled = false;
        _found.Text = faces ? "Looking for faces…" : "Reading the text…";
        try
        {
            // A copy of its own, as the boxes keep being drawn from the picture meanwhile.
            using var picture = _picture.Clone();
            var found = faces
                ? await Task.Run(() => Redaction.FindFacesAsync(picture))
                : await Task.Run(() => Redaction.FindTextAsync(picture)) ?? [];
            var known = State.Boxes.ToHashSet();
            var added = found.Where(b => !known.Contains(b)).ToList();
            _found.Text = (faces, found.Count) switch
            {
                (true, 0) => "No faces were found.",
                (false, 0) => "No text was found.",
                (true, var n) => $"{Describe.Count(n, "face")} found. Check each box, and remove any that aren't needed.",
                (false, var n) => $"{Describe.Count(n, "word")} found. Check each box, and remove any that aren't needed.",
            };
            if (added.Count > 0) Commit(State with { Boxes = State.Boxes.AddRange(added) });
        }
        catch (Exception ex) when (ex is ConversionException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _found.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            _faces.IsEnabled = Redaction.CanFindFaces;
            _text.IsEnabled = Env.Ocr;
        }
    }

    // ---- Drawing, moving and resizing boxes, in the picture's own pixels

    Size Bounds => new(_picture!.Width, _picture.Height);

    double Tolerance => Grip / _canvas.View.Scale;

    static Rect ToRect(RedactBox box) => new(box.X, box.Y, box.Width, box.Height);

    /// <summary>The box a point is on, the selected one first since it's drawn over the rest, then the last drawn.</summary>
    int? BoxAt(Point at)
    {
        var tolerance = Tolerance;
        if (_selected is { } s && BoxDrag.Hit(at, ToRect(State.Boxes[s]), tolerance, cornersOnly: false) != BoxDrag.Edges.None) return s;
        for (var i = State.Boxes.Count - 1; i >= 0; i--)
            if (ToRect(State.Boxes[i]).Contains(at)) return i;
        return null;
    }

    public void Down(Point at, ModifierKeys modifiers, int clicks)
    {
        if (_picture is null) return;
        _committed = false;
        if (BoxAt(at) is { } index)
        {
            _selected = index;
            _dragging = index;
            _drag = BoxDrag.Start(at, ToRect(State.Boxes[index]), Bounds, null, Tolerance);
        }
        else
        {
            // Started away from every box: a new one, drawn from here.
            _selected = null;
            _dragging = -1;
            _drag = BoxDrag.Start(at, new Rect(-1e6, -1e6, 0, 0), Bounds, null, Tolerance);
        }
        DrawFrames();
        RaiseUpdated();
    }

    public void Move(Point at)
    {
        if (_drag is null) return;
        var rect = _drag.To(at);
        var box = new RedactBox((int)Math.Round(rect.X), (int)Math.Round(rect.Y), Math.Max(1, (int)Math.Round(rect.Width)), Math.Max(1, (int)Math.Round(rect.Height)));
        if (_dragging < 0)
        {
            // A new box counts once it's big enough to see, so a stray click doesn't add one.
            if (rect.Width * _canvas.View.Scale < 4 && rect.Height * _canvas.View.Scale < 4) return;
            _dragging = State.Boxes.Count;
            _selected = _dragging;
            Commit(State with { Boxes = State.Boxes.Add(box) });
            _committed = true;
            return;
        }
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
            // Drawn and then shrunk back to almost nothing: as if it never was.
            if (box.Width * _canvas.View.Scale < 4 || box.Height * _canvas.View.Scale < 4)
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
        if (_picture is null) return null;
        if (_selected is { } s)
        {
            var edges = BoxDrag.Hit(at, ToRect(State.Boxes[s]), Tolerance, cornersOnly: false);
            switch (edges)
            {
                case BoxDrag.Edges.Left | BoxDrag.Edges.Top or BoxDrag.Edges.Right | BoxDrag.Edges.Bottom: return Cursors.SizeNWSE;
                case BoxDrag.Edges.Right | BoxDrag.Edges.Top or BoxDrag.Edges.Left | BoxDrag.Edges.Bottom: return Cursors.SizeNESW;
                case BoxDrag.Edges.Left or BoxDrag.Edges.Right: return Cursors.SizeWE;
                case BoxDrag.Edges.Top or BoxDrag.Edges.Bottom: return Cursors.SizeNS;
            }
        }
        return BoxAt(at) is not null ? Cursors.SizeAll : Cursors.Cross;
    }

    public override bool OnKey(KeyEventArgs e)
    {
        if (_picture is null || Keyboard.FocusedElement is TextBox) return false;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Delete or Key.Back when _selected is not null:
                RemoveSelected();
                return true;
            case Key.Escape when _selected is not null:
                _selected = null;
                DrawFrames();
                RaiseUpdated();
                return true;
            case Key.Tab when State.Boxes.Count > 0:
                var by = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                _selected = ((_selected ?? (by > 0 ? -1 : 0)) + by + State.Boxes.Count) % State.Boxes.Count;
                DrawFrames();
                RaiseUpdated();
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when _selected is { } s:
                Nudge(s, e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 }, e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 }, e.IsRepeat);
                return true;
            default:
                return false;
        }
    }

    internal void RemoveSelected()
    {
        if (_selected is not { } s) return;
        _selected = null;
        Commit(State with { Boxes = State.Boxes.RemoveAt(s) });
    }

    /// <summary>Moves a box by the keyboard, staying on the picture; held arrows make one undo step.</summary>
    internal void Nudge(int index, int dx, int dy, bool repeat = false)
    {
        var box = State.Boxes[index];
        var moved = box with
        {
            X = Math.Clamp(box.X + dx, 0, Math.Max(0, (int)_picture!.Width - box.Width)),
            Y = Math.Clamp(box.Y + dy, 0, Math.Max(0, (int)_picture.Height - box.Height)),
        };
        if (moved == box) return;
        var next = State with { Boxes = State.Boxes.SetItem(index, moved) };
        if (repeat) Amend(next);
        else Commit(next);
    }
}

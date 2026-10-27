using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tanjelo.Core;
using Tanjelo.Core.Converters;
using Shapes = System.Windows.Shapes;

namespace Tanjelo.Editors;

/// <summary>Draws marks with the engine's own renderer, in the picture's pixels, so the screen shows what's saved.</summary>
sealed class MarksLayer : FrameworkElement
{
    public IReadOnlyList<Mark> Marks { get; set; } = [];
    public ImageSource? Picture { get; set; }
    public Size PictureSize { get; set; }

    public MarksLayer() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext context) => MarkRenderer.Draw(context, Marks, Picture, PictureSize);
}

/// <summary>
/// Annotate: arrows, lines, rectangles, ellipses, text, numbered callouts, a magnifier, a pen and a highlighter, each in
/// a colour and a width. Marks stay editable until saving: pick one to move it, pull its handles, change its colour or
/// width, or delete it.
/// </summary>
sealed class AnnotateEditor : Editor<ImmutableList<Mark>>, ICanvasTool
{
    internal enum Tool { Select, Arrow, Line, Rectangle, Ellipse, Text, Callout, Magnifier, Pen, Highlighter }

    static readonly (Tool Tool, string Glyph, string Name, Key Key, string Hint)[] Tools =
    [
        (Tool.Select, "", "Select", Key.V, "Click a mark to pick it; drag it or its handles"),
        (Tool.Arrow, "", "Arrow", Key.A, "Drag from the tail to the point; Shift keeps to 45°"),
        (Tool.Line, "", "Line", Key.L, "Drag from one end to the other; Shift keeps to 45°"),
        (Tool.Rectangle, "", "Rectangle", Key.R, "Drag from corner to corner; Shift makes a square"),
        (Tool.Ellipse, "", "Ellipse", Key.E, "Drag across it; Shift makes a circle"),
        (Tool.Text, "", "Text", Key.T, "Click where the text starts and type; Enter finishes"),
        (Tool.Callout, "", "Callout", Key.N, "Click to place the next number"),
        (Tool.Magnifier, "", "Magnifier", Key.M, "Click what to enlarge; pull the ring's handle to resize it"),
        (Tool.Pen, "", "Pen", Key.P, "Draw freehand"),
        (Tool.Highlighter, "", "Highlighter", Key.H, "Draw over what to highlight"),
    ];

    static readonly string[] Colours = ["#E5383B", "#F97A3D", "#FFD400", "#2E8540", "#3A7BD5", "#1B1B1B", "#FFFFFF"];
    static readonly Choice[] Widths = [new("1", "Thin"), new("2", "Medium"), new("4", "Thick")];
    static readonly Choice[] Sizes = [new("1", "Small"), new("1.6", "Medium"), new("2.6", "Large")];

    /// <summary>How near a mark or handle, in screen pixels, still counts as on it.</summary>
    const double Grip = 7;

    readonly string _file;
    readonly Target _target;
    readonly ZoomCanvas _canvas = new();
    readonly MarksLayer _layer = new();
    readonly WrapPanel _toolbar = new();
    readonly StackPanel _panel = new();
    readonly Dictionary<Tool, RadioButton> _toolButtons = [];
    readonly FrameworkElement _sizeRow;
    Action<Colour> _showColour = _ => { };
    Action<string> _showWidth = _ => { }, _showSize = _ => { };
    Tool _tool = Tool.Arrow;
    Colour _colour;
    double _width = 2, _size = 1.6; // shares of the picture's unit
    double _unit = 1;               // a thousandth of the picture's longer side, at least a pixel
    int? _selected;

    // A gesture on the canvas.
    enum Grab { None, Draw, Move, Start, End, Box, Size }
    Grab _grab;
    Point _from;
    Mark? _before;
    BoxDrag? _boxDrag;
    bool _committed;

    // Text being typed: the box, and the mark it edits (or null for a new one).
    TextBox? _typing;
    int? _typingFor;
    Point _typingAt;

    public AnnotateEditor(string file, Target target)
    {
        _file = file;
        _target = target;
        Colour.TryParse(Colours[0], out _colour);
        _canvas.Tool = this;
        _canvas.World.Children.Add(_layer);
        _canvas.ViewChanged += () =>
        {
            DrawSelection();
            PlaceTyping();
        };

        foreach (var (tool, glyph, name, key, hint) in Tools)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(tool == Tool.Callout ? CalloutIcon() : Panels.Themed(
                new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center }, TextBlock.FontFamilyProperty, "IconFont"));
            var button = Panels.Themed(new RadioButton
            {
                Content = content,
                GroupName = "annotate-tools",
                ToolTip = $"{name} ({key}): {hint}",
                IsChecked = tool == _tool,
                Margin = new Thickness(0, 0, 4, 4),
            }, FrameworkElement.StyleProperty, "Chip");
            button.Checked += (_, _) => Use(tool);
            _toolButtons[tool] = button;
            _toolbar.Children.Add(button);
        }

        var (swatches, showColour) = Panels.Swatches(Colours, _colour, colour =>
        {
            _colour = colour;
            _showColour(colour);
            Restyle(m => m with { Colour = colour.ToString() });
        });
        _showColour = showColour;
        var (widths, showWidth) = Panels.Chips(Widths, "2", value =>
        {
            _width = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            Restyle(m => m.Kind is MarkKind.Text or MarkKind.Callout ? m : m with { Width = LineWidth });
        });
        _showWidth = showWidth;
        var (sizes, showSize) = Panels.Chips(Sizes, "1.6", value =>
        {
            _size = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            Restyle(m => m.Kind switch
            {
                MarkKind.Text => m with { Size = TextSize },
                MarkKind.Callout => m with { Size = CalloutSize },
                _ => m,
            });
        });
        _showSize = showSize;
        _panel.Children.Add(Panels.Heading("Colour"));
        _panel.Children.Add(swatches);
        _panel.Children.Add(Panels.Heading("Line", 14));
        _panel.Children.Add(widths);
        var sizeRow = new StackPanel();
        sizeRow.Children.Add(Panels.Heading("Text and numbers", 20));
        sizeRow.Children.Add(sizes);
        _sizeRow = sizeRow;
        _panel.Children.Add(sizeRow);
        _panel.Children.Add(Panels.Hint("Changes go to the selected mark, and to the ones you draw next.", 14));
        _panel.Children.Add(Panels.Hint("Every mark stays editable until you save: pick it with Select (V), move it, pull its handles, " +
            "or press Delete. Double-click text to change it. The arrows nudge the selected mark.", 10));
    }

    static FrameworkElement CalloutIcon()
    {
        var circle = Panels.Themed(new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.4) }, Border.BorderBrushProperty, "TextPrimary");
        circle.Child = new TextBlock { Text = "1", FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return circle;
    }

    public override FrameworkElement Main => _canvas;
    public override FrameworkElement Toolbar => _toolbar;
    public override FrameworkElement Panel => _panel;
    public override ZoomCanvas Canvas => _canvas;
    public override string Summary => _layer.PictureSize.Width > 0 ? Describe.Pixels((int)_layer.PictureSize.Width, (int)_layer.PictureSize.Height) : "";

    public override string Status => !Started ? "" :
        Describe.Count(State.Count, "mark") + " · " + Tools.First(t => t.Tool == _tool).Hint;

    internal ImmutableList<Mark> Marks => State;
    internal int? Selected => _selected;
    internal TextBox? Typing => _typing;

    double LineWidth => Math.Max(2, Math.Round(3 * _width * _unit, 1));
    double TextSize => Math.Max(10, Math.Round(24 * _size * _unit));
    double CalloutSize => Math.Max(16, Math.Round(34 * _size * _unit));
    double MagnifierSize => Math.Round(220 * _unit);

    public override async Task LoadAsync(CancellationToken cancel)
    {
        using var picture = await Task.Run(() => Redaction.ReadUpright(_file), cancel);
        var bitmap = await Task.Run(() => Panels.Bitmap(picture), cancel);
        var size = new Size(picture.Width, picture.Height);
        _unit = Math.Max(1, Math.Max(size.Width, size.Height) / 1000);
        (_layer.Picture, _layer.PictureSize, _layer.Width, _layer.Height) = (bitmap, size, size.Width, size.Height);
        _canvas.Show(bitmap, size);
        Begin([]);
    }

    public override Target Result()
    {
        EndText(keep: true);
        if (State.Count == 0) throw new ConversionException("Draw something first.");
        return _target.WithParameters(new Dictionary<string, string> { ["spec"] = MarkSpec.Write(State) });
    }

    protected override void Show(ImmutableList<Mark> marks)
    {
        if (_selected >= marks.Count) _selected = null;
        _layer.Marks = marks;
        _layer.InvalidateVisual();
        DrawSelection();
    }

    internal void Use(Tool tool)
    {
        EndText(keep: true);
        _tool = tool;
        if (_toolButtons[tool].IsChecked != true) _toolButtons[tool].IsChecked = true;
        if (tool != Tool.Select && _selected is not null)
        {
            _selected = null;
            DrawSelection();
        }
        _canvas.Cursor = null;
        RaiseUpdated();
    }

    /// <summary>A new style for the selected mark, as one step.</summary>
    void Restyle(Func<Mark, Mark> change)
    {
        if (_selected is not { } s) return;
        var changed = change(State[s]);
        if (changed != State[s]) Commit(State.SetItem(s, changed));
    }

    /// <summary>Shows the panel's choices as the selected mark has them.</summary>
    void Reflect(Mark mark)
    {
        if (Colour.TryParse(mark.Colour, out var colour))
        {
            _colour = colour;
            _showColour(colour);
        }
        _sizeRow.Visibility = Visibility.Visible;
    }

    // ---- The selection, drawn in screen units

    void DrawSelection()
    {
        _canvas.Overlay.Children.Clear();
        if (!Started || _selected is not { } s || s >= State.Count) return;
        var mark = State[s];
        var view = _canvas.View;
        var frame = view.ToCanvas(MarkRenderer.Bounds(mark));
        frame.Inflate(4, 4);
        var outline = new Shapes.Rectangle { Width = frame.Width, Height = frame.Height, StrokeThickness = 1, StrokeDashArray = [4, 3] };
        outline.SetResourceReference(Shapes.Shape.StrokeProperty, "Accent");
        System.Windows.Controls.Canvas.SetLeft(outline, frame.X);
        System.Windows.Controls.Canvas.SetTop(outline, frame.Y);
        _canvas.Overlay.Children.Add(outline);
        foreach (var (_, at) in Handles(mark))
        {
            var spot = view.ToCanvas(at);
            var handle = new Shapes.Ellipse { Width = 10, Height = 10, Fill = Brushes.White, StrokeThickness = 1.5 };
            handle.SetResourceReference(Shapes.Shape.StrokeProperty, "Accent");
            System.Windows.Controls.Canvas.SetLeft(handle, spot.X - 5);
            System.Windows.Controls.Canvas.SetTop(handle, spot.Y - 5);
            _canvas.Overlay.Children.Add(handle);
        }
    }

    /// <summary>The handles a mark has, in the picture's pixels, and what pulling each does.</summary>
    static IEnumerable<(Grab Grab, Point At)> Handles(Mark mark) => mark.Kind switch
    {
        MarkKind.Arrow or MarkKind.Line => [(Grab.Start, mark.Start), (Grab.End, mark.End)],
        MarkKind.Rectangle or MarkKind.Ellipse => BoxCorners(new Rect(mark.Start, mark.End)).Select(p => (Grab.Box, p)),
        MarkKind.Callout or MarkKind.Magnifier => [(Grab.Size, mark.Start + new Vector(mark.Size / 2, 0))],
        MarkKind.Text => [(Grab.Size, MarkRenderer.Bounds(mark).BottomRight)],
        _ => [],
    };

    static Point[] BoxCorners(Rect box) => [box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft];

    // ---- The pointer, in the picture's own pixels

    double Tolerance => Grip / _canvas.View.Scale;

    int? MarkAt(Point at)
    {
        for (var i = State.Count - 1; i >= 0; i--)
            if (MarkRenderer.Hits(State[i], at, Tolerance)) return i;
        return null;
    }

    public void Down(Point at, ModifierKeys modifiers, int clicks)
    {
        if (!Started) return;
        EndText(keep: true);
        _from = at;
        _committed = false;
        _grab = Grab.None;
        _boxDrag = null;
        if (_tool == Tool.Select)
        {
            if (_selected is { } s && Handles(State[s]).FirstOrDefault(h => (h.At - at).Length <= Tolerance * 1.4) is { Grab: not Grab.None } handle)
            {
                _grab = handle.Grab;
                _before = State[s];
                if (handle.Grab == Grab.Box)
                    _boxDrag = BoxDrag.Start(at, new Rect(_before.Start, _before.End), new Size(double.MaxValue / 4, double.MaxValue / 4), null, Tolerance * 1.4);
                return;
            }
            _selected = MarkAt(at);
            if (_selected is { } picked)
            {
                _before = State[picked];
                Reflect(_before);
                if (clicks == 2 && _before.Kind == MarkKind.Text)
                {
                    BeginText(_before.Start, picked);
                    return;
                }
                _grab = Grab.Move;
            }
            DrawSelection();
            RaiseUpdated();
            return;
        }

        switch (_tool)
        {
            case Tool.Text:
                if (MarkAt(at) is { } existing && State[existing].Kind == MarkKind.Text) BeginText(State[existing].Start, existing);
                else BeginText(at, null);
                return;
            case Tool.Callout or Tool.Magnifier:
                // Placed at once, then dragged into place while the button is down.
                var placed = New(_tool == Tool.Callout ? MarkKind.Callout : MarkKind.Magnifier, [at]);
                _selected = State.Count;
                Commit(State.Add(placed));
                _committed = true;
                _before = placed;
                _grab = Grab.Move;
                return;
            default:
                _grab = Grab.Draw;
                return;
        }
    }

    public void Move(Point at)
    {
        if (_grab == Grab.None || !Started) return;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_grab == Grab.Draw)
        {
            if (!_committed && (at - _from).Length * _canvas.View.Scale < 3) return;
            var kind = (MarkKind)Enum.Parse(typeof(MarkKind), _tool.ToString());
            var mark = kind is MarkKind.Pen or MarkKind.Highlighter
                ? (_committed ? State[^1] with { Points = State[^1].Points.Add(MarkPoint.From(at)) } : New(kind, [_from, at]))
                : New(kind, [_from, Constrained(kind, _from, at, shift)]);
            if (_committed)
            {
                Amend(State.SetItem(State.Count - 1, mark));
            }
            else
            {
                _selected = State.Count;
                Commit(State.Add(mark));
                _committed = true;
            }
            return;
        }
        if (_selected is not { } s || _before is null) return;
        var next = _grab switch
        {
            Grab.Move => _before.Moved(at - _from),
            Grab.Start => _before with { Points = [MarkPoint.From(Constrained(_before.Kind, _before.End, at, shift)), _before.Points[1]] },
            Grab.End => _before with { Points = [_before.Points[0], MarkPoint.From(Constrained(_before.Kind, _before.Start, at, shift))] },
            Grab.Box when _boxDrag is not null => BoxTo(_before, _boxDrag.To(at)),
            Grab.Size when _before.Kind == MarkKind.Text => _before with { Size = Math.Max(6, _before.Size * (at.Y - _before.Start.Y) / Math.Max(1, MarkRenderer.Bounds(_before).Height)) },
            Grab.Size => _before with { Size = Math.Max(12, 2 * (at - _before.Start).Length) },
            _ => _before,
        };
        if (next == State[s]) return;
        if (_committed) Amend(State.SetItem(s, next));
        else Commit(State.SetItem(s, next));
        _committed = true;
    }

    public void Up(Point at)
    {
        // A click with a drawing tool, without dragging, makes a dot with the pen and nothing with the others.
        if (_grab == Grab.Draw && !_committed && _tool is Tool.Pen or Tool.Highlighter)
        {
            _selected = State.Count;
            Commit(State.Add(New(_tool == Tool.Pen ? MarkKind.Pen : MarkKind.Highlighter, [_from])));
        }
        _grab = Grab.None;
        _before = null;
        _boxDrag = null;
    }

    public Cursor? CursorAt(Point at)
    {
        if (!Started) return null;
        if (_tool != Tool.Select) return _tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        if (_selected is { } s && Handles(State[s]).Any(h => (h.At - at).Length <= Tolerance * 1.4)) return Cursors.SizeNWSE;
        return MarkAt(at) is not null ? Cursors.SizeAll : null;
    }

    Mark New(MarkKind kind, IEnumerable<Point> points) => new()
    {
        Kind = kind,
        Points = [.. points.Select(MarkPoint.From)],
        Colour = _colour.ToString(),
        Width = kind == MarkKind.Magnifier ? Math.Max(2, Math.Round(3 * _unit)) : kind == MarkKind.Highlighter ? LineWidth * 4 : LineWidth,
        Size = kind switch { MarkKind.Text => TextSize, MarkKind.Callout => CalloutSize, MarkKind.Magnifier => MagnifierSize, _ => 48 },
    };

    static Mark BoxTo(Mark mark, Rect box) => mark with { Points = [MarkPoint.From(box.TopLeft), MarkPoint.From(box.BottomRight)] };

    /// <summary>With Shift: lines keep to the nearest 45°, and boxes to a square.</summary>
    static Point Constrained(MarkKind kind, Point from, Point to, bool shift)
    {
        if (!shift) return to;
        var d = to - from;
        if (kind is MarkKind.Rectangle or MarkKind.Ellipse)
        {
            var side = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
            return from + new Vector(Math.Sign(d.X) * side, Math.Sign(d.Y) * side);
        }
        var angle = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
        return from + new Vector(Math.Cos(angle), Math.Sin(angle)) * d.Length;
    }

    // ---- Typing text

    /// <summary>Opens a box for typing at a picture point, for a new text mark or to change one.</summary>
    internal void BeginText(Point at, int? existing)
    {
        EndText(keep: true);
        _typingAt = at;
        _typingFor = existing;
        var mark = existing is { } i ? State[i] : New(MarkKind.Text, [at]);
        var colour = Colour.TryParse(mark.Colour, out var c) ? Color.FromRgb(c.R, c.G, c.B) : Colors.Red;
        _typing = new TextBox
        {
            Text = mark.Text ?? "",
            AcceptsReturn = false,
            Background = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            Foreground = new SolidColorBrush(colour),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            MinWidth = 40,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
            Tag = mark.Size,
        };
        _typing.SetResourceReference(Control.BorderBrushProperty, "Accent");
        _typing.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                EndText(keep: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                EndText(keep: false);
                e.Handled = true;
            }
        };
        _typing.LostKeyboardFocus += (_, _) => EndText(keep: true);
        _canvas.Controls.Children.Add(_typing);
        PlaceTyping();
        _typing.Focus();
        _typing.SelectAll();
        if (existing is not null)
        {
            // The mark hides while it's being typed over.
            _layer.Marks = State.RemoveAt(existing.Value);
            _layer.InvalidateVisual();
        }
        RaiseUpdated();
    }

    void PlaceTyping()
    {
        if (_typing is null) return;
        var spot = _canvas.View.ToCanvas(_typingAt);
        _typing.FontSize = Math.Max(4, (double)_typing.Tag * _canvas.View.Scale);
        System.Windows.Controls.Canvas.SetLeft(_typing, spot.X - 1);
        System.Windows.Controls.Canvas.SetTop(_typing, spot.Y - 1);
    }

    /// <summary>Finishes typing: the text becomes a mark (or changes one), or is dropped.</summary>
    internal void EndText(bool keep)
    {
        if (_typing is not { } box) return;
        _typing = null;
        _canvas.Controls.Children.Remove(box);
        var text = box.Text.Trim();
        var existing = _typingFor;
        _typingFor = null;
        if (existing is { } i)
        {
            if (!keep || text == State[i].Text)
            {
                Show(State);
            }
            else if (text.Length == 0)
            {
                _selected = null;
                Commit(State.RemoveAt(i));
            }
            else
            {
                Commit(State.SetItem(i, State[i] with { Text = text }));
            }
        }
        else if (keep && text.Length > 0)
        {
            _selected = State.Count;
            Commit(State.Add(New(MarkKind.Text, [_typingAt]) with { Text = text }));
        }
        _canvas.Focus();
        RaiseUpdated();
    }

    public override bool OnKey(KeyEventArgs e)
    {
        if (!Started || _typing is not null || Keyboard.FocusedElement is TextBox) return false;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && Tools.FirstOrDefault(t => t.Key == e.Key) is { Name: not null } shortcut)
        {
            Use(shortcut.Tool);
            return true;
        }
        var step = modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Delete or Key.Back when _selected is not null:
                DeleteSelected();
                return true;
            case Key.Escape when _selected is not null:
                _selected = null;
                DrawSelection();
                RaiseUpdated();
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down when _selected is { } s:
                var by = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), _ => new Vector(0, step) };
                if (e.IsRepeat) Amend(State.SetItem(s, State[s].Moved(by)));
                else Commit(State.SetItem(s, State[s].Moved(by)));
                return true;
            default:
                return false;
        }
    }

    internal void DeleteSelected()
    {
        if (_selected is not { } s) return;
        _selected = null;
        Commit(State.RemoveAt(s));
    }
}

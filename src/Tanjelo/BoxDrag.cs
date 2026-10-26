using System.Windows;

namespace Tanjelo;

/// <summary>
/// What a drag does to a box on a picture, such as a crop: inside the box it moves it, on an edge or corner it
/// resizes it, and outside it draws a new one. Everything is in the picture's own pixels, and the box always stays
/// inside the picture. With a shape to keep, only the corners resize, and new boxes keep the shape too.
/// </summary>
sealed class BoxDrag
{
    [Flags]
    public enum Edges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    public enum Kind { Move, Resize, Draw }

    /// <summary>The smallest box a drag can leave, in pixels.</summary>
    const double Smallest = 2;

    readonly Point _start;
    readonly Rect _box;
    readonly Size _bounds;
    readonly double? _ratio;

    public Kind What { get; }
    public Edges Pulled { get; }

    BoxDrag(Kind what, Edges pulled, Point start, Rect box, Size bounds, double? ratio)
    {
        (What, Pulled, _start, _box, _bounds, _ratio) = (what, pulled, start, box, bounds, ratio);
    }

    /// <param name="tolerance">How far from an edge, in pixels, still counts as on it.</param>
    public static BoxDrag Start(Point at, Rect box, Size bounds, double? ratio, double tolerance)
    {
        var edges = Hit(at, box, tolerance, cornersOnly: ratio is not null);
        var what = edges != Edges.None ? Kind.Resize : box.Contains(at) ? Kind.Move : Kind.Draw;
        return new BoxDrag(what, edges, at, box, bounds, ratio);
    }

    /// <summary>The edges a point is on, for choosing what a drag does and which cursor to show.</summary>
    public static Edges Hit(Point at, Rect box, double tolerance, bool cornersOnly)
    {
        var near = new Rect(box.X - tolerance, box.Y - tolerance, box.Width + 2 * tolerance, box.Height + 2 * tolerance);
        if (!near.Contains(at)) return Edges.None;
        var edges = Edges.None;
        if (Math.Abs(at.X - box.Left) <= tolerance) edges |= Edges.Left;
        else if (Math.Abs(at.X - box.Right) <= tolerance) edges |= Edges.Right;
        if (Math.Abs(at.Y - box.Top) <= tolerance) edges |= Edges.Top;
        else if (Math.Abs(at.Y - box.Bottom) <= tolerance) edges |= Edges.Bottom;
        var corner = (edges & (Edges.Left | Edges.Right)) != 0 && (edges & (Edges.Top | Edges.Bottom)) != 0;
        return cornersOnly && !corner ? Edges.None : edges;
    }

    /// <summary>The box with the pointer now at <paramref name="at"/>.</summary>
    public Rect To(Point at)
    {
        at = new Point(Math.Clamp(at.X, 0, _bounds.Width), Math.Clamp(at.Y, 0, _bounds.Height));
        switch (What)
        {
            case Kind.Move:
                var x = Math.Clamp(_box.X + at.X - _start.X, 0, _bounds.Width - _box.Width);
                var y = Math.Clamp(_box.Y + at.Y - _start.Y, 0, _bounds.Height - _box.Height);
                return new Rect(x, y, _box.Width, _box.Height);

            case Kind.Draw:
                return _ratio is { } shape ? Fit(_start, at, shape) : Span(_start, at);

            default:
                if (_ratio is { } ratio)
                {
                    // A corner moves; the opposite one stays where it was.
                    var anchor = new Point(Pulled.HasFlag(Edges.Left) ? _box.Right : _box.Left, Pulled.HasFlag(Edges.Top) ? _box.Bottom : _box.Top);
                    var corner = new Point(Pulled.HasFlag(Edges.Left) ? _box.Left : _box.Right, Pulled.HasFlag(Edges.Top) ? _box.Top : _box.Bottom);
                    return Fit(anchor, new Point(corner.X + at.X - _start.X, corner.Y + at.Y - _start.Y), ratio);
                }
                double left = _box.Left, top = _box.Top, right = _box.Right, bottom = _box.Bottom;
                var (dx, dy) = (at.X - _start.X, at.Y - _start.Y);
                if (Pulled.HasFlag(Edges.Left)) left = Math.Clamp(left + dx, 0, right - Smallest);
                if (Pulled.HasFlag(Edges.Right)) right = Math.Clamp(right + dx, left + Smallest, _bounds.Width);
                if (Pulled.HasFlag(Edges.Top)) top = Math.Clamp(top + dy, 0, bottom - Smallest);
                if (Pulled.HasFlag(Edges.Bottom)) bottom = Math.Clamp(bottom + dy, top + Smallest, _bounds.Height);
                return new Rect(left, top, right - left, bottom - top);
        }
    }

    /// <summary>The box between two points, at least the smallest size.</summary>
    Rect Span(Point a, Point b)
    {
        var rect = new Rect(a, b);
        rect.Width = Math.Max(rect.Width, Math.Min(Smallest, _bounds.Width - rect.X));
        rect.Height = Math.Max(rect.Height, Math.Min(Smallest, _bounds.Height - rect.Y));
        return rect;
    }

    /// <summary>
    /// A box of the given shape from <paramref name="anchor"/> towards <paramref name="toward"/>, big enough to reach
    /// the pointer's further side but no further than the picture allows.
    /// </summary>
    Rect Fit(Point anchor, Point toward, double ratio)
    {
        var (dx, dy) = (toward.X - anchor.X, toward.Y - anchor.Y);
        var width = Math.Max(Math.Abs(dx), Math.Abs(dy) * ratio);
        var room = (dx < 0 ? anchor.X : _bounds.Width - anchor.X, dy < 0 ? anchor.Y : _bounds.Height - anchor.Y);
        width = Math.Max(Smallest, Math.Min(width, Math.Min(room.Item1, room.Item2 * ratio)));
        var height = width / ratio;
        return new Rect(dx < 0 ? anchor.X - width : anchor.X, dy < 0 ? anchor.Y - height : anchor.Y, width, height);
    }
}

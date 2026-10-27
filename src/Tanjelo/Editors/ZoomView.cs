using System.Windows;

namespace Tanjelo.Editors;

/// <summary>
/// Where a picture sits in an editor's canvas: how many canvas units each of its pixels takes, and the canvas point
/// its top-left corner is at. Canvas units are WPF's device-independent pixels, so at 100% one pixel of the picture is
/// one pixel of the screen whatever the display scaling. Zooming keeps the picture pixel under the pointer where it
/// is; the picture always stays in view: one smaller than the canvas stays inside it, a larger one covers it.
/// </summary>
sealed class ZoomView
{
    /// <summary>The zoom limits, where 1 is 100%.</summary>
    public const double Least = 0.02, Most = 32;

    double _pixelsPerDip = 1;

    /// <summary>The picture's size in its own pixels.</summary>
    public Size Picture { get; private set; } = new(1, 1);

    /// <summary>The canvas's size in its own units.</summary>
    public Size Viewport { get; private set; } = new(1, 1);

    /// <summary>Canvas units per picture pixel.</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>Where the picture's top-left corner is on the canvas.</summary>
    public Point Offset { get; private set; }

    /// <summary>Whether the picture follows the canvas's size, fitting it. Zooming or panning stops that until <see cref="Fit"/>.</summary>
    public bool Fitted { get; private set; } = true;

    /// <summary>The zoom as people read it: 1 is 100%, a picture pixel per screen pixel.</summary>
    public double Zoom => Scale * _pixelsPerDip;

    /// <summary>The zoom at which the whole picture fits, no larger than 100% so small pictures aren't blown up.</summary>
    public double FitZoom => Math.Min(1, Math.Min(Viewport.Width / Picture.Width, Viewport.Height / Picture.Height) * _pixelsPerDip);

    public void Show(Size picture, Size viewport, double pixelsPerDip)
    {
        (Picture, Viewport, _pixelsPerDip) = (Positive(picture), Positive(viewport), pixelsPerDip > 0 ? pixelsPerDip : 1);
        Fit();
    }

    /// <summary>The canvas changed size: a fitted picture fits again, a zoomed one keeps its zoom and stays in view.</summary>
    public void Resize(Size viewport, double pixelsPerDip)
    {
        (Viewport, _pixelsPerDip) = (Positive(viewport), pixelsPerDip > 0 ? pixelsPerDip : 1);
        if (Fitted) Fit();
        else Keep();
    }

    public void Fit()
    {
        Scale = FitZoom / _pixelsPerDip;
        Offset = new Point((Viewport.Width - Picture.Width * Scale) / 2, (Viewport.Height - Picture.Height * Scale) / 2);
        Fitted = true;
    }

    /// <summary>Zooms by <paramref name="factor"/>, keeping the picture pixel at canvas point <paramref name="at"/> under it.</summary>
    public void ZoomAt(Point at, double factor) => ZoomTo(Zoom * factor, at);

    /// <summary>Zooms to <paramref name="zoom"/> (1 is 100%), keeping the picture pixel at canvas point <paramref name="at"/> under it.</summary>
    public void ZoomTo(double zoom, Point at)
    {
        var pixel = ToPicture(at);
        Scale = Math.Clamp(zoom, Least, Most) / _pixelsPerDip;
        Offset = new Point(at.X - pixel.X * Scale, at.Y - pixel.Y * Scale);
        Fitted = false;
        Keep();
    }

    /// <summary>Zooms around the middle of the canvas, as the keyboard and the zoom buttons do.</summary>
    public void ZoomTo(double zoom) => ZoomTo(zoom, new Point(Viewport.Width / 2, Viewport.Height / 2));

    public void PanBy(Vector by)
    {
        Offset += by;
        Fitted = false;
        Keep();
    }

    public Point ToPicture(Point canvas) => new((canvas.X - Offset.X) / Scale, (canvas.Y - Offset.Y) / Scale);

    public Point ToCanvas(Point picture) => new(Offset.X + picture.X * Scale, Offset.Y + picture.Y * Scale);

    public Rect ToCanvas(Rect picture) => new(ToCanvas(picture.TopLeft), ToCanvas(picture.BottomRight));

    /// <summary>Keeps the picture in view: inside the canvas along a side where it's smaller, covering it where it's larger.</summary>
    void Keep()
    {
        static double Within(double offset, double size, double room) =>
            size <= room ? Math.Clamp(offset, 0, room - size) : Math.Clamp(offset, room - size, 0);
        Offset = new Point(Within(Offset.X, Picture.Width * Scale, Viewport.Width), Within(Offset.Y, Picture.Height * Scale, Viewport.Height));
    }

    static Size Positive(Size size) => new(Math.Max(1, size.Width), Math.Max(1, size.Height));
}

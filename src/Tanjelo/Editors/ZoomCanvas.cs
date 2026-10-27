using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tanjelo.Editors;

/// <summary>What an editor does with the pointer on its canvas. Points are in the picture's own pixels.</summary>
interface ICanvasTool
{
    /// <param name="clicks">2 for a double-click.</param>
    void Down(Point at, ModifierKeys modifiers, int clicks);

    void Move(Point at);

    void Up(Point at);

    /// <summary>The pointer to show over this spot while no button is down.</summary>
    Cursor? CursorAt(Point at);
}

/// <summary>
/// Shows a picture to fit, zooms with the wheel around the pointer, and pans with Space+drag or the middle button.
/// Editors draw on <see cref="World"/> in the picture's own pixels, so marks zoom with it, and on <see cref="Overlay"/>
/// in canvas units, for handles that stay the same size at any zoom. The left button goes to <see cref="Tool"/>.
/// </summary>
sealed class ZoomCanvas : Grid
{
    readonly Canvas _content = new();
    readonly MatrixTransform _transform = new();
    Point? _panFrom;
    bool _toolDown;
    bool _space;

    public ZoomView View { get; } = new();

    public Image Picture { get; } = new() { Stretch = Stretch.Fill };

    /// <summary>Drawn with the picture and in its pixels, such as marks.</summary>
    public Canvas World { get; } = new();

    /// <summary>Drawn over everything in canvas units, such as handles. It doesn't take the pointer.</summary>
    public Canvas Overlay { get; } = new() { IsHitTestVisible = false };

    public ICanvasTool? Tool { get; set; }

    /// <summary>The zoom or the picture's place changed, so whatever is drawn on <see cref="Overlay"/> needs moving.</summary>
    public event Action? ViewChanged;

    public ZoomCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        Background = Brushes.Transparent;
        _content.RenderTransform = _transform;
        _content.Children.Add(Picture);
        _content.Children.Add(World);
        var holder = new Canvas();
        holder.Children.Add(_content);
        Children.Add(holder);
        Children.Add(Overlay);
        SizeChanged += (_, _) =>
        {
            if (!HasPicture) return;
            View.Resize(RenderSize, PixelsPerDip);
            Apply();
        };
    }

    public bool HasPicture => Picture.Source is not null;

    double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>Shows a new picture, fitted. <paramref name="pixels"/> is its full size, which the source may be a smaller copy of.</summary>
    public void Show(ImageSource source, Size pixels)
    {
        Picture.Source = source;
        foreach (var layer in new FrameworkElement[] { Picture, World, _content })
            (layer.Width, layer.Height) = (pixels.Width, pixels.Height);
        View.Show(pixels, RenderSize, PixelsPerDip);
        Apply();
    }

    /// <summary>Swaps in another copy of the same picture, such as a new preview, without moving the view.</summary>
    public void Replace(ImageSource source) => Picture.Source = source;

    /// <summary>Whether Space is held, which turns a drag into panning.</summary>
    public bool SpaceHeld
    {
        get => _space;
        set
        {
            _space = value;
            if (_panFrom is null) Cursor = value ? Cursors.SizeAll : null;
        }
    }

    public void Fit()
    {
        View.Fit();
        Apply();
    }

    public void ZoomTo(double zoom)
    {
        View.ZoomTo(zoom);
        Apply();
    }

    /// <summary>One step in or out from the keyboard or the zoom buttons, stopping at 100% and at fitting on the way.</summary>
    public void Step(int direction)
    {
        var next = View.Zoom * (direction > 0 ? 1.25 : 0.8);
        foreach (var stop in new[] { 1, View.FitZoom })
            if (Math.Sign(stop - View.Zoom) == Math.Sign(direction) && Math.Sign(next - stop) == Math.Sign(direction))
                next = stop;
        if (Math.Abs(next - View.FitZoom) < 1e-9) Fit();
        else ZoomTo(next);
    }

    /// <summary>The pointer went down at a point on the canvas, in canvas units.</summary>
    internal void PointerDown(Point at, MouseButton button, ModifierKeys modifiers, int clicks = 1)
    {
        if (!HasPicture) return;
        if (button == MouseButton.Middle || (button == MouseButton.Left && _space))
        {
            _panFrom = at;
            Cursor = Cursors.SizeAll;
        }
        else if (button == MouseButton.Left && Tool is not null)
        {
            _toolDown = true;
            Tool.Down(View.ToPicture(at), modifiers, clicks);
        }
    }

    internal void PointerMove(Point at)
    {
        if (_panFrom is { } from)
        {
            View.PanBy(at - from);
            _panFrom = at;
            Apply();
        }
        else if (_toolDown)
        {
            Tool?.Move(View.ToPicture(at));
        }
        else if (!_space && HasPicture)
        {
            Cursor = Tool?.CursorAt(View.ToPicture(at));
        }
    }

    internal void PointerUp(Point at)
    {
        if (_panFrom is not null)
        {
            _panFrom = null;
            Cursor = _space ? Cursors.SizeAll : null;
        }
        else if (_toolDown)
        {
            _toolDown = false;
            Tool?.Up(View.ToPicture(at));
        }
    }

    internal void Wheel(Point at, int delta)
    {
        if (!HasPicture) return;
        View.ZoomAt(at, Math.Pow(1.2, delta / 120.0));
        Apply();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (!HasPicture || e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        PointerDown(e.GetPosition(this), e.ChangedButton, Keyboard.Modifiers, e.ClickCount);
        if (_panFrom is not null || _toolDown) CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) => PointerMove(e.GetPosition(this));

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_panFrom is null && !_toolDown) return;
        PointerUp(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        // Capture can go without a button coming up, such as when another window takes the focus.
        if (_panFrom is not null || _toolDown) PointerUp(e.GetPosition(this));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Wheel(e.GetPosition(this), e.Delta);
        e.Handled = true;
    }

    /// <summary>Puts the picture where the view says, smoothing it when shrunk and showing crisp pixels when zoomed in.</summary>
    void Apply()
    {
        _transform.Matrix = new Matrix(View.Scale, 0, 0, View.Scale, View.Offset.X, View.Offset.Y);
        RenderOptions.SetBitmapScalingMode(Picture, View.Zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        ViewChanged?.Invoke();
    }
}

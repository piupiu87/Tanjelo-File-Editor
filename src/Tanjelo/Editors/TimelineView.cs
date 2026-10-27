using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tanjelo.Core;
using Tanjelo.Core.Converters;

namespace Tanjelo.Editors;

/// <summary>The bands of a timeline, from the top: times, pictures, sound, the editor's own rows, and the overview.</summary>
enum TimelineLane { None, Ruler, Pictures, Sound, Rows, Overview }

/// <summary>A place on the timeline: its time, its band and row, and how many seconds a few pixels make there.</summary>
/// <param name="Grip">How far off in seconds the pointer can be and still be on something, such as a handle.</param>
readonly record struct TimelinePoint(double Time, TimelineLane Lane, int Row, double Grip);

/// <summary>What an editor does with the pointer on its timeline, and what it draws there.</summary>
interface ITimelineTool
{
    /// <summary>The button went down; true when the editor took it, otherwise the playhead goes there.</summary>
    bool Down(TimelinePoint at, ModifierKeys modifiers);

    void Move(TimelinePoint at, ModifierKeys modifiers);
    void Up(TimelinePoint at);
    Cursor? CursorAt(TimelinePoint at);

    /// <summary>Draws the editor's marks, such as handles or ranges, over the pictures and sound and under the playhead.</summary>
    void Draw(DrawingContext dc, TimelineView view);
}

/// <summary>
/// A timeline for audio and video: times along the top, thumbnails, the shape of the sound, rows for the editor's own
/// marks and the playhead. The wheel zooms around the pointer and Shift+wheel scrolls; clicking or dragging on the
/// times or the pictures moves the playhead. Everything is drawn in one pass, so long files stay quick.
/// </summary>
sealed class TimelineView : FrameworkElement
{
    public const double RulerHeight = 24, PicturesHeight = 46, RowHeight = 20, OverviewHeight = 10, Gap = 4, Inset = 12;

    readonly List<(double Time, BitmapSource Picture)> _thumbnails = [];
    double _duration = 1, _start, _perPixel = 0.01, _playhead;
    float _loudest = 1;
    bool _fitted = true;

    // A drag in progress: who has it, and from where.
    enum Drag { None, Scrub, Tool, Overview }
    Drag _drag;
    double _overviewFrom, _startFrom;

    public TimelineView()
    {
        Focusable = false;
        ClipToBounds = true;
        foreach (var (property, key) in new[] { (LaneProperty, "Field"), (EdgeProperty, "FieldBorder"), (TextProperty, "TextSecondary"),
            (FaintProperty, "TextTertiary"), (AccentProperty, "Accent"), (SoftProperty, "AccentSoft") })
            SetResourceReference(property, key);
    }

    // Colours follow the theme: a change of theme redraws the timeline.
    public static readonly DependencyProperty LaneProperty = Colour(nameof(Lane));
    public static readonly DependencyProperty EdgeProperty = Colour(nameof(Edge));
    public static readonly DependencyProperty TextProperty = Colour(nameof(Text));
    public static readonly DependencyProperty FaintProperty = Colour(nameof(Faint));
    public static readonly DependencyProperty AccentProperty = Colour(nameof(Accent));
    public static readonly DependencyProperty SoftProperty = Colour(nameof(Soft));

    static DependencyProperty Colour(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(TimelineView), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Lane => (Brush)GetValue(LaneProperty);
    public Brush Edge => (Brush)GetValue(EdgeProperty);
    public Brush Text => (Brush)GetValue(TextProperty);
    public Brush Faint => (Brush)GetValue(FaintProperty);
    public Brush Accent => (Brush)GetValue(AccentProperty);
    public Brush Soft => (Brush)GetValue(SoftProperty);

    public ITimelineTool? Tool { get; set; }

    /// <summary>The file's frames, for snapping and keyframe marks; null for sound.</summary>
    public FrameIndex? Frames
    {
        get;
        set
        {
            field = value;
            InvalidateVisual();
        }
    }

    public Waveform? Sound
    {
        get;
        set
        {
            field = value;
            // Quiet recordings are drawn as large as loud ones, so their shape can be seen.
            _loudest = Math.Max(0.05f, value is { Peaks.Count: > 0 } sound ? sound.Peaks.Max() : 1);
            InvalidateVisual();
        }
    }

    /// <summary>Whether there's a band of thumbnails; off for sound.</summary>
    public bool ShowPictures { get; set; } = true;

    /// <summary>For sound on its own: the shape of the sound takes all the room the pictures would have.</summary>
    public bool SoundFills { get; set; }

    /// <summary>How tall the sound's band is when it doesn't fill.</summary>
    public double SoundHeight { get; set; } = 54;

    /// <summary>The editor's own rows under the sound, such as one bar per redaction.</summary>
    public int Rows
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public double Duration
    {
        get => _duration;
        set
        {
            _duration = Math.Max(0.001, value);
            if (_fitted) Fit();
            InvalidateVisual();
        }
    }

    /// <summary>The time the playhead is at.</summary>
    public double Playhead
    {
        get => _playhead;
        set
        {
            _playhead = Math.Clamp(value, 0, _duration);
            InvalidateVisual();
        }
    }

    /// <summary>The time at the left edge of the lanes, and seconds per pixel.</summary>
    public double ViewStart => _start;
    public double SecondsPerPixel => _perPixel;
    public double ViewEnd => _start + Width0 * _perPixel;
    public bool Fitted => _fitted;

    /// <summary>The playhead was moved with the pointer, to this time.</summary>
    public event Action<double>? Scrubbed;

    /// <summary>The zoom or the part in view changed.</summary>
    public event Action? ViewChanged;

    public void AddThumbnail(double time, BitmapSource picture)
    {
        var at = _thumbnails.BinarySearch((time, picture), Comparer<(double Time, BitmapSource)>.Create((a, b) => a.Time.CompareTo(b.Time)));
        _thumbnails.Insert(at >= 0 ? at : ~at, (time, picture));
        InvalidateVisual();
    }

    public int ThumbnailCount => _thumbnails.Count;

    // ---- Geometry

    double Width0 => Math.Max(1, ActualWidth - 2 * Inset);

    public double XAt(double time) => Inset + (time - _start) / _perPixel;

    public double TimeAt(double x) => _start + (x - Inset) * _perPixel;

    /// <summary>Top and height of each band, as laid out now.</summary>
    public (double Top, double Height) Band(TimelineLane lane, int row = 0)
    {
        if (lane == TimelineLane.Ruler) return (0, RulerHeight);
        var y = RulerHeight + Gap;
        if (lane == TimelineLane.Pictures) return (y, ShowPictures ? PicturesHeight : 0);
        if (ShowPictures) y += PicturesHeight + Gap;
        var below = (Rows > 0 ? Rows * RowHeight + Gap : 0) + OverviewHeight;
        var sound = SoundFills ? Math.Max(SoundHeight, ActualHeight - y - below - Gap) : SoundHeight;
        if (lane == TimelineLane.Sound) return (y, sound);
        y += sound + Gap;
        if (lane == TimelineLane.Rows) return (y + row * RowHeight, RowHeight);
        if (Rows > 0) y += Rows * RowHeight + Gap;
        return (y, OverviewHeight);
    }

    protected override Size MeasureOverride(Size available)
    {
        var height = RulerHeight + Gap + (ShowPictures ? PicturesHeight + Gap : 0) + SoundHeight + Gap + (Rows > 0 ? Rows * RowHeight + Gap : 0) + OverviewHeight;
        return new Size(double.IsInfinity(available.Width) ? 400 : available.Width, SoundFills && !double.IsInfinity(available.Height) ? Math.Max(height, available.Height) : height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (_fitted) Fit();
        else Keep();
    }

    // ---- Zoom and scroll

    /// <summary>The longest stretch a pixel can stand for when zoomed all the way in: a few pixels a frame, or a millisecond.</summary>
    double Finest => Frames is { Count: > 1 } frames ? frames.End / frames.Count / 24 : 0.0005;

    public void Fit()
    {
        _perPixel = _duration / Width0;
        _start = 0;
        _fitted = true;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Zooms by a factor, keeping the time at <paramref name="x"/> under it.</summary>
    public void ZoomAt(double x, double factor)
    {
        var time = TimeAt(x);
        var fit = _duration / Width0;
        _perPixel = Math.Clamp(_perPixel / factor, Math.Min(Finest, fit), fit);
        _fitted = _perPixel >= fit - 1e-12;
        _start = time - (x - Inset) * _perPixel;
        Keep();
    }

    public void ScrollBy(double pixels)
    {
        _start += pixels * _perPixel;
        _fitted = false;
        Keep();
    }

    /// <summary>Scrolls just enough to show a time, a little in from the edge, as the playhead moves on.</summary>
    public void Reveal(double time)
    {
        if (time >= _start && time <= ViewEnd) return;
        _start = time < _start ? time - Width0 * _perPixel * 0.1 : time - Width0 * _perPixel * 0.1;
        Keep();
    }

    void Keep()
    {
        _start = Math.Clamp(_start, 0, Math.Max(0, _duration - Width0 * _perPixel));
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    // ---- The pointer

    TimelinePoint PointAt(Point at)
    {
        var lane = TimelineLane.None;
        var row = 0;
        foreach (var candidate in new[] { TimelineLane.Ruler, TimelineLane.Pictures, TimelineLane.Sound, TimelineLane.Overview })
        {
            var (top, height) = Band(candidate);
            if (height > 0 && at.Y >= top && at.Y < top + height + Gap) lane = candidate;
        }
        var (rowsTop, _) = Band(TimelineLane.Rows);
        if (Rows > 0 && at.Y >= rowsTop && at.Y < rowsTop + Rows * RowHeight)
            (lane, row) = (TimelineLane.Rows, (int)((at.Y - rowsTop) / RowHeight));
        return new TimelinePoint(Math.Clamp(TimeAt(at.X), 0, _duration), lane, row, 6 * _perPixel);
    }

    internal void PointerDown(Point at, ModifierKeys modifiers)
    {
        var point = PointAt(at);
        if (point.Lane == TimelineLane.Overview)
        {
            (_drag, _overviewFrom, _startFrom) = (Drag.Overview, at.X, _start);
            if (at.X < OverviewX(_start) || at.X > OverviewX(ViewEnd))
            {
                // Clicked beside the window in view: centre it there.
                _start = (at.X - Inset) / Width0 * _duration - Width0 * _perPixel / 2;
                _startFrom = _start;
                Keep();
            }
            return;
        }
        if (point.Lane is not (TimelineLane.Ruler or TimelineLane.Pictures) && Tool?.Down(point, modifiers) == true)
        {
            _drag = Drag.Tool;
            return;
        }
        _drag = Drag.Scrub;
        Scrubbed?.Invoke(point.Time);
    }

    internal void PointerMove(Point at, ModifierKeys? modifiers = null)
    {
        var point = PointAt(at);
        switch (_drag)
        {
            case Drag.Scrub:
                Scrubbed?.Invoke(point.Time);
                break;
            case Drag.Tool:
                Tool?.Move(point, modifiers ?? Keyboard.Modifiers);
                break;
            case Drag.Overview:
                _start = _startFrom + (at.X - _overviewFrom) / Width0 * _duration;
                _fitted = false;
                Keep();
                break;
            default:
                Cursor = point.Lane is TimelineLane.Ruler or TimelineLane.Pictures ? Cursors.IBeam
                    : point.Lane == TimelineLane.Overview ? Cursors.SizeWE
                    : Tool?.CursorAt(point) ?? Cursors.IBeam;
                break;
        }
    }

    internal void PointerUp(Point at)
    {
        var drag = _drag;
        _drag = Drag.None;
        if (drag == Drag.Tool) Tool?.Up(PointAt(at));
    }

    internal void Wheel(Point at, int delta, ModifierKeys modifiers)
    {
        if (modifiers.HasFlag(ModifierKeys.Shift)) ScrollBy(-delta / 120.0 * Width0 / 8);
        else ZoomAt(at.X, Math.Pow(1.25, delta / 120.0));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        PointerDown(e.GetPosition(this), Keyboard.Modifiers);
        if (_drag != Drag.None) CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) => PointerMove(e.GetPosition(this));

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        PointerUp(e.GetPosition(this));
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_drag != Drag.None) PointerUp(e.GetPosition(this));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Wheel(e.GetPosition(this), e.Delta, Keyboard.Modifiers);
        e.Handled = true;
    }

    // ---- Drawing

    double OverviewX(double time) => Inset + time / _duration * Width0;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 2 * Inset) return;
        var edge = new Pen(Edge, 1);
        edge.Freeze();
        var left = Inset;
        var right = width - Inset;

        // The bands
        foreach (var lane in new[] { TimelineLane.Pictures, TimelineLane.Sound })
        {
            var (top, height) = Band(lane);
            if (height > 0) dc.DrawRoundedRectangle(Lane, edge, new Rect(left - 0.5, top - 0.5, right - left + 1, height + 1), 4, 4);
        }
        for (var row = 0; row < Rows; row++)
        {
            var (top, height) = Band(TimelineLane.Rows, row);
            dc.DrawRectangle(row % 2 == 0 ? Lane : Brushes.Transparent, null, new Rect(left, top, right - left, height));
        }

        dc.PushClip(new RectangleGeometry(new Rect(left, 0, right - left, ActualHeight)));
        DrawRuler(dc, left, right);
        DrawPictures(dc, left, right);
        DrawSound(dc, left, right);
        DrawKeyframes(dc, left, right);
        Tool?.Draw(dc, this);
        dc.Pop();
        DrawOverview(dc);
        DrawPlayhead(dc);
    }

    void DrawRuler(DrawingContext dc, double left, double right)
    {
        var (top, height) = Band(TimelineLane.Ruler);
        var (major, minor) = Steps(_perPixel * 90);
        var faint = new Pen(Faint, 1);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface((FontFamily)(TryFindResource("UiFont") ?? new FontFamily("Segoe UI")), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        for (var k = Math.Floor(_start / minor); k * minor <= ViewEnd + minor; k++)
        {
            var t = k * minor;
            var x = Math.Round(XAt(t)) + 0.5;
            if (x < left - 1 || x > right + 1) continue;
            var isMajor = Math.Abs(t / major - Math.Round(t / major)) < 1e-6;
            dc.DrawLine(faint, new Point(x, top + height - (isMajor ? 8 : 4)), new Point(x, top + height));
            if (!isMajor) continue;
            var label = new FormattedText(Label(t, major), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, Text, dpi);
            dc.DrawText(label, new Point(x + 3, top + 2));
        }
    }

    /// <summary>A step between labelled ticks of at least <paramref name="least"/> seconds, from a list of round ones, and the step between small ticks.</summary>
    static (double Major, double Minor) Steps(double least)
    {
        double[] steps = [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
        var major = steps.FirstOrDefault(s => s >= least, 7200);
        // Small ticks at round times too: every 5 seconds for 15 and 30, every 15 for a minute or an hour.
        return (major, major / (major is 15 or 900 ? 3 : major is 30 or 1800 ? 6 : major is 60 or 3600 ? 4 : 5));
    }

    static string Label(double seconds, double step)
    {
        var ms = (long)Math.Round(seconds * 1000);
        var text = OptionValues.FormatTime(ms / 1000);
        if (step >= 1) return text;
        // Fractions to as many places as the step needs, so neighbouring labels differ.
        var places = step >= 0.1 ? 1 : step >= 0.01 ? 2 : 3;
        return text + "." + (ms % 1000).ToString("000", CultureInfo.InvariantCulture)[..places];
    }

    void DrawPictures(DrawingContext dc, double left, double right)
    {
        var (top, height) = Band(TimelineLane.Pictures);
        if (height <= 0 || _thumbnails.Count == 0) return;
        var first = _thumbnails[0].Picture;
        var tile = Math.Max(16, (height - 4) * first.PixelWidth / Math.Max(1, first.PixelHeight));
        dc.PushClip(new RectangleGeometry(new Rect(left, top, right - left, height), 4, 4));
        // Tiles sit at fixed places along the timeline, so they don't swim as it scrolls.
        for (var x = left + ((XAt(0) - left) % tile + tile) % tile - tile; x < right; x += tile)
        {
            var picture = Nearest(TimeAt(x + tile / 2));
            if (picture is null || TimeAt(x) > _duration) continue;
            dc.DrawImage(picture, new Rect(x, top + 2, tile - 1, height - 4));
        }
        dc.Pop();
    }

    BitmapSource? Nearest(double time)
    {
        if (_thumbnails.Count == 0) return null;
        int lo = 0, hi = _thumbnails.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_thumbnails[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        if (lo > 0 && time - _thumbnails[lo - 1].Time < _thumbnails[lo].Time - time) lo--;
        return _thumbnails[lo].Picture;
    }

    void DrawSound(DrawingContext dc, double left, double right)
    {
        var (top, height) = Band(TimelineLane.Sound);
        if (Sound is not { } sound || height <= 0) return;
        var middle = top + height / 2;
        var half = height / 2 - 3;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var from = Math.Max(left, XAt(0));
            var to = Math.Min(right, XAt(Math.Min(_duration, sound.Duration)));
            if (to <= from) return;
            var tops = new List<Point>();
            var bottoms = new List<Point>();
            for (var x = from; x <= to; x++)
            {
                // At least a hairline, so silence still shows where the sound is.
                var peak = Math.Max(0.5 / half, Math.Min(1, sound.Peak(TimeAt(x), TimeAt(x + 1)) / _loudest));
                tops.Add(new Point(x, middle - peak * half));
                bottoms.Add(new Point(x, middle + peak * half));
            }
            bottoms.Reverse();
            g.BeginFigure(tops[0], isFilled: true, isClosed: true);
            g.PolyLineTo(tops, isStroked: false, isSmoothJoin: false);
            g.PolyLineTo(bottoms, isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();
        dc.DrawGeometry(Text, null, geometry);
    }

    void DrawKeyframes(DrawingContext dc, double left, double right)
    {
        if (Frames is not { Estimated: false } frames || frames.Keyframes.Count == 0) return;
        var (top, height) = ShowPictures ? Band(TimelineLane.Pictures) : Band(TimelineLane.Ruler);
        var keys = frames.Keyframes;
        // Marks only while they're far enough apart to tell from each other.
        if (frames.End / keys.Count / _perPixel < 3) return;
        var y = top + height;
        for (var i = Math.Max(0, LowerBound(keys, frames.At(_start)) - 1); i < keys.Count; i++)
        {
            var x = Math.Round(XAt(frames.Start(keys[i]))) + 0.5;
            if (x > right) break;
            if (x < left) continue;
            dc.DrawGeometry(Accent, null, new PathGeometry([new PathFigure(new Point(x - 3.5, y), [new LineSegment(new Point(x + 3.5, y), false), new LineSegment(new Point(x, y - 5), false)], true)]));
        }
    }

    static int LowerBound(IReadOnlyList<int> list, int value)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    void DrawOverview(DrawingContext dc)
    {
        var (top, height) = Band(TimelineLane.Overview);
        if (_fitted) return;
        var track = new Rect(Inset, top + 3, Width0, height - 6);
        dc.DrawRoundedRectangle(Lane, null, track, 2, 2);
        var x0 = OverviewX(_start);
        var x1 = Math.Max(x0 + 6, OverviewX(ViewEnd));
        dc.DrawRoundedRectangle(Faint, null, new Rect(x0, top + 2, x1 - x0, height - 4), 3, 3);
        var playhead = OverviewX(_playhead);
        dc.DrawRectangle(Accent, null, new Rect(playhead - 1, top + 1, 2, height - 2));
    }

    void DrawPlayhead(DrawingContext dc)
    {
        var x = Math.Round(XAt(_playhead)) + 0.5;
        if (x < Inset - 1 || x > ActualWidth - Inset + 1) return;
        var (rulerTop, rulerHeight) = Band(TimelineLane.Ruler);
        var (overviewTop, _) = Band(TimelineLane.Overview);
        var bottom = overviewTop - Gap;
        var pen = new Pen(Accent, 2);
        dc.DrawLine(pen, new Point(x, rulerTop + rulerHeight - 9), new Point(x, bottom));
        var head = new PathGeometry([new PathFigure(new Point(x - 6, rulerTop + rulerHeight - 12),
            [new LineSegment(new Point(x + 6, rulerTop + rulerHeight - 12), false), new LineSegment(new Point(x, rulerTop + rulerHeight - 4), false)], true)]);
        dc.DrawGeometry(Accent, null, head);
    }
}

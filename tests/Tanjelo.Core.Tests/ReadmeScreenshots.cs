using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Tanjelo.Editors;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Tool = Tanjelo.Editors.AnnotateEditor.Tool;

namespace Tanjelo.Core.Tests;

/// <summary>
/// The README's screenshots, made off-screen from a drawn landscape and a clip made from it, so they show no one's files
/// or paths. They only run when TANJELO_SCREENSHOTS names a folder to save them in, such as the full path of
/// docs\images.
/// </summary>
[Collection("Windows player")]
public class ReadmeScreenshots
{
    static readonly string? Folder = Environment.GetEnvironmentVariable("TANJELO_SCREENSHOTS");

    const string Off = "TANJELO_SCREENSHOTS isn't set";

    [Fact]
    public async Task Menu_options_dialog_and_annotate_editor()
    {
        Assert.SkipUnless(Folder is not null, Off);
        using var scratch = new Scratch();
        var photo = scratch.PathOf("lakeside.jpg");
        await Ui.Run(async () =>
        {
            Landscape(photo);
            var engine = Env.Engine();

            var menu = new FormatMenuWindow([photo], engine.GetMenu([photo]), (ModifierKeys.Control | ModifierKeys.Shift, Key.Space));
            var host = Fit(menu, 330);
            await Ui.Settle();
            Assert.True(menu.Press(Key.Tab, ModifierKeys.None));
            Assert.True(menu.Press(Key.Down, ModifierKeys.None));
            host = Refit(host);
            await Ui.Settle();
            Shoot(host, "menu");
            menu.Close();

            var dialog = new OptionsWindow(engine, [photo], Env.Offered(engine, "background", [photo]), new Dictionary<string, string>());
            host = Fit(dialog);
            for (var waited = 0; dialog.PreviewImage.Source is null || dialog.PreviewBusy.Visibility == Visibility.Visible; waited += 100)
            {
                Assert.True(waited < 30_000, "the preview never came");
                await Ui.Settle(100);
            }
            Shoot(host, "add-background");
            dialog.Close();

            var target = Env.Offered(engine, "annotate", [photo]);
            var editor = new AnnotateEditor(photo, target);
            var window = new EditorWindow(photo, target, editor);
            host = Ui.Host(window, 1200, 800);
            await window.LoadAsync();
            await Ui.Settle();
            Ui.Layout(host);
            var canvas = editor.Canvas;
            editor.Use(Tool.Text);
            Click(canvas, new Point(150, 120));
            editor.Typing!.Text = "First light, 6:12";
            editor.EndText(keep: true);
            editor.Use(Tool.Arrow);
            Drag(canvas, new Point(460, 215), new Point(715, 315));
            editor.Use(Tool.Callout);
            Click(canvas, new Point(905, 255));
            editor.Use(Tool.Rectangle);
            Drag(canvas, new Point(105, 560), new Point(265, 735));
            editor.Use(Tool.Callout);
            Click(canvas, new Point(305, 575));
            editor.Use(Tool.Magnifier);
            Click(canvas, new Point(800, 640));
            editor.Use(Tool.Select);
            Click(canvas, new Point(600, 760));
            Ui.Layout(host);
            await Ui.Settle();
            Shoot(host, "annotate");
            window.Close();
        });
    }

    [Fact]
    public async Task Trim_editor_in_the_dark_theme()
    {
        Assert.SkipUnless(Folder is not null, Off);
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var still = scratch.PathOf("lakeside.jpg");
        await Ui.Run(async () =>
        {
            Landscape(still);
            await Task.CompletedTask;
        });
        // A slow zoom into the landscape, with a tune that swells and fades so the waveform has a shape.
        var clip = scratch.Ffmpeg("lakeside.mp4",
        [
            "-loop", "1", "-framerate", "30", "-i", still,
            "-f", "lavfi", "-i", "aevalsrc='0.45*sin(2*PI*220*t)*pow(sin(PI*t*0.9),2)+0.3*sin(2*PI*330*t)*pow(sin(PI*t*1.7+1),2)':s=48000:d=8",
            "-vf", "zoompan=z='1+0.0012*on':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s=960x640:fps=30,format=yuv420p",
            "-t", "8", "-c:v", "libx264", "-g", "30", "-c:a", "aac", "-shortest",
        ]);
        await Ui.Run(async () =>
        {
            var (window, editor, host) = await TrimEditorTests.Open(clip, "trim", (file, target) => new TrimEditor(file, target, Env.Tools));
            Ui.UseTheme("dark", host);
            TrimEditorTests.Drag(editor.View, 0, 1.6, TimelineLane.Pictures);
            TrimEditorTests.Drag(editor.View, editor.Current.End, 6.3);
            await editor.Seek();
            await Ui.Settle(300);
            Shoot(host, "trim");
            window.Close();
        });
    }

    static void Drag(ZoomCanvas canvas, Point from, Point to)
    {
        canvas.PointerDown(canvas.View.ToCanvas(from), MouseButton.Left, ModifierKeys.None);
        for (var i = 1; i <= 5; i++)
            canvas.PointerMove(canvas.View.ToCanvas(new Point(from.X + (to.X - from.X) * i / 5, from.Y + (to.Y - from.Y) * i / 5)));
        canvas.PointerUp(canvas.View.ToCanvas(to));
    }

    static void Click(ZoomCanvas canvas, Point at)
    {
        canvas.PointerDown(canvas.View.ToCanvas(at), MouseButton.Left, ModifierKeys.None);
        canvas.PointerUp(canvas.View.ToCanvas(at));
    }

    /// <summary>A window that sizes itself to its content, hosted at that size.</summary>
    static FrameworkElement Fit(Window window, double width = double.PositiveInfinity) =>
        Refit(Ui.Host(window, double.IsInfinity(width) ? 2000 : width, 2000), width);

    static FrameworkElement Refit(FrameworkElement host, double width = double.NaN)
    {
        var content = (FrameworkElement)((System.Windows.Controls.Border)host).Child;
        content.Measure(new Size(double.IsNaN(width) ? host.Width : width, double.PositiveInfinity));
        if (double.IsInfinity(width)) host.Width = Math.Ceiling(content.DesiredSize.Width);
        host.Height = Math.Ceiling(content.DesiredSize.Height);
        Ui.Layout(host);
        return host;
    }

    /// <summary>
    /// Saves the host at twice its size, for sharp pictures on high-DPI screens, as a window with rounded corners, a
    /// thin edge and a soft shadow on a see-through page.
    /// </summary>
    static void Shoot(FrameworkElement host, string name)
    {
        const double scale = 2, margin = 28, corner = 8;
        Ui.Layout(host);
        var shot = new RenderTargetBitmap((int)(host.ActualWidth * scale), (int)(host.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        shot.Render(host);
        var frame = new Rect(margin, margin, host.ActualWidth, host.ActualHeight);

        var shadow = new DrawingVisual { Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.3, Color = Colors.Black } };
        using (var dc = shadow.RenderOpen())
            dc.DrawRoundedRectangle(Brushes.Black, null, frame, corner, corner);
        var window = new DrawingVisual();
        using (var dc = window.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(frame, corner, corner));
            dc.DrawImage(shot, frame);
            dc.Pop();
            var edge = Theme.IsDark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0);
            frame.Inflate(-0.5, -0.5);
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(edge), 1), frame, corner, corner);
        }

        var page = new RenderTargetBitmap((int)((host.ActualWidth + 2 * margin) * scale), (int)((host.ActualHeight + 2 * margin) * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        page.Render(shadow);
        page.Render(window);
        Directory.CreateDirectory(Folder!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(page));
        using var stream = File.Create(Path.Combine(Folder!, name + ".png"));
        encoder.Save(stream);
    }

    /// <summary>A lake at sunrise, drawn, 1200 by 800, saved as a JPG.</summary>
    static void Landscape(string path)
    {
        const int width = 1200, height = 800, horizon = 520;
        var sun = new Point(800, 335);
        var scene = new DrawingVisual();
        using (var dc = scene.RenderOpen())
        {
            dc.DrawRectangle(Vertical(("#22385F", 0), ("#6C5F92", 0.45), ("#F09A6A", 0.82), ("#FBD39C", 1)), null, new Rect(0, 0, width, horizon));
            dc.DrawEllipse(new RadialGradientBrush(Hex("#90FFE2A0"), Hex("#00FFE2A0")), null, sun, 280, 280);
            dc.DrawEllipse(new RadialGradientBrush(Hex("#FFF9DC"), Hex("#FFC160")), null, sun, 62, 62);
            dc.DrawGeometry(Solid("#9A7896"), null, Ridge(horizon, (0, 395), (150, 320), (270, 372), (430, 282), (575, 355), (690, 318), (860, 405), (1030, 335), (1200, 378)));
            dc.DrawGeometry(Solid("#5B5577"), null, Ridge(horizon, (0, 462), (170, 425), (390, 474), (610, 436), (830, 478), (1010, 442), (1200, 468)));
            dc.DrawRectangle(Vertical(("#F0B080", 0), ("#8A7493", 0.38), ("#283A5E", 1)), null, new Rect(0, horizon, width, height - horizon));
            for (var i = 0; i < 10; i++)
            {
                var half = 66 - i * 5.5;
                dc.DrawRoundedRectangle(Solid("#A0FFE3A8"), null, new Rect(sun.X - half, horizon + 12 + i * 21, half * 2, 4), 2, 2);
            }
            var shore = new StreamGeometry();
            using (var g = shore.Open())
            {
                g.BeginFigure(new Point(0, 640), true, true);
                g.PolyLineTo([new(180, 660), new(330, 705), new(430, 760), new(470, height), new(0, height)], true, true);
            }
            dc.DrawGeometry(Solid("#18233A"), null, shore);
            foreach (var (x, y, size) in new[] { (140.0, 690.0, 1.0), (215, 705, 0.8), (60, 670, 0.9) })
                dc.DrawGeometry(Solid("#101A2C"), null, Pine(x, y, size));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(scene);
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    static Geometry Ridge(double floor, params (double X, double Y)[] tops)
    {
        var ridge = new StreamGeometry();
        using (var g = ridge.Open())
        {
            g.BeginFigure(new Point(tops[0].X, floor), true, true);
            g.PolyLineTo([.. tops.Select(t => new Point(t.X, t.Y)), new(tops[^1].X, floor)], true, true);
        }
        return ridge;
    }

    /// <summary>A pine tree standing on (<paramref name="x"/>, <paramref name="y"/>): three tiers and a trunk.</summary>
    static Geometry Pine(double x, double y, double size)
    {
        var tree = new GeometryGroup();
        tree.Children.Add(new RectangleGeometry(new Rect(x - 4 * size, y - 18 * size, 8 * size, 18 * size)));
        for (var tier = 0; tier < 3; tier++)
        {
            var bottom = y - (14 + tier * 30) * size;
            var half = (42 - tier * 10) * size;
            var tierShape = new StreamGeometry();
            using (var g = tierShape.Open())
            {
                g.BeginFigure(new Point(x - half, bottom), true, true);
                g.PolyLineTo([new(x, bottom - 58 * size), new(x + half, bottom)], true, true);
            }
            tree.Children.Add(tierShape);
        }
        return tree;
    }

    static Color Hex(string hex) => (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    static SolidColorBrush Solid(string hex) => new(Hex(hex));

    static LinearGradientBrush Vertical(params (string Hex, double At)[] stops) =>
        new(new GradientStopCollection(stops.Select(s => new GradientStop(Hex(s.Hex), s.At))), 90);
}

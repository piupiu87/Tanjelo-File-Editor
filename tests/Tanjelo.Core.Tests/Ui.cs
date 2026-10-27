using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Size = System.Windows.Size;

namespace Tanjelo.Core.Tests;

/// <summary>
/// The app's windows, off-screen. One STA thread holds the app's resources for the whole run; tests build windows
/// there without showing them, drive them through the methods the mouse and keyboard reach, and render them to
/// pictures, which are also saved to %TEMP%\tanjelo-tests\renders for a person to look at.
/// </summary>
static class Ui
{
    static readonly Lazy<Dispatcher> Thread = new(Start);

    public static string RenderFolder { get; } = Path.Combine(Path.GetTempPath(), "tanjelo-tests", "renders");

    static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            var app = WithoutStartup();
            app.InitializeComponent();
            Theme.Preference = "light";
            Theme.Apply(app.Resources);
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "UI tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.Result;
    }

    /// <summary>
    /// The app, for its styles and brushes, without its startup. WPF queues Startup as an Application is made and raises
    /// it once the dispatcher runs; the app's own would go into tray mode, with an icon, the global shortcut and a "ready"
    /// notification, or open a menu for any file named on the command line.
    /// </summary>
    static App WithoutStartup()
    {
        var queued = new List<DispatcherOperation>();
        DispatcherHookEventHandler collect = (_, e) => queued.Add(e.Operation);
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.Hooks.OperationPosted += collect;
        var app = new App();
        dispatcher.Hooks.OperationPosted -= collect;
        foreach (var operation in queued) operation.Abort();
        return app;
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread, one test at a time, since they share the theme.</summary>
    public static async Task Run(Func<Task> work)
    {
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await Thread.Value.InvokeAsync(() =>
            {
                UseTheme("light");
                return work();
            }).Task.Unwrap();
        }
        finally
        {
            Gate.Release();
        }
    }

    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Switches the theme. A host outside any window doesn't hear about the app's brushes changing, so it gets its own
    /// copy of them, as a shown window would see them change.
    /// </summary>
    public static void UseTheme(string theme, FrameworkElement? host = null)
    {
        Theme.Preference = theme;
        Theme.Apply(Application.Current.Resources);
        if (host is not null) Theme.Apply(host.Resources);
    }

    /// <summary>Lets layout and queued work run, as the message loop would between events.</summary>
    public static async Task Settle(int milliseconds = 30)
    {
        await Task.Delay(milliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Lays out a window's content at a size as if the window were showing, without showing it. The content leaves the
    /// window for a frame of the window's colours, which stays its parent, so later renders see the same layout.
    /// </summary>
    public static FrameworkElement Host(Window window, double width, double height)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var host = new System.Windows.Controls.Border
        {
            Child = content,
            UseLayoutRounding = true,
            Width = width,
            Height = height,
        };
        // Followed like the window follows them, so a change of theme shows in the next render.
        host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Surface");
        host.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimary");
        System.Windows.Documents.TextElement.SetFontFamily(host, (System.Windows.Media.FontFamily)Application.Current.Resources["UiFont"]);
        System.Windows.Documents.TextElement.SetFontSize(host, 13);
        Layout(host);
        return host;
    }

    public static void Layout(FrameworkElement host)
    {
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();
    }

    /// <summary>The host as a picture at 1:1, also saved as <paramref name="name"/>.png in <see cref="RenderFolder"/>.</summary>
    public static BitmapSource Render(FrameworkElement host, string name)
    {
        Layout(host);
        var bitmap = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();
        Directory.CreateDirectory(RenderFolder);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(RenderFolder, name + ".png")))
            encoder.Save(stream);
        return bitmap;
    }

    /// <summary>The colour of one pixel of a render.</summary>
    public static System.Windows.Media.Color Pixel(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return System.Windows.Media.Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>Whether two colours are within <paramref name="tolerance"/> on every channel.</summary>
    public static bool Near(System.Windows.Media.Color a, System.Windows.Media.Color b, int tolerance = 12) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    public static System.Windows.Media.Color Colour(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
}

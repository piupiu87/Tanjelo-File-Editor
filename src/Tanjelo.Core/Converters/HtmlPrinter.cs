using System.Globalization;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Prints HTML to PDF with the WebView2 runtime that comes with Windows (Edge's engine). It runs on a thread of its
/// own with a message loop and a window that's never shown, so it works from the console too. Scripts are off and
/// every request that isn't for a local file is refused, so a page can't fetch anything from the network.
/// </summary>
internal sealed class HtmlPrinter : IDisposable
{
    /// <summary>One print shouldn't take long; a page that stops it is given up on.</summary>
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    const double Margin = 2 / 2.54; // 2 cm, in inches

    // Countries that use US Letter paper rather than A4.
    static readonly HashSet<string> LetterRegions = ["US", "CA", "MX", "PH", "CL", "CO", "VE", "PR", "GT", "CR", "PA", "DO", "SV", "HN", "NI", "BZ"];

    public static string UserDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tanjelo", "WebView2");

    readonly Thread _thread;
    readonly Dispatcher _dispatcher;
    HwndSource? _window;
    CoreWebView2Environment? _environment;
    CoreWebView2Controller? _controller;

    public HtmlPrinter()
    {
        var started = new TaskCompletionSource<Dispatcher>();
        _thread = new Thread(() =>
        {
            started.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "HTML printer",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _dispatcher = started.Task.GetAwaiter().GetResult();
    }

    /// <summary>The installed WebView2 runtime's version, or null when there's none.</summary>
    public static string? RuntimeVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // WebView2RuntimeNotFoundException, or the loader DLL missing
        }
    }

    /// <summary>The paper for this region: Letter where that's the norm, otherwise A4. Width and height in inches.</summary>
    internal static (double Width, double Height) Paper(RegionInfo region) =>
        LetterRegions.Contains(region.TwoLetterISORegionName) ? (8.5, 11) : (210 / 25.4, 297 / 25.4);

    /// <summary>Prints the page at <paramref name="htmlFile"/> to <paramref name="pdf"/>, with 2 cm margins and no browser header or footer.</summary>
    public async Task PrintAsync(string htmlFile, string pdf, CancellationToken ct)
    {
        var printing = _dispatcher.InvokeAsync(() => PrintHereAsync(htmlFile, pdf)).Task.Unwrap();
        if (await Task.WhenAny(printing, Task.Delay(Timeout, ct)) != printing)
        {
            ct.ThrowIfCancellationRequested();
            throw new ConversionException("WebView2 stopped responding while printing.");
        }
        await printing;
    }

    async Task PrintHereAsync(string htmlFile, string pdf)
    {
        var web = await StartAsync();
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object? sender, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult(e.IsSuccess);
        web.NavigationCompleted += OnLoaded;
        try
        {
            web.Navigate(new Uri(Path.GetFullPath(htmlFile)).AbsoluteUri);
            if (!await loaded.Task)
                throw new ConversionException("WebView2 couldn't open the page to print it.");
        }
        finally
        {
            web.NavigationCompleted -= OnLoaded;
        }

        var (width, height) = Paper(RegionInfo.CurrentRegion);
        var settings = _environment!.CreatePrintSettings();
        settings.Orientation = CoreWebView2PrintOrientation.Portrait;
        settings.PageWidth = width;
        settings.PageHeight = height;
        settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = Margin;
        settings.ShouldPrintHeaderAndFooter = false;
        settings.ShouldPrintBackgrounds = true;
        if (!await web.PrintToPdfAsync(Path.GetFullPath(pdf), settings))
            throw new ConversionException("WebView2 couldn't print the PDF.");
    }

    async Task<CoreWebView2> StartAsync()
    {
        if (_controller is not null) return _controller.CoreWebView2;

        // WebView2 needs a window to live in. This one is a popup that is never shown.
        const int WS_POPUP = unchecked((int)0x80000000);
        _window = new HwndSource(new HwndSourceParameters("Tanjelo printer", 816, 1056) { WindowStyle = WS_POPUP });
        Directory.CreateDirectory(UserDataFolder);
        try
        {
            _environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder, new CoreWebView2EnvironmentOptions
            {
                // No update checks, safe-browsing lookups, prefetching or pings. Chromium still opens sockets ahead of
                // requests it then drops (for an iframe, say), so all traffic goes to a proxy that isn't there, even
                // for this computer's own addresses: nothing can reach a server, and no name is looked up.
                AdditionalBrowserArguments = "--disable-background-networking --dns-prefetch-disable --no-pings --disable-component-update "
                    + "--proxy-server=127.0.0.1:9 --proxy-bypass-list=<-loopback>",
            });
            _controller = await _environment.CreateCoreWebView2ControllerAsync(_window.Handle);
        }
        catch (Exception ex) when (ex is not ConversionException)
        {
            throw new ConversionException($"WebView2 couldn't start: {ex.Message.Split('\n')[0].Trim()}", ex);
        }

        var web = _controller.CoreWebView2;
        var settings = web.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.IsReputationCheckingRequired = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

        web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        web.WebResourceRequested += (_, e) =>
        {
            if (!IsLocal(e.Request.Uri))
                e.Response = _environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        web.NavigationStarting += (_, e) => e.Cancel = !IsLocal(e.Uri);
        web.FrameNavigationStarting += (_, e) => e.Cancel = !IsLocal(e.Uri);
        return web;
    }

    internal static bool IsLocal(string uri) =>
        uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                _controller?.Close();
                _window?.Dispose();
            }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Best effort: the browser process ends with ours anyway.
        }
        _dispatcher.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(10));
    }
}

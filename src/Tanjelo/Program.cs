namespace Tanjelo;

/// <summary>
/// Starts Tanjelo. Setup and the uninstaller run Tanjelo.exe with their own arguments: <c>--quit</c> asks the Tanjelo in
/// the tray to quit, and <c>--before-uninstall</c> takes away what Tanjelo added to Windows. Those are handled first,
/// without showing anything, and only an ordinary start goes on to the app.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--quit")) return Installation.Quit();
        if (args.Contains("--before-uninstall"))
        {
            Installation.BeforeUninstall();
            return 0;
        }
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

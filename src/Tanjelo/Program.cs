using Velopack;

namespace Tanjelo;

/// <summary>
/// Starts Tanjelo. The installer runs Tanjelo.exe with its own arguments after installing and before uninstalling;
/// Velopack handles those first and exits, and only an ordinary start goes on to the app.
/// </summary>
static class Program
{
    [STAThread]
    static void Main()
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => Installation.AfterInstall())
            .OnBeforeUninstallFastCallback(_ => Installation.BeforeUninstall())
            .Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

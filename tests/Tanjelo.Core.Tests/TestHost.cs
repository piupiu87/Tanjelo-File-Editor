using System.Runtime.CompilerServices;

namespace Tanjelo.Core.Tests;

static class TestHost
{
    /// <summary>
    /// Ends the test run's process the way Tanjelo and tanj end, without Windows' DLL shutdown: once Windows.Data.Pdf
    /// has drawn a page, the graphics driver can spin there for good after every result has been reported.
    /// </summary>
    [ModuleInitializer]
    internal static void EndWithoutDllShutdown() => ExitGuard.EndWhenMainReturns();
}

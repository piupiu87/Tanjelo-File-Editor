using System.Runtime.InteropServices;

namespace Tanjelo.Core;

/// <summary>
/// Ends the process without Windows' DLL shutdown. Once Windows.Data.Pdf has drawn a page, its DLL frees a Direct3D
/// device during that shutdown, and the Intel graphics driver can spin there forever: Windows has already stopped the
/// threads it waits for. A finished test run was seen stuck like that, using a whole core, for over half an hour.
/// </summary>
public static class ExitGuard
{
    /// <summary>Flushes the console and ends the process at once. Call it only when everything is saved.</summary>
    public static void EndNow(int exitCode)
    {
        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch (IOException) { /* the console or pipe is already gone */ }
        TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode));
    }

    /// <summary>Ends the process this way as soon as Main returns, keeping Main's exit code.</summary>
    public static void EndWhenMainReturns() =>
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EndNow(Environment.ExitCode);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool TerminateProcess(IntPtr process, uint exitCode);
}

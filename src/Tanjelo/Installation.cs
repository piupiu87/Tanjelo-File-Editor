using Microsoft.Win32;

namespace Tanjelo;

/// <summary>
/// What the installer's hooks do. After installing: put the install folder on the user's PATH, so <c>tanj</c> works in
/// any terminal. Before uninstalling: take that away again, with the other things Tanjelo can add to Windows (Start with
/// Windows, the "Send to" entry and the Windows 11 menu entry), so nothing is left behind. Settings in
/// %APPDATA%\Tanjelo stay for a reinstall. Each hook has 30 seconds and must not show anything.
/// </summary>
static class Installation
{
    /// <summary>The folder Velopack keeps the installed version in, the same across updates (…\Tanjelo\current).</summary>
    static string InstallFolder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static void AfterInstall() => Try(() => SetUserPath(WithFolder(UserPath(), InstallFolder)));

    public static void BeforeUninstall()
    {
        Try(() => SetUserPath(WithoutFolder(UserPath(), InstallFolder)));
        Try(() => ShellIntegration.StartsWithWindows = false);
        Try(() => ShellIntegration.InSendToMenu = false);
        Try(() => ShellIntegration.InWindows11Menu = false);
    }

    /// <summary>A PATH with <paramref name="folder"/> added at the end, unless it's there already.</summary>
    internal static string WithFolder(string path, string folder) =>
        Entries(path).Any(e => Same(e, folder)) ? path : string.Join(';', Entries(path).Append(folder));

    /// <summary>A PATH without <paramref name="folder"/>, keeping every other entry as it was.</summary>
    internal static string WithoutFolder(string path, string folder) =>
        string.Join(';', Entries(path).Where(e => !Same(e, folder)));

    static IEnumerable<string> Entries(string path) => path.Split(';', StringSplitOptions.RemoveEmptyEntries);

    static bool Same(string entry, string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'))),
            Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);

    // The user's own PATH, as written (with %VARIABLES% unexpanded), not the one this process was started with.
    static string UserPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey("Environment");
        return key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    static void SetUserPath(string path)
    {
        using var key = Registry.CurrentUser.CreateSubKey("Environment");
        if (path == (key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "")) return;
        if (path.Length == 0) key.DeleteValue("Path", throwOnMissingValue: false);
        else key.SetValue("Path", path, RegistryValueKind.ExpandString);
        // Terminals opened from now on, and Explorer, read the new PATH.
        Native.SendMessageTimeout(Native.HWND_BROADCAST, Native.WM_SETTINGCHANGE, IntPtr.Zero, "Environment", Native.SMTO_ABORTIFHUNG, 2000, out _);
    }

    static void Try(Action step)
    {
        try { step(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                   or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"installer hook: {ex.Message}");
        }
    }
}

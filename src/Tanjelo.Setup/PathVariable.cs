using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Tanjelo.Setup;

/// <summary>
/// The install folder on the PATH, the user's or the machine's by scope, so <c>tanj</c> works in any terminal opened
/// afterwards. The PATH is read and written as it's stored, with %VARIABLES% unexpanded, and every other entry is kept
/// as it was.
/// </summary>
static class PathVariable
{
    const string UserKey = "Environment";
    const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public static void Add(Places places, Scope scope, string folder) => Change(places, scope, path => WithFolder(path, folder));

    public static void Remove(Places places, Scope scope, string folder) => Change(places, scope, path => WithoutFolder(path, folder));

    public static bool Has(Places places, Scope scope, string folder) => Entries(Read(places, scope)).Any(e => Same(e, folder));

    /// <summary>A PATH with <paramref name="folder"/> added at the end, unless it's there already.</summary>
    internal static string WithFolder(string path, string folder) =>
        Entries(path).Any(e => Same(e, folder)) ? path : string.Join(";", Entries(path).Concat([folder]));

    /// <summary>A PATH without <paramref name="folder"/>, keeping every other entry as it was.</summary>
    internal static string WithoutFolder(string path, string folder) =>
        string.Join(";", Entries(path).Where(e => !Same(e, folder)));

    static IEnumerable<string> Entries(string path) => path.Split([';'], StringSplitOptions.RemoveEmptyEntries);

    static bool Same(string entry, string folder) =>
        string.Equals(Folders.Trim(Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'))), Folders.Trim(folder), StringComparison.OrdinalIgnoreCase);

    static RegistryKey? Open(Places places, Scope scope, bool writable) => scope == Scope.User
        ? writable ? places.CurrentUser.CreateSubKey(UserKey) : places.CurrentUser.OpenSubKey(UserKey)
        : writable ? places.LocalMachine.CreateSubKey(MachineKey) : places.LocalMachine.OpenSubKey(MachineKey);

    static string Read(Places places, Scope scope)
    {
        using var key = Open(places, scope, writable: false);
        return key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    static void Change(Places places, Scope scope, Func<string, string> change)
    {
        using var key = Open(places, scope, writable: true)!;
        var before = key.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var after = change(before);
        if (after == before) return;
        if (after.Length == 0 && scope == Scope.User) key.DeleteValue("Path", throwOnMissingValue: false);
        else key.SetValue("Path", after, RegistryValueKind.ExpandString);
        // Terminals opened from now on, and Explorer, read the new PATH.
        if (places.Announce)
            SendMessageTimeout(new IntPtr(0xFFFF) /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "Environment", 0x0002 /* SMTO_ABORTIFHUNG */, 2000, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}

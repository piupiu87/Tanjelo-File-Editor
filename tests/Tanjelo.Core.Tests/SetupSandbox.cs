using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using Microsoft.Win32;
using Tanjelo.Setup;

namespace Tanjelo.Core.Tests;

/// <summary>
/// A PC for setup to work on, made of a scratch folder and a scratch key in the registry: Program Files, the Start menus,
/// %LOCALAPPDATA%, the PATH and Apps are all in there, so nothing setup does reaches this PC's own. Tanjelo itself and
/// the administrator prompt are fakes; the elevated copy is setup's own code, run on a thread over a real pipe.
/// </summary>
sealed class SetupSandbox : IDisposable
{
    const string TestKeys = @"Software\Tanjelo tests";
    readonly Scratch _scratch = new();
    readonly string _key = TestKeys + @"\" + Guid.NewGuid().ToString("N");
    readonly RegistryKey _user, _machine;

    public Places Places { get; }
    public global::Tanjelo.Setup.Log Log { get; }
    public Installer Installer { get; }
    public FakeTanjelo Tanjelo { get; } = new();
    public FakeElevation Elevation { get; }
    public SetupJob Job { get; }

    public SetupSandbox()
    {
        _user = Registry.CurrentUser.CreateSubKey(_key + @"\HKCU");
        _machine = Registry.CurrentUser.CreateSubKey(_key + @"\HKLM");
        string Folder(string name) => Directory.CreateDirectory(_scratch.PathOf(name)).FullName;
        Places = new Places
        {
            LocalAppData = Folder(@"Users\someone\AppData\Local"),
            RoamingAppData = Folder(@"Users\someone\AppData\Roaming"),
            ProgramFiles = Folder("Program Files"),
            UserStartMenu = Folder(@"Users\someone\Start Menu\Programs"),
            CommonStartMenu = Folder(@"ProgramData\Start Menu\Programs"),
            SendTo = Folder(@"Users\someone\SendTo"),
            Desktop = Folder(@"Users\someone\Desktop"),
            TaskbarPins = Folder(@"Users\someone\TaskBar"),
            Temp = Folder(@"Users\someone\Temp"),
            Windows = Folder("Windows"),
            CurrentUser = _user,
            LocalMachine = _machine,
            // A test can't give a folder to Administrators without being one, so the user stands in for them.
            MachineOwner = Me,
            TrustedOwners = [Me, FolderSecurity.Administrators, FolderSecurity.LocalSystem],
        };
        Log = new global::Tanjelo.Setup.Log(_scratch.PathOf("setup.log"));
        Installer = new Installer(Places, Log);
        Elevation = new FakeElevation(this);
        Job = new SetupJob(Installer, Tanjelo, Elevation, Log) { QuitWait = TimeSpan.FromMilliseconds(400), StopWait = TimeSpan.FromMilliseconds(400) };
    }

    public string Root => _scratch.Dir;

    public string PathOf(string name) => _scratch.PathOf(name);

    /// <summary>The user running the tests.</summary>
    public static System.Security.Principal.SecurityIdentifier Me => System.Security.Principal.WindowsIdentity.GetCurrent().User!;

    /// <summary>A junction at <paramref name="link"/> to <paramref name="target"/>, made as any user can make one, with mklink.</summary>
    public static void Junction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(Folders.IsLink(link), $"no junction at {link}");
    }

    /// <summary>The made-up files of an app version, as the build zips the published folder.</summary>
    public static (string Path, string Text)[] App(string version) =>
    [
        ("Tanjelo.exe", "Tanjelo " + version),
        ("tanj.exe", "tanj " + version),
        (Installer.UninstallerName, "uninstaller " + version),
        (@"licenses\LICENSE.txt", "MIT"),
        (@"runtimes\win-x64\native\WebView2Loader.dll", "loader " + version),
    ];

    /// <summary>A payload holding <paramref name="files"/>, with the SHA-256 the build would record (or a wrong one).</summary>
    public Payload Payload(string version, (string Path, string Text)[] files, bool damaged = false)
    {
        var zip = PathOf($"payload-{version}-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var (path, text) in files)
                using (var writer = new StreamWriter(archive.CreateEntry(path.Replace('\\', '/')).Open()))
                    writer.Write(text);
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
        var payload = new Payload(() => File.OpenRead(zip), damaged ? new string('0', 64) : sha, version);
        Elevation.Payload = payload;
        return payload;
    }

    /// <summary>What the fake winget was asked, one line per run.</summary>
    public string WingetCalls => PathOf("winget-calls.txt");

    /// <summary>
    /// A fake winget, a batch file that writes down its arguments and says it installed the package; it fails like an
    /// installer would for ids in <paramref name="failing"/>, and hangs for those in <paramref name="hanging"/>.
    /// </summary>
    public Winget FakeWinget(string[]? failing = null, string[]? hanging = null, TimeSpan? timeout = null)
    {
        // Windows' own findstr and ping, by their full paths: a PATH from Git's shell has a Unix find first.
        const string Has = "\"%SystemRoot%\\System32\\findstr.exe\" /l /c:";
        var script = new System.Text.StringBuilder("@echo off\r\n");
        script.Append($"echo %*>> \"{WingetCalls}\"\r\n");
        foreach (var id in failing ?? [])
            script.Append($"echo %* | {Has}\"{id}\" >nul && (echo Installing...& echo Installer failed with exit code: 1603& exit /b 1)\r\n");
        foreach (var id in hanging ?? [])
            script.Append($"echo %* | {Has}\"{id}\" >nul && (\"%SystemRoot%\\System32\\ping.exe\" -n 30 127.0.0.1 >nul& exit /b 0)\r\n");
        script.Append("echo Successfully installed\r\nexit /b 0\r\n");
        var exe = PathOf("winget.cmd");
        File.WriteAllText(exe, script.ToString());
        return new Winget(exe, Log) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
    }

    public string UserPath => Read(_user, "Environment", "Path");
    public string MachinePath => Read(_machine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path");

    public RegistryKey? AppsEntry(Scope scope) => (scope == Scope.User ? _user : _machine).OpenSubKey(global::Tanjelo.Setup.AppsEntry.KeyPath);

    public static string Read(RegistryKey root, string key, string name)
    {
        using var opened = root.OpenSubKey(key);
        return opened?.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
    }

    public void Dispose()
    {
        _user.Dispose();
        _machine.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        using (var tests = Registry.CurrentUser.OpenSubKey(TestKeys))
            if (tests is { SubKeyCount: 0, ValueCount: 0 })
                Registry.CurrentUser.DeleteSubKey(TestKeys, throwOnMissingSubKey: false);
        _scratch.Dispose();
    }
}

/// <summary>Tanjelo as setup sees it running: programs listed by the path they run from, which quit when asked, or not.</summary>
sealed class FakeTanjelo : ITanjeloProcesses
{
    readonly Dictionary<int, string> _running = [];
    int _next = 100;

    public bool QuitsWhenAsked { get; set; } = true;
    public List<string> AskedToQuit { get; } = [];
    public List<string> Stopped { get; } = [];
    public List<string> BeforeUninstallRuns { get; } = [];
    public List<string> Started { get; } = [];

    public void Run(string exe) => _running[_next++] = exe;

    public IReadOnlyList<int> In(string folder) => _running.Where(p => Folders.Within(p.Value, folder)).Select(p => p.Key).ToList();

    public void AskToQuit(string folder)
    {
        AskedToQuit.Add(folder);
        if (QuitsWhenAsked)
            foreach (var id in In(folder)) _running.Remove(id);
    }

    public void Stop(int id, string folder)
    {
        Stopped.Add(_running[id]);
        _running.Remove(id);
    }

    public void BeforeUninstall(string exe) => BeforeUninstallRuns.Add(exe);

    public void Start(string exe) => Started.Add(exe);
}

/// <summary>
/// The administrator prompt, always answered yes (or always no): the elevated copy is setup's own helper code, run on
/// a thread with the sandbox's places, talking to setup over a real named pipe.
/// </summary>
sealed class FakeElevation(SetupSandbox sandbox) : IElevation
{
    public bool IsElevated { get; set; }
    public bool Refused { get; set; }
    public int Runs { get; private set; }
    public int ExitCode { get; private set; }

    /// <summary>The payload inside the elevated copy, which is the same Setup.exe.</summary>
    public Payload? Payload { get; set; }

    /// <summary>A pause after each of the elevated copy's reports, so a test can be sure to cancel it part way.</summary>
    public TimeSpan ReportPause { get; set; }

    public async Task<IReadOnlyList<string>> RunAsync(HelperRequest request, IProgress<Report>? progress, CancellationToken cancel)
    {
        if (Refused) throw new SetupException(global::Tanjelo.Setup.Strings.InstallingForEveryoneNeedsAdministratorPermission, ExitCodes.NoAdministrator);
        Runs++;
        var name = "Tanjelo.Tests." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var helper = Task.Run(() =>
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(10_000);
            return HelperChannel.Serve(client, (reports, stop) => request.Run(sandbox.Installer, Payload, new Reporter<Report>(report =>
            {
                reports.Report(report);
                Thread.Sleep(ReportPause);
            }), stop));
        });
        await server.WaitForConnectionAsync(cancel);
        try
        {
            return await HelperChannel.ListenAsync(server, progress, cancel);
        }
        finally
        {
            ExitCode = await helper;
        }
    }
}

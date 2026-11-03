using System.Diagnostics;
using System.IO.Pipes;
using Tanjelo.Setup;
using SetupStrings = Tanjelo.Setup.Strings;

namespace Tanjelo.Core.Tests;

/// <summary>
/// Waiting for the elevated copy, with a real program in its place: a command prompt that ends at once, or reports on
/// the pipe and outlives setup's wait. Windows reports a program's exit on a thread of its own, whenever it comes; once
/// that came after setup had finished with the copy, and the exception it threw ended setup.
/// </summary>
public class SetupHelperTests
{
    static Process Prompt(string command) => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /c \"{command}\"")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;

    static NamedPipeServerStream Pipe(out string name)
    {
        name = "Tanjelo.Tests." + Guid.NewGuid().ToString("N");
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [Fact]
    public async Task A_copy_that_ends_without_connecting_is_reported_with_its_exit_code()
    {
        using var pipe = Pipe(out _);
        using var helper = Prompt("exit 5");
        var stopped = await Assert.ThrowsAsync<SetupException>(() =>
            HelperProcess.RunAsync(helper, pipe, null, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(5)));
        Assert.Equal(string.Format(SetupStrings.TheAdministratorCopyOfSetupStopped, 5), stopped.Message);
    }

    [Fact]
    public async Task A_copy_that_ends_after_setup_is_done_with_it_harms_nothing()
    {
        Process helper;
        var exitedLate = new TaskCompletionSource<bool>();
        using (var pipe = Pipe(out var name))
        {
            // Says it's done, then lives on for a couple of seconds, longer than setup waits for it.
            helper = Prompt($"echo done>\\\\.\\pipe\\{name}& \"%SystemRoot%\\System32\\ping.exe\" -n 3 127.0.0.1 >nul");
            helper.EnableRaisingEvents = true;
            helper.Exited += (_, _) => exitedLate.TrySetResult(true);
            var left = await HelperProcess.RunAsync(helper, pipe, null, TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(100));
            Assert.Empty(left);
            Assert.False(helper.HasExited, "the prompt should still be running after setup stopped waiting");
        }
        // Its exit comes now, after everything setup used for it has gone; nothing may throw.
        await exitedLate.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.True(helper.HasExited);
        helper.Dispose();
    }

    /// <summary>A real program that waits about <paramref name="seconds"/> seconds, under the name given: a copy of ping.</summary>
    static Process Waiting(string exe, int seconds)
    {
        if (!File.Exists(exe)) File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        return Process.Start(new ProcessStartInfo(exe, $"-n {seconds + 1} 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
    }

    static void End(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (InvalidOperationException) { }
        process.Dispose();
    }

    [Fact]
    public void Only_Tanjelos_programs_count_as_Tanjelo_running_never_setup_or_the_uninstaller()
    {
        using var scratch = new Scratch();
        var processes = new TanjeloProcesses(new global::Tanjelo.Setup.Log(scratch.PathOf("setup.log")));
        // The test runner's own folder: whatever runs from it, the runner itself is never listed.
        Assert.DoesNotContain(Environment.ProcessId, processes.In(AppContext.BaseDirectory));

        Assert.True(TanjeloProcesses.IsTanjelo(@"C:\Program Files\Tanjelo\Tanjelo.exe"));
        Assert.True(TanjeloProcesses.IsTanjelo(@"C:\Program Files\Tanjelo\TANJ.EXE"));
        Assert.True(TanjeloProcesses.IsTanjelo(@"C:\Users\someone\AppData\Local\Tanjelo\Update.exe"));
        Assert.False(TanjeloProcesses.IsTanjelo(@"C:\Program Files\Tanjelo\Uninstall Tanjelo.exe"));
        Assert.False(TanjeloProcesses.IsTanjelo(@"C:\Program Files\Tanjelo\Tanjelo-1.2.0-Setup.exe"));

        // Real programs in a folder, one named as Tanjelo and one as the uninstaller.
        var folder = Directory.CreateDirectory(scratch.PathOf("Tanjelo")).FullName;
        var tanjelo = Waiting(Path.Combine(folder, "Tanjelo.exe"), 20);
        var uninstaller = Waiting(Path.Combine(folder, Installer.UninstallerName), 20);
        try
        {
            Assert.Equal([tanjelo.Id], processes.In(folder));
            processes.Stop(uninstaller.Id, folder);
            Assert.False(uninstaller.WaitForExit(500), "the uninstaller was stopped");
            processes.Stop(tanjelo.Id, folder);
            Assert.True(tanjelo.WaitForExit(5000));
        }
        finally
        {
            End(tanjelo);
            End(uninstaller);
        }
    }

    [Fact]
    public async Task Uninstalling_with_only_the_uninstaller_running_neither_waits_nor_asks_and_it_goes_once_both_copies_end()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.Machine);
        await pc.Job.InstallAsync(folder, Scope.Machine, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, () => false, TestContext.Current.CancellationToken);
        // The uninstaller as Apps starts it, and its elevated copy: two programs from the one file in the install folder.
        var uninstaller = Path.Combine(folder, Installer.UninstallerName);
        File.Delete(uninstaller);
        var parent = Waiting(uninstaller, 6);
        var helper = Waiting(uninstaller, 12);
        try
        {
            var asked = false;
            var job = new SetupJob(pc.Installer, new TanjeloProcesses(pc.Log), pc.Elevation, pc.Log) { QuitWait = TimeSpan.FromSeconds(15), StopWait = TimeSpan.FromSeconds(2) };
            var took = Stopwatch.StartNew();
            var left = await job.UninstallAsync(folder, uninstaller, null, () => asked = true, TestContext.Current.CancellationToken);
            took.Stop();

            Assert.Empty(left);
            Assert.False(asked, "it asked to stop Tanjelo, which wasn't running");
            Assert.True(took.Elapsed < TimeSpan.FromSeconds(5), $"it waited {took.Elapsed}");
            Assert.DoesNotContain("asking Tanjelo", File.ReadAllText(pc.PathOf("setup.log")));
            Assert.False(parent.HasExited);
            Assert.False(helper.HasExited);
            Assert.Null(pc.AppsEntry(Scope.Machine));
            Assert.False(File.Exists(Path.Combine(folder, "Tanjelo.exe")));
            // The uninstaller in use is skipped, and waited for: it's still there while either copy runs.
            Assert.True(File.Exists(uninstaller));
            Assert.True(parent.WaitForExit(15_000));
            await Task.Delay(1500, TestContext.Current.CancellationToken);
            Assert.False(helper.HasExited);
            Assert.True(File.Exists(uninstaller), "the uninstaller went while its elevated copy still ran");
            Assert.True(helper.WaitForExit(15_000));
            for (var waited = 0; Directory.Exists(folder) && waited < 40_000; waited += 200)
                await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            End(parent);
            End(helper);
        }
    }

    [Fact]
    public async Task Something_unexpected_shows_on_the_last_page_with_the_log()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));
        await Ui.Run(async () =>
        {
            var (window, host, attached) = SetupWizardTests.Open(pc, payload);
            using (attached)
            {
                window.GoNext();
                window.Fail(new InvalidOperationException("nobody saw this coming"));
                await Ui.Settle();
                Ui.Layout(host);
                Assert.Equal(SetupWindow.Page.Done, window.Current);
                Assert.Equal(SetupStrings.SetupStopped, window.Heading.Text);
                Assert.Equal("nobody saw this coming", window.ProblemText.Text);
                Assert.Equal(System.Windows.Visibility.Visible, window.LogLine.Visibility);
                Assert.Equal(ExitCodes.Failed, window.ExitCode);
                Assert.Contains("setup stopped: System.InvalidOperationException: nobody saw this coming", File.ReadAllText(pc.PathOf("setup.log")));
                A11y.InOrder(A11y.Check(host, "setup stopped"), window.LogLink, window.NextButton);
                Ui.Render(host, "setup-stopped-light");
            }
        });
    }
}

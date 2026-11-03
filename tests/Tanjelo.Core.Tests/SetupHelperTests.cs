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

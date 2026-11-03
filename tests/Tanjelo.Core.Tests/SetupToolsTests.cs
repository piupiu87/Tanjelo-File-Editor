using System.Windows;
using Tanjelo.Setup;
using SetupStrings = Tanjelo.Setup.Strings;

namespace Tanjelo.Core.Tests;

/// <summary>
/// The optional tools page: what's ticked and why, installing through a fake winget that succeeds, fails or hangs, the
/// Store pages for HEIC, and setup finding tools where Tanjelo does. The real winget only ever runs on CI.
/// </summary>
public class SetupToolsTests
{
    static ToolRow Row(SetupWindow window, string key) => window.Tools.Single(t => t.Tool.Key == key);

    [Fact]
    public async Task Missing_tools_most_of_Tanjelo_needs_start_ticked_each_saying_what_it_is_for()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));
        await Ui.Run(async () =>
        {
            var (window, host, attached) = SetupWizardTests.Open(pc, payload, winget: pc.FakeWinget(), installed: tool => tool.Key == "pandoc");
            using (attached)
            {
                Assert.Equal(["ffmpeg", "pandoc", "webview2", "7zip", "libreoffice", "heic"], window.Tools.Select(t => t.Tool.Key));
                Assert.True(Row(window, "ffmpeg").Ticked);
                Assert.True(Row(window, "webview2").Ticked);
                // Already there: shown as installed, and can't be ticked.
                Assert.False(Row(window, "pandoc").Ticked);
                Assert.False(Row(window, "pandoc").CanTick);
                Assert.Equal(SetupStrings.Installed, Row(window, "pandoc").Details);
                foreach (var key in new[] { "7zip", "libreoffice", "heic" })
                {
                    Assert.False(Row(window, key).Ticked, key);
                    Assert.True(Row(window, key).CanTick, key);
                }
                Assert.All(window.Tools, row => Assert.False(string.IsNullOrWhiteSpace(row.Purpose)));
                Assert.Contains(SetupStrings.AsksForAdministratorPermission, Row(window, "7zip").Details);
                Assert.Contains(SetupStrings.GplInstalledAsItsOwnProgram, Row(window, "ffmpeg").Details);

                window.GoNext();
                window.GoNext();
                await Ui.Settle();
                Ui.Layout(host);
                Assert.Equal(SetupWindow.Page.Tools, window.Current);
                Assert.Equal(SetupStrings.TickedToolsAreInstalledWithWinget, window.ToolsIntro.Text);
                // Each row's box is named for the tool, and Tab reaches it and its licence link.
                var order = A11y.Check(host, "setup tools");
                var boxes = order.OfType<System.Windows.Controls.CheckBox>().ToList();
                Assert.Equal(5, boxes.Count); // pandoc's is off
                Assert.Equal("ffmpeg", System.Windows.Automation.AutomationProperties.GetName(boxes[0]));
                Assert.Equal(SetupStrings.FfmpegPurpose, System.Windows.Automation.AutomationProperties.GetHelpText(boxes[0]));
                A11y.InOrder(order, boxes[0], boxes[^1], window.BackButton, window.NextButton);
                SetupWizardTests.RenderInThemes(window, host, "tools-page");
            }
        });
    }

    [Fact]
    public async Task Without_winget_the_rows_link_to_each_tools_site()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));
        await Ui.Run(async () =>
        {
            var (window, host, attached) = SetupWizardTests.Open(pc, payload);
            using (attached)
            {
                foreach (var row in window.Tools.Where(t => t.Tool.Key != "heic"))
                {
                    Assert.False(row.CanTick, row.Name);
                    Assert.False(row.Ticked, row.Name);
                    Assert.Equal(Visibility.Visible, row.SiteVisibility);
                }
                // The Store doesn't need winget.
                Assert.True(Row(window, "heic").CanTick);
                window.GoNext();
                window.GoNext();
                await Ui.Settle();
                Ui.Layout(host);
                Assert.Equal(SetupStrings.WingetIsnTOnThisPC, window.ToolsIntro.Text);
                A11y.Check(host, "setup tools without winget");
                Ui.Render(host, "setup-tools-no-winget-light");
                window.GoNext();
                await window.Working;
                Assert.Equal(SetupStrings.TanjeloIsInstalled, window.Heading.Text);
                Assert.False(File.Exists(pc.WingetCalls));
            }
        });
    }

    [Fact]
    public async Task Ticked_tools_are_installed_with_winget_and_a_failure_is_listed_without_failing_the_install()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));
        await Ui.Run(async () =>
        {
            var opened = new List<string>();
            var (window, host, attached) = SetupWizardTests.Open(pc, payload, winget: pc.FakeWinget(failing: ["EdgeWebView2Runtime"]));
            using (attached)
            {
                window.Open = opened.Add;
                Row(window, "7zip").Ticked = true;
                Row(window, "heic").Ticked = true;
                window.GoNext();
                window.GoNext();
                window.GoNext();
                await window.Working;
                await Ui.Settle();
                Ui.Layout(host);

                Assert.Equal(ExitCodes.Done, window.ExitCode);
                Assert.Equal(SetupStrings.TanjeloIsInstalled, window.Heading.Text);
                // Each through winget, just for the user where the package can be.
                var calls = File.ReadAllLines(pc.WingetCalls).Select(l => l.Trim()).ToList();
                Assert.Equal(
                [
                    "install --id Gyan.FFmpeg.Essentials --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                    "install --id JohnMacFarlane.Pandoc --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --scope user",
                    "install --id Microsoft.EdgeWebView2Runtime --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --scope user",
                    "install --id 7zip.7zip --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                ], calls);
                // HEIC opens its two Store pages instead.
                Assert.Equal(["ms-windows-store://pdp/?ProductId=9PMMSR1CGPWG", "ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7"], opened);
                Assert.Equal(string.Format(SetupStrings.AlsoInstalled, "ffmpeg, pandoc, 7-Zip"), window.ToolsDoneText.Text);
                var problem = Assert.Single((IEnumerable<ToolProblem>)window.ToolProblems.ItemsSource);
                Assert.Equal(new ToolProblem(SetupStrings.WebView2Runtime, "Installer failed with exit code: 1603", "https://developer.microsoft.com/microsoft-edge/webview2/"), problem);
                A11y.Check(host, "setup done with a failed tool");
                Ui.Render(host, "setup-done-tools-light");
            }
        });
    }

    [Fact]
    public async Task Cancel_during_the_tools_skips_the_ones_not_started()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));
        await Ui.Run(async () =>
        {
            var (window, host, attached) = SetupWizardTests.Open(pc, payload, winget: pc.FakeWinget(hanging: ["Gyan.FFmpeg"], timeout: TimeSpan.FromSeconds(3)));
            using (attached)
            {
                window.GoNext();
                window.GoNext();
                window.GoNext();
                var ffmpeg = SetupStrings.InstallingTool.Replace("{0}", "ffmpeg");
                for (var waited = 0; window.StatusText.Text != ffmpeg; waited += 20)
                {
                    Assert.True(waited < 20_000, "ffmpeg never started");
                    await Ui.Settle(20);
                }
                Assert.Equal(SetupStrings.SkipTheRest, window.CancelButton.Content);
                Assert.True(window.CancelButton.IsEnabled);
                window.Cancel();
                Assert.Equal(SetupStrings.TheRestAreSkippedOnceThisOne, window.StatusText.Text);
                await window.Working;

                // The one under way was left to its timeout; the rest never ran, and Tanjelo is installed all the same.
                Assert.Equal(ExitCodes.Done, window.ExitCode);
                var calls = File.ReadAllLines(pc.WingetCalls);
                Assert.Single(calls);
                var problem = Assert.Single((IEnumerable<ToolProblem>)window.ToolProblems.ItemsSource);
                Assert.Equal("ffmpeg", problem.Name);
                Assert.Equal(string.Format(SetupStrings.TookLongerThanMinutes, 1), problem.Reason);
            }
        });
    }

    [Fact]
    public void The_reason_given_is_wingets_last_line_that_says_something()
    {
        Assert.Equal("Installer failed with exit code: 1603", Winget.LastLine("Found ffmpeg\r\n  ██████████▒▒  80%\r\nInstaller failed with exit code: 1603\r\n"));
        Assert.Equal("", Winget.LastLine("   -\r   \\\r   |\r"));
    }

    [Fact]
    public async Task A_winget_that_hangs_is_given_up_on()
    {
        using var pc = new SetupSandbox();
        var winget = pc.FakeWinget(hanging: ["7zip"], timeout: TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;
        var reason = await winget.InstallAsync(OptionalTools.All.Single(t => t.Key == "7zip"), TestContext.Current.CancellationToken);
        Assert.Equal(string.Format(SetupStrings.TookLongerThanMinutes, 1), reason);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
        Assert.Null(await winget.InstallAsync(OptionalTools.All.Single(t => t.Key == "ffmpeg"), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The real winget, installing 7-Zip, small and quick. It installs software, so it runs only where
    /// TANJELO_REAL_WINGET is 1, as on CI's throwaway runner, and only if that runner has winget.
    /// </summary>
    [Fact]
    public async Task With_the_real_winget_a_tool_installs_and_setup_then_finds_it()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TANJELO_REAL_WINGET") == "1", "TANJELO_REAL_WINGET isn't 1: this installs 7-Zip");
        Assert.SkipUnless(Winget.Find() is not null, "winget isn't on this PC");
        using var pc = new SetupSandbox();
        var winget = new Winget(Winget.Find()!, pc.Log);
        var sevenZip = OptionalTools.All.Single(t => t.Key == "7zip");
        var reason = await winget.InstallAsync(sevenZip, TestContext.Current.CancellationToken);
        Assert.True(reason is null, $"{reason}\n{File.ReadAllText(pc.PathOf("setup.log"))}");
        Assert.True(OptionalTools.IsInstalled(sevenZip));
        // A second time it's there already, which counts as done.
        Assert.Null(await winget.InstallAsync(sevenZip, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Setup_finds_tools_where_Tanjelo_does()
    {
        // Both look at this PC as it is; neither changes it.
        var toolbox = Toolbox.Detect();
        var found = OptionalTools.All.ToDictionary(t => t.Key, OptionalTools.IsInstalled);
        Assert.Equal(toolbox.Ffmpeg is not null, found["ffmpeg"]);
        Assert.Equal(toolbox.Pandoc is not null, found["pandoc"]);
        Assert.Equal(toolbox.SevenZip is not null, found["7zip"]);
        Assert.Equal(toolbox.LibreOffice is not null, found["libreoffice"]);
        Assert.Equal(toolbox.WebView2 is not null, found["webview2"]);
        Assert.Equal(toolbox.Heic, found["heic"]);
    }
}

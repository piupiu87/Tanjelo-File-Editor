using System.Windows;
using System.Windows.Automation;
using Tanjelo.Setup;
using SetupStrings = Tanjelo.Setup.Strings;
using SetupTheme = Tanjelo.Setup.Theme;

namespace Tanjelo.Core.Tests;

/// <summary>
/// Setup's wizard and the uninstaller's, off-screen on a sandbox PC (<see cref="SetupSandbox"/>): driven page by page
/// as the buttons drive them, rendered in light, dark and a contrast theme, with every control named and reached by Tab.
/// </summary>
public class SetupWizardTests
{
    public const double Width = 620, Height = 560;

    /// <summary>Setup's wizard for <paramref name="payload"/>, on the UI thread, laid out and in a window that's never shown.</summary>
    /// <param name="winget">The fake winget the tools page uses; none means no winget on this PC.</param>
    /// <param name="installed">Which optional tools are there already; none by default.</param>
    internal static (SetupWindow Window, FrameworkElement Host, IDisposable Attached) Open(SetupSandbox pc, Payload payload, string[]? args = null,
        Winget? winget = null, Func<OptionalTool, bool>? installed = null)
    {
        SetupTheme.DarkOverride = false;
        SetupTheme.ContrastOverride = null;
        var window = new SetupWindow(pc.Job, payload, Options.Parse(args ?? []), pc.Log, winget, installed ?? (_ => false)) { Ask = _ => true, Open = _ => { } };
        window.ApplyTheme();
        var host = Ui.Host(window, Width, Height);
        return (window, host, A11y.Attach(host));
    }

    static async Task Settle(FrameworkElement host)
    {
        await Ui.Settle();
        Ui.Layout(host);
    }

    /// <summary>The page as it looks in light, dark and Windows' Aquatic contrast theme.</summary>
    internal static void RenderInThemes(SetupWindow window, FrameworkElement host, string name)
    {
        try
        {
            Ui.Render(host, $"setup-{name}-light");
            SetupTheme.DarkOverride = true;
            window.ApplyTheme();
            Ui.Render(host, $"setup-{name}-dark");
            SetupTheme.ContrastOverride = SetupTheme.Contrast.Aquatic;
            window.ApplyTheme();
            var render = Ui.Render(host, $"setup-{name}-contrast");
            Assert.True(Ui.Near(SetupTheme.Contrast.Aquatic.Window, Ui.Pixel(render, 2, 2)), $"{name}: the background should be the contrast theme's window colour");
        }
        finally
        {
            SetupTheme.DarkOverride = false;
            SetupTheme.ContrastOverride = null;
            window.ApplyTheme();
        }
    }

    static async Task Until(Func<bool> condition, string what)
    {
        for (var waited = 0; !condition(); waited += 20)
        {
            Assert.True(waited < 20_000, $"waited too long for {what}");
            await Ui.Settle(20);
        }
    }

    [Fact]
    public async Task Every_page_is_named_reached_by_Tab_and_drawn_in_each_theme()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));
        await Ui.Run(async () =>
        {
            // ffmpeg is there already; winget installs the rest that are ticked.
            var (window, host, attached) = Open(pc, payload, ["--all-users"], pc.FakeWinget(), tool => tool.Key == "ffmpeg");
            using (attached)
            {
                await Settle(host);
                Assert.Equal(SetupWindow.Page.Welcome, window.Current);
                Assert.Equal(SetupStrings.InstallTanjelo, window.Heading.Text);
                var order = A11y.Check(host, "setup welcome");
                A11y.InOrder(order, window.LicenceLink, window.NextButton, window.CancelButton);
                RenderInThemes(window, host, "welcome");

                window.GoNext();
                await Settle(host);
                Assert.Equal(SetupWindow.Page.Location, window.Current);
                Assert.True(window.ForEveryone.IsChecked);
                order = A11y.Check(host, "setup location");
                A11y.InOrder(order, window.ForMe, window.ForEveryone, window.FolderBox, window.BrowseButton, window.BackButton, window.NextButton, window.CancelButton);
                Assert.Equal(SetupStrings.Folder, new System.Windows.Automation.Peers.TextBoxAutomationPeer(window.FolderBox).GetName());
                Assert.Equal(SetupStrings.Next, window.NextButton.Content);
                RenderInThemes(window, host, "location");

                window.GoNext();
                await Settle(host);
                Assert.Equal(SetupWindow.Page.Tools, window.Current);
                Assert.Equal(SetupStrings.Install, window.NextButton.Content);
                order = A11y.Check(host, "setup tools");
                A11y.InOrder(order, window.BackButton, window.NextButton, window.CancelButton);
                RenderInThemes(window, host, "tools");

                // For everyone, so the elevated copy's reports can be slowed enough to see the page while it works.
                pc.Elevation.ReportPause = TimeSpan.FromMilliseconds(150);
                window.GoNext();
                await Until(() => window.Bar.Value > 0.1, "the copy to start");
                Ui.Layout(host);
                Assert.Equal(SetupWindow.Page.Progress, window.Current);
                Assert.StartsWith(SetupStrings.CopyingTanjeloOf[..15], window.StatusText.Text);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(window.StatusText));
                order = A11y.Check(host, "setup installing");
                Assert.Contains(window.CancelButton, order);
                RenderInThemes(window, host, "installing");
                pc.Elevation.ReportPause = TimeSpan.Zero;
                await window.Working;
                await Settle(host);

                Assert.Equal(SetupWindow.Page.Done, window.Current);
                Assert.Equal(SetupStrings.TanjeloIsInstalled, window.Heading.Text);
                Assert.Equal(1, window.Bar.Value);
                Assert.Equal(string.Format(SetupStrings.AlsoInstalled, $"pandoc, {SetupStrings.WebView2Runtime}"), window.ToolsDoneText.Text);
                order = A11y.Check(host, "setup done");
                A11y.InOrder(order, window.StartCheck, window.NextButton);
                RenderInThemes(window, host, "done");
            }

            var folder = pc.Installer.DefaultFolder(Scope.Machine);
            var uninstall = new SetupWindow(pc.Job, folder, null, pc.Log) { Ask = _ => true };
            uninstall.ApplyTheme();
            var uninstallHost = Ui.Host(uninstall, Width, Height);
            using (A11y.Attach(uninstallHost))
            {
                await Settle(uninstallHost);
                Assert.Equal(SetupWindow.Page.Confirm, uninstall.Current);
                Assert.Contains(folder, uninstall.ConfirmText.Text);
                A11y.InOrder(A11y.Check(uninstallHost, "uninstall"), uninstall.NextButton, uninstall.CancelButton);
                Assert.Equal(SetupStrings.Uninstall, uninstall.NextButton.Content);
                RenderInThemes(uninstall, uninstallHost, "uninstall");
                uninstall.GoNext();
                await uninstall.Working;
                await Settle(uninstallHost);
                Assert.Equal(SetupStrings.TanjeloIsUninstalled, uninstall.Heading.Text);
                Assert.Equal(Visibility.Collapsed, uninstall.StartCheck.Visibility);
                A11y.Check(uninstallHost, "uninstalled");
                RenderInThemes(uninstall, uninstallHost, "uninstalled");
                Assert.Equal(ExitCodes.Done, uninstall.ExitCode);
            }
            Assert.False(Directory.Exists(folder));
        });
    }

    [Fact]
    public async Task A_whole_install_just_for_me_through_the_wizard()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));
        await Ui.Run(async () =>
        {
            var (window, host, attached) = Open(pc, payload);
            using (attached)
            {
                window.GoNext();
                await Settle(host);
                var mine = pc.Installer.DefaultFolder(Scope.User);
                Assert.True(window.ForMe.IsChecked);
                Assert.Equal(mine, window.FolderBox.Text);
                Assert.Equal(string.Format(SetupStrings.TanjeloGoesInAFolderOfItsOwn, mine), window.FolderNote.Text);

                // Choosing everyone moves a default folder to Program Files, and back.
                window.ForEveryone.IsChecked = true;
                Assert.Equal(pc.Installer.DefaultFolder(Scope.Machine), window.FolderBox.Text);
                window.ForMe.IsChecked = true;
                Assert.Equal(mine, window.FolderBox.Text);

                // A folder setup refuses says why and can't go on.
                var busy = Directory.CreateDirectory(pc.PathOf(@"Busy\Tanjelo")).FullName;
                File.WriteAllText(Path.Combine(busy, "report.docx"), "someone's");
                window.FolderBox.Text = pc.PathOf("Busy");
                Assert.Equal(Visibility.Visible, window.FolderProblem.Visibility);
                Assert.Equal(string.Format(SetupStrings.AlreadyHasFilesInItChooseAn, busy), window.FolderProblem.Text);
                Assert.False(window.NextButton.IsEnabled);
                window.GoNext();
                Assert.Equal(SetupWindow.Page.Location, window.Current);

                // Any other folder gets one of Tanjelo's own inside it.
                var apps = Directory.CreateDirectory(pc.PathOf("My Apps")).FullName;
                window.FolderBox.Text = apps;
                Assert.Equal(Visibility.Collapsed, window.FolderProblem.Visibility);
                Assert.True(window.NextButton.IsEnabled);
                Assert.Equal(Path.Combine(apps, "Tanjelo"), window.Target);
                window.GoBack();
                Assert.Equal(SetupWindow.Page.Welcome, window.Current);
                window.GoNext();
                window.GoNext();
                window.GoNext();
                await window.Working;

                Assert.Equal(SetupWindow.Page.Done, window.Current);
                Assert.Equal(ExitCodes.Done, window.ExitCode);
                Assert.Equal(0, pc.Elevation.Runs);
                Assert.True(File.Exists(Path.Combine(apps, "Tanjelo", "Tanjelo.exe")));
                Assert.True(window.StartCheck.IsChecked);
                window.Finish();
                Assert.Equal([Path.Combine(apps, "Tanjelo", "Tanjelo.exe")], pc.Tanjelo.Started);
            }
        });
    }

    [Fact]
    public async Task An_installed_Tanjelo_is_updated_where_it_is()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.User);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, () => false, CancellationToken.None);
        var payload = pc.Payload("1.3.0", SetupSandbox.App("1.3.0"));
        await Ui.Run(async () =>
        {
            var (window, host, attached) = Open(pc, payload, ["--all-users"]);
            using (attached)
            {
                await Settle(host);
                Assert.Equal(SetupStrings.UpdateTanjelo, window.Heading.Text);
                Assert.Equal(string.Format(SetupStrings.Version, "1.3.0"), window.Subheading.Text);
                Assert.Equal(string.Format(SetupStrings.TanjeloIsInstalledForYouIn, "1.2.0", folder, "1.3.0"), window.InstalledText.Text);
                window.GoNext();
                await Settle(host);
                // Never two copies: the scope and folder can't be changed.
                Assert.True(window.ForMe.IsChecked);
                Assert.False(window.ForEveryone.IsEnabled);
                Assert.False(window.FolderBox.IsEnabled);
                Assert.Equal(string.Format(SetupStrings.TanjeloIsInstalledInSoItSUpdated, folder), window.FolderNote.Text);
                Assert.Equal(SetupStrings.Next, window.NextButton.Content);
                A11y.Check(host, "setup update");
                RenderInThemes(window, host, "update");
                window.GoNext();
                Assert.Equal(SetupStrings.Update, window.NextButton.Content);
                window.GoNext();
                await window.Working;
                Assert.Equal(SetupStrings.TanjeloIsUpdated, window.Heading.Text);
                Assert.Equal("Tanjelo 1.3.0", File.ReadAllText(Path.Combine(folder, "Tanjelo.exe")));
            }
        });
    }

    [Fact]
    public async Task Without_administrator_permission_it_goes_back_to_choosing()
    {
        using var pc = new SetupSandbox();
        pc.Elevation.Refused = true;
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));
        await Ui.Run(async () =>
        {
            var (window, host, attached) = Open(pc, payload, ["--all-users"]);
            using (attached)
            {
                window.GoNext();
                window.GoNext();
                window.GoNext();
                await window.Working;
                Assert.Equal(SetupWindow.Page.Location, window.Current);
                Assert.Equal(SetupStrings.InstallingForEveryoneNeedsAdministratorPermission, window.FolderProblem.Text);
                Assert.Equal(Visibility.Visible, window.FolderProblem.Visibility);
                // Just for me goes ahead.
                window.ForMe.IsChecked = true;
                Assert.True(window.NextButton.IsEnabled);
                window.GoNext();
                window.GoNext();
                await window.Working;
                Assert.Equal(SetupStrings.TanjeloIsInstalled, window.Heading.Text);
            }
        });
    }

    [Fact]
    public async Task Cancelling_the_copy_undoes_it_and_says_so()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", [.. SetupSandbox.App("1.2.0"), .. Enumerable.Range(0, 30).Select(i => ($@"data\{i}.bin", new string('x', 1 << 16)))]);
        await Ui.Run(async () =>
        {
            var (window, host, attached) = Open(pc, payload, ["--all-users"]);
            using (attached)
            {
                pc.Elevation.ReportPause = TimeSpan.FromMilliseconds(50);
                window.GoNext();
                window.GoNext();
                window.GoNext();
                await Until(() => window.Bar.Value > 0, "the copy to start");
                Assert.True(window.CancelButton.IsEnabled);
                window.Cancel();
                Assert.False(window.CancelButton.IsEnabled);
                await window.Working;
                Assert.Equal(SetupWindow.Page.Done, window.Current);
                Assert.Equal(SetupStrings.SetupWasCancelled, window.Heading.Text);
                Assert.Equal(SetupStrings.NothingWasInstalled, window.DoneText.Text);
                Assert.Equal(Visibility.Collapsed, window.StartCheck.Visibility);
                Assert.Equal(ExitCodes.Cancelled, window.ExitCode);
                Assert.False(Directory.Exists(pc.Installer.DefaultFolder(Scope.Machine)));
                Ui.Layout(host);
                Ui.Render(host, "setup-cancelled-light");
            }
        });
    }

    [Fact]
    public async Task Tanjelo_still_running_is_stopped_only_if_the_person_says_so()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.User);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, () => false, CancellationToken.None);
        pc.Tanjelo.Run(Path.Combine(folder, "Tanjelo.exe"));
        pc.Tanjelo.QuitsWhenAsked = false;
        var payload = pc.Payload("1.3.0", SetupSandbox.App("1.3.0"));
        await Ui.Run(async () =>
        {
            var (window, host, attached) = Open(pc, payload);
            using (attached)
            {
                var asked = new List<string>();
                window.Ask = question =>
                {
                    asked.Add(question);
                    return false;
                };
                window.GoNext();
                window.GoNext();
                window.GoNext();
                await window.Working;
                Assert.Equal([SetupStrings.TanjeloIsStillRunningAndDidnT], asked);
                Assert.Equal(SetupStrings.TanjeloWasnTInstalled, window.Heading.Text);
                Assert.Equal(SetupStrings.TanjeloIsStillRunningCloseItFrom, window.ProblemText.Text);
                Assert.Equal(string.Format(SetupStrings.TanjeloStaysAsItWas, "1.2.0"), window.DoneText.Text);
                Assert.Equal(Visibility.Visible, window.LogLine.Visibility);
                Assert.Empty(pc.Tanjelo.Stopped);
                await Settle(host);
                A11y.InOrder(A11y.Check(host, "setup failed"), window.LogLink, window.NextButton);
                Ui.Render(host, "setup-failed-light");
            }
        });
    }
}

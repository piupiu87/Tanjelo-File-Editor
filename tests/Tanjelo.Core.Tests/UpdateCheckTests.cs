using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace Tanjelo.Core.Tests;

/// <summary>
/// The opt-in update check, against a fake of GitHub's answer: the handler below stands in for the network, so these
/// tests never reach it, and it records every request so they can say exactly what would have been sent.
/// </summary>
public class UpdateCheckTests
{
    /// <summary>Answers every request with the same status and body, and keeps the requests.</summary>
    sealed class FakeGitHub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public Exception? Fail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests.Add(request);
            if (Fail is not null) throw Fail;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body), RequestMessage = request });
        }
    }

    static string Release(string tag, string page) =>
        $$"""{"tag_name": "{{tag}}", "name": "Tanjelo {{tag}}", "html_url": "{{page}}", "assets": [], "body": "Notes"}""";

    const string Page = "https://github.com/piupiu87/Tanjelo-File-Editor/releases/tag/v1.2.0";

    [Fact]
    public async Task Off_it_sends_nothing_at_all()
    {
        using var scratch = new Scratch();
        var github = new FakeGitHub(HttpStatusCode.OK, Release("v9.0.0", Page));
        var check = new UpdateCheck(github, scratch.PathOf("update.json"), new Version(1, 0, 0), () => DateTimeOffset.Now);
        for (var i = 0; i < 3; i++)
            Assert.False(await check.RunIfDueAsync(enabled: false, TestContext.Current.CancellationToken));
        Assert.Empty(github.Requests);
        Assert.False(File.Exists(scratch.PathOf("update.json")));
        Assert.Equal(("", null), check.Describe());
    }

    [Fact]
    public async Task On_it_asks_GitHub_once_a_day_and_announces_a_new_version_once()
    {
        using var scratch = new Scratch();
        var state = scratch.PathOf("update.json");
        var now = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);
        var github = new FakeGitHub(HttpStatusCode.OK, Release("v1.2.0", Page));
        var check = new UpdateCheck(github, state, new Version(1, 0, 0, 0), () => now);

        Assert.True(await check.RunIfDueAsync(enabled: true, TestContext.Current.CancellationToken));
        var request = Assert.Single(github.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(UpdateCheck.LatestUrl, request.RequestUri!.ToString());
        Assert.Null(request.Content);
        // Nothing but the request: GitHub's required User-Agent, without a version, and what kind of answer is wanted.
        Assert.Equal(["Accept", "User-Agent"], request.Headers.Select(h => h.Key).Order());
        Assert.Equal("Tanjelo", request.Headers.UserAgent.ToString());

        Assert.Equal("1.2.0", check.Newer);
        Assert.Equal(Page, check.Page);
        Assert.True(check.ShouldAnnounce);
        check.MarkAnnounced();
        Assert.False(check.ShouldAnnounce);
        var (text, link) = check.Describe();
        Assert.StartsWith("Tanjelo 1.2.0 is out (checked ", text);
        Assert.Equal(Page, link);

        // Within the day, nothing more is asked, even by a new run of the app.
        now = now.AddHours(23);
        Assert.False(await check.RunIfDueAsync(true, TestContext.Current.CancellationToken));
        var restarted = new UpdateCheck(github, state, new Version(1, 0, 0), () => now);
        Assert.False(await restarted.RunIfDueAsync(true, TestContext.Current.CancellationToken));
        Assert.Single(github.Requests);
        Assert.False(restarted.ShouldAnnounce); // announced already

        // A day on, it asks again; a clock set back counts as due rather than never.
        now = now.AddHours(1);
        Assert.True(await restarted.RunIfDueAsync(true, TestContext.Current.CancellationToken));
        now = now.AddDays(-30);
        Assert.True(await restarted.RunIfDueAsync(true, TestContext.Current.CancellationToken));
        Assert.Equal(3, github.Requests.Count);
    }

    [Fact]
    public async Task The_same_or_an_older_version_is_the_latest()
    {
        using var scratch = new Scratch();
        foreach (var tag in new[] { "v1.0.0", "1.0", "v0.9.5" })
        {
            var github = new FakeGitHub(HttpStatusCode.OK, Release(tag, Page));
            var check = new UpdateCheck(github, scratch.PathOf(tag + ".json"), new Version(1, 0, 0), () => DateTimeOffset.Now);
            Assert.True(await check.RunIfDueAsync(true, TestContext.Current.CancellationToken));
            Assert.Null(check.Newer);
            Assert.False(check.ShouldAnnounce);
            Assert.StartsWith("Tanjelo 1.0.0 is the latest version (checked today at ", check.Describe().Text);
            Assert.Null(check.Describe().Link);
        }
    }

    [Fact]
    public async Task Problems_are_shown_quietly_and_wait_a_day_like_an_answer()
    {
        using var scratch = new Scratch();
        var now = DateTimeOffset.Now;
        var cases = new (FakeGitHub GitHub, string Expected)[]
        {
            (new FakeGitHub(HttpStatusCode.Forbidden, """{"message": "API rate limit exceeded"}"""), "GitHub answered 403"),
            (new FakeGitHub(HttpStatusCode.OK, "<html>a captive portal</html>"), ""),
            (new FakeGitHub(HttpStatusCode.OK, """{"name": "no tag"}"""), "didn't name a version"),
            (new FakeGitHub(HttpStatusCode.OK, Release("nightly", Page)), "didn't name a version"),
            (new FakeGitHub(HttpStatusCode.OK, "") { Fail = new HttpRequestException("No such host is known.") }, "No such host is known."),
            (new FakeGitHub(HttpStatusCode.OK, "") { Fail = new TaskCanceledException() }, "didn't answer in time"),
        };
        var i = 0;
        foreach (var (github, expected) in cases)
        {
            var check = new UpdateCheck(github, scratch.PathOf($"{i++}.json"), new Version(1, 0, 0), () => now);
            Assert.True(await check.RunIfDueAsync(true, TestContext.Current.CancellationToken));
            Assert.NotNull(check.Last.Problem);
            Assert.Contains(expected, check.Last.Problem);
            Assert.StartsWith("Couldn't check today at ", check.Describe().Text);
            Assert.Null(check.Newer);
            Assert.False(await check.RunIfDueAsync(true, TestContext.Current.CancellationToken));
            Assert.Single(github.Requests);
        }
    }

    [Fact]
    public async Task Only_a_release_page_of_this_project_is_opened()
    {
        using var scratch = new Scratch();
        foreach (var page in new[] { "https://example.com/releases/tag/v2.0.0", "https://github.com/piupiu87/Tanjelo-File-Editor/releases.evil.com/x", "" })
        {
            var check = new UpdateCheck(new FakeGitHub(HttpStatusCode.OK, Release("v2.0.0", page)), scratch.PathOf(Guid.NewGuid() + ".json"),
                new Version(1, 0, 0), () => DateTimeOffset.Now);
            await check.RunIfDueAsync(true, TestContext.Current.CancellationToken);
            Assert.Equal("2.0.0", check.Newer);
            Assert.Equal(UpdateCheck.ReleasesPage, check.Page);
        }
    }

    [Fact]
    public async Task The_settings_window_turns_it_on_and_shows_what_it_found()
    {
        await Ui.Run(async () =>
        {
            var applied = new List<Settings>();
            var opened = new List<string>();
            var window = new SettingsWindow(new Settings(), applied.Add, new FakeWindowsSettings(), (_, _) => new Toolbox());
            window.PageClicked += opened.Add;
            var host = Ui.Host(window, 600, 1600);
            await Ui.Settle();
            Assert.False(window.CheckUpdatesBox.IsChecked); // off by default
            Assert.Equal(Visibility.Collapsed, window.UpdateStatus.Visibility);

            window.CheckUpdatesBox.IsChecked = true;
            window.CheckUpdatesBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.True(applied[^1].CheckForUpdates);

            window.ShowUpdate("Tanjelo 1.2.0 is out (checked today at 09:30).", Page);
            Assert.Equal(Visibility.Visible, window.UpdateStatus.Visibility);
            Assert.Contains(window.UpdateLink, window.UpdateStatus.Inlines);
            window.UpdateLink.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
            Assert.Equal([Page], opened);
            Ui.Render(host, "settings-update");

            // With nothing to see, there's no link to reach.
            window.ShowUpdate("Tanjelo 1.0.0 is the latest version (checked today at 09:30).", null);
            Assert.DoesNotContain(window.UpdateLink, window.UpdateStatus.Inlines);
        });
    }

    sealed class FakeWindowsSettings : IWindowsSettings
    {
        public bool StartsWithWindows { get; set; }
        public bool InSendToMenu { get; set; }
        public bool CanUseWindows11Menu { get; set; }
        public bool InWindows11Menu { get; set; }
    }
}

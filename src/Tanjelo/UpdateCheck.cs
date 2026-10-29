using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tanjelo;

/// <summary>
/// The opt-in update check, the only network request Tanjelo makes itself. When on, it asks GitHub for the latest
/// release at most once a day and remembers the answer in %LOCALAPPDATA%\Tanjelo\update.json. It never downloads
/// anything: a newer version is announced with a link to its release page.
/// </summary>
sealed class UpdateCheck
{
    public const string LatestUrl = "https://api.github.com/repos/piupiu87/Tanjelo-File-Editor/releases/latest";
    public const string ReleasesPage = "https://github.com/piupiu87/Tanjelo-File-Editor/releases";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public static string DefaultStatePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tanjelo", "update.json");

    /// <summary>What the last check found. <see cref="Checked"/> is set by failed checks too, so a day passes before the next.</summary>
    public sealed record Status(DateTimeOffset? Checked = null, string? Latest = null, string? Page = null, string? Announced = null, string? Problem = null);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    readonly HttpMessageHandler _handler;
    readonly string _statePath;
    readonly Func<DateTimeOffset> _now;

    public Version Current { get; }
    public Status Last { get; private set; }

    /// <param name="handler">Sends the request; tests pass a fake, so they never reach the network.</param>
    public UpdateCheck(HttpMessageHandler handler, string statePath, Version current, Func<DateTimeOffset> now)
    {
        _handler = handler;
        _statePath = statePath;
        _now = now;
        Current = new Version(current.Major, current.Minor, Math.Max(current.Build, 0));
        Last = Load(statePath);
    }

    public bool Due => Last.Checked is not { } at || _now() - at >= Interval || at > _now();

    /// <summary>A version newer than this one, if the last check found one.</summary>
    public string? Newer => Last.Latest is { } latest && Parse(latest) is { } version && version > Current ? latest : null;

    /// <summary>Whether the newer version is yet to be announced in the notification area; each version is announced once.</summary>
    public bool ShouldAnnounce => Newer is { } newer && newer != Last.Announced;

    /// <summary>The release page to open: the one GitHub named, if it's this project's, or the list of releases.</summary>
    public string Page => Last.Page is { } page && page.StartsWith(ReleasesPage + "/", StringComparison.Ordinal) ? page : ReleasesPage;

    /// <summary>Asks GitHub when the check is on and a day has passed since the last; returns whether it asked. Never throws.</summary>
    public async Task<bool> RunIfDueAsync(bool enabled, CancellationToken cancel = default)
    {
        if (!enabled || !Due) return false;
        Last = Last with { Checked = _now(), Problem = null };
        try
        {
            using var client = new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1 << 20 };
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            // GitHub refuses requests without a User-Agent; nothing else goes with it.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Tanjelo", null));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            var tag = json.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var latest = tag?.TrimStart('v', 'V');
            if (latest is null || Parse(latest) is null)
                throw new FormatException("GitHub's answer didn't name a version.");
            var page = json.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            Last = Last with { Latest = latest, Page = page };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException
                                   or InvalidOperationException)
        {
            if (cancel.IsCancellationRequested) throw;
            Last = Last with { Problem = ex is TaskCanceledException ? "GitHub didn't answer in time." : ex.Message };
        }
        Save();
        return true;
    }

    public void MarkAnnounced()
    {
        if (Newer is not { } newer) return;
        Last = Last with { Announced = newer };
        Save();
    }

    /// <summary>A line for the settings window, and the page its link opens if there's something to see.</summary>
    public (string Text, string? Link) Describe()
    {
        if (Last.Checked is not { } at) return ("", null);
        var when = at.LocalDateTime.Date == _now().LocalDateTime.Date ? $"today at {at.LocalDateTime:HH:mm}" : $"on {at.LocalDateTime:d MMMM}";
        if (Last.Problem is { } problem) return ($"Couldn't check {when}: {problem}", null);
        if (Newer is { } newer) return ($"Tanjelo {newer} is out (checked {when}).", Page);
        return ($"Tanjelo {Current} is the latest version (checked {when}).", null);
    }

    static Version? Parse(string text) => Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;

    static Status Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<Status>(File.ReadAllText(path), Json) ?? new Status();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return new Status();
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(Last, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"update.json: {ex.Message}");
        }
    }
}

using System.Net.Http;
using System.Text.RegularExpressions;

namespace OnaPlotter.Services;

/// <summary>
/// Polls the static <c>js/version.g.js</c> on the server every
/// <see cref="CheckIntervalMinutes"/> minutes. When the published
/// build hash differs from <see cref="OnaPlotter.BuildInfo.CommitHash"/>
/// (the hash baked into THIS WASM bundle at compile time), flips
/// <see cref="IsUpdateAvailable"/> and fires
/// <see cref="OnChanged"/> so the UI can surface a "Reload to
/// update" chip.
/// <para>
/// Why poll the JS file rather than <c>_framework/blazor.boot.json</c>:
/// version.g.js is tiny (one assignment line, ~80 bytes), regenerated
/// by the StampBuildInfo MSBuild target on every build, and already
/// served as a regular static asset by the SignalK webapp host.
/// blazor.boot.json carries hashes for every WASM file but a fetch
/// + parse on each tick would dwarf the version.g.js fetch in cost.
/// </para>
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>How often to re-fetch <c>version.g.js</c>. 5 minutes
    /// balances "helm gets a Reload prompt within an acceptable
    /// window of a deploy" against "we're not hammering the server
    /// when the helm leaves the page open all weekend". The first
    /// check fires <see cref="FirstCheckSeconds"/> seconds after
    /// startup so a freshly-loaded page that's already on the
    /// latest hash doesn't waste a sync HTTP round-trip during
    /// the warm-up.</summary>
    public const int CheckIntervalMinutes = 5;
    public const int FirstCheckSeconds = 30;

    private readonly HttpClient _http;
    private readonly System.Threading.Timer _timer;
    private readonly string _embeddedHash = OnaPlotter.BuildInfo.CommitHash;
    private bool _disposed;

    /// <summary>Latest hash observed on the server. Null until the
    /// first poll resolves; thereafter the most-recently-fetched
    /// value (UPDATE checks may legitimately roll the hash backward
    /// during a rollback, in which case we still surface a Reload
    /// chip so the helm picks up the rollback as well).</summary>
    public string? LatestHash { get; private set; }

    /// <summary>True when the server's hash differs from the bundle
    /// the helm is currently running. Sticky once flipped: even if a
    /// later poll happens to match again (eg. multi-server load
    /// balancer with stale node), the helm has already SEEN the
    /// new-bundle prompt, so we don't take it back.</summary>
    public bool IsUpdateAvailable { get; private set; }

    /// <summary>Fires whenever <see cref="IsUpdateAvailable"/> or
    /// <see cref="LatestHash"/> changes. UI components subscribe to
    /// re-render the chip.</summary>
    public event Action? OnChanged;

    public UpdateChecker(HttpClient http)
    {
        _http = http;
        _timer = new System.Threading.Timer(
            _ => _ = CheckAsync(),
            state: null,
            dueTime: TimeSpan.FromSeconds(FirstCheckSeconds),
            period: TimeSpan.FromMinutes(CheckIntervalMinutes));
    }

    /// <summary>Manual trigger -- used by tests + the "check now"
    /// path if we ever surface one. Same regex parser as the timer
    /// path; safe to call concurrently (the timer's tick is also
    /// fire-and-forget so a manual call interleaving with the timer
    /// just produces two parallel requests, which is harmless).</summary>
    public async Task CheckAsync()
    {
        if (_disposed) return;
        try
        {
            // Cache-buster query param. Browser caches version.g.js
            // unless told otherwise, and a stale cache would defeat
            // the whole check. ?_=ticks is opaque to the server but
            // fingerprints the URL so the browser refetches.
            var url = $"js/version.g.js?_={DateTime.UtcNow.Ticks}";
            var content = await _http.GetStringAsync(url);
            var match = Regex.Match(content, @"hash:\s*""([^""]+)""");
            if (!match.Success) return;     // malformed payload; try again next tick
            var serverHash = match.Groups[1].Value;
            if (serverHash == LatestHash && LatestHash is not null) return;
            LatestHash = serverHash;
            // Flip the latch only when the embedded hash is known
            // (a "?" / "unknown" build means we can't compare). The
            // StampBuildInfo target falls back to "unknown" only on
            // a CI machine without git; on a real build the hash is
            // always set.
            if (!string.IsNullOrEmpty(_embeddedHash)
                && _embeddedHash != "unknown"
                && serverHash != _embeddedHash)
            {
                IsUpdateAvailable = true;
            }
            OnChanged?.Invoke();
        }
        catch (HttpRequestException) { /* offline / 404; retry next tick */ }
        catch (TaskCanceledException) { /* timeout; retry */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}

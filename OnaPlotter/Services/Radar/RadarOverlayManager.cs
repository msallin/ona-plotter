using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services.Radar;

/// <summary>
/// Owns the radar overlay's session state (the device list, the
/// per-radar capabilities cache, which overlays are currently being
/// painted, the user's sticky-off preferences, and the last range
/// pushed to the JS layer) and the orchestration that ties them
/// together. Pulled out of <c>Map.razor</c> so the policy is
/// testable in isolation -- the page becomes a thin wiring layer
/// that injects this manager and forwards UI callbacks into it.
///
/// Lifecycle: scoped to the page session (the same as Map.razor's
/// own lifetime). Cleared on page reload by design -- session-only
/// preferences (sticky off, last-range cache) are intentional;
/// nothing here belongs in IAppSettings.
///
/// Threading: Blazor WASM is single-threaded; the manager assumes
/// all its public methods run on the renderer's synchronization
/// context, so the mutable fields don't need locks.
/// </summary>
public sealed class RadarOverlayManager
{
    // Wrapped collaborators. Both interfaces so the manager can be
    // constructed in tests with hand-rolled fakes (no IJSRuntime,
    // no real HttpClient).
    private readonly IRadarApi _radarApi;
    private readonly IRadarOverlayHost _host;
    private readonly ISignalKBaseUrl _baseUrl;

    // Session state. Public read-only views so the page (and bUnit)
    // can bind into the layers panel without re-implementing the
    // accessors.
    private List<RadarInfo> _radars = [];
    private readonly Dictionary<string, RadarCapabilities?> _capabilities = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _userDisabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastRange = new(StringComparer.Ordinal);

    public IReadOnlyList<RadarInfo> Radars => _radars;
    public IReadOnlyDictionary<string, RadarCapabilities?> Capabilities => _capabilities;
    public IReadOnlySet<string> EnabledRadarIds => _enabled;

    /// <summary>Optional sink for user-facing error messages (toasts).
    /// Null in tests -- they assert against thrown exceptions or the
    /// captured fake-host calls instead.</summary>
    public Action<string>? OnError { get; set; }

    public RadarOverlayManager(IRadarApi radarApi, IRadarOverlayHost host, ISignalKBaseUrl baseUrl)
    {
        _radarApi = radarApi;
        _host = host;
        _baseUrl = baseUrl;
    }

    /// <summary>Replace the known-radar list (called from the page on
    /// every poll cycle). Folds the three steps that need to follow
    /// in order: capability prefetch, transmit-follows-overlay auto-
    /// toggle, range-diff push to the JS layer. Single entry point
    /// so the init path and the periodic poll can't drift.</summary>
    public async Task OnRadarListUpdatedAsync(IReadOnlyList<RadarInfo> radars, CancellationToken ct = default)
    {
        _radars = [.. radars];
        await EnsureCapabilitiesAsync(ct);
        await ApplyAutoToggleAsync(ct);
        await PushRangeUpdatesAsync(ct);
    }

    /// <summary>User-clicked the layer checkbox. Updates the sticky
    /// preference so the next auto-toggle pass respects the deliberate
    /// choice, then delegates to the same Enable / Disable internals
    /// the auto pass uses so all paths take exactly one code route
    /// into the host.</summary>
    public async Task OnUserToggleAsync(RadarInfo radar, bool enabled, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(radar.Id)) return;
        if (enabled)
        {
            _userDisabled.Remove(radar.Id);
            await EnableAsync(radar, ct);
        }
        else
        {
            _userDisabled.Add(radar.Id);
            await DisableAsync(radar.Id, ct);
        }
    }

    /// <summary>Visible state for tests / page bindings: has the user
    /// explicitly turned this overlay off in this session?</summary>
    public bool IsUserDisabled(string radarId) => _userDisabled.Contains(radarId);

    // --- internals ----------------------------------------------------

    /// <summary>Open the overlay canvas + spoke WS for one radar.
    /// Idempotent -- a second call when the overlay is already on is
    /// a no-op so both the user-driven and the auto pass can call
    /// freely without checking themselves.</summary>
    private async Task EnableAsync(RadarInfo radar, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(radar.Id)) return;
        if (_enabled.Contains(radar.Id)) return;        // already on; idempotent

        // Capabilities are eagerly prefetched on every list refresh;
        // this just reads what's there. A null cached value means the
        // fetch failed earlier; in that case we fall back to whatever
        // the device-list response gave us for geometry and a null
        // legend (the JS layer ships a Navico-default fallback palette).
        if (!_capabilities.TryGetValue(radar.Id, out var caps))
        {
            caps = await _radarApi.GetCapabilitiesAsync(radar.Id, ct);
            _capabilities[radar.Id] = caps;
        }

        // Clamp server-supplied geometry. The JS layer allocates a
        // (2*maxLen)^2 ImageData backed by the spokes*maxLen LUT, so
        // an unbounded value from a hostile / buggy plugin would
        // crash the tab. RadarOverlayLimits caps to a sane envelope.
        int spokes = RadarOverlayLimits.ClampSpokesPerRevolution(
            caps?.SpokesPerRevolution ?? radar.SpokesPerRevolution);
        int maxLen = RadarOverlayLimits.ClampSpokeLength(
            caps?.MaxSpokeLength ?? radar.MaxSpokeLength);
        int range = radar.Range ?? RadarOverlayLimits.DefaultRangeMetres;

        // Spoke WebSocket URL. Prefer the server-supplied value (spec
        // intent), but only if it points back at our SK origin -- a
        // compromised plugin could otherwise hand us an attacker URL
        // and the browser would dutifully open it (exfil / SSRF).
        // On mismatch or absence, build the canonical SK-proxied URL
        // ourselves (matches freeboard-sk's fallback).
        string url = !string.IsNullOrEmpty(radar.SpokeDataUrl)
                     && SignalKUrls.IsSpokeUrlOnSameOrigin(radar.SpokeDataUrl!, _baseUrl.BaseUrl)
            ? radar.SpokeDataUrl!
            : SignalKUrls.RadarSpokeWs(_baseUrl.BaseUrl, radar.Id).ToString();

        var cfg = new RadarOverlayStartConfig(
            RadarId: radar.Id,
            SpokeDataUrl: url,
            SpokesPerRevolution: spokes,
            MaxSpokeLength: maxLen,
            Range: range,
            Legend: caps?.Legend,
            Opacity: 0.75);

        try
        {
            await _host.StartOverlayAsync(cfg, ct);
            _enabled.Add(radar.Id);
            // Seed the range cache with what we just sent so the
            // next PushRangeUpdates pass doesn't re-send the same
            // range to the JS layer (it already has it via cfg).
            _lastRange[radar.Id] = range;
        }
        catch (RadarOverlayException ex)
        {
            OnError?.Invoke($"Radar {radar.Name}: overlay failed ({ex.Message})");
        }
    }

    /// <summary>Tear down the overlay for one radar. Idempotent.</summary>
    private async Task DisableAsync(string radarId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(radarId)) return;
        if (!_enabled.Contains(radarId)) return;        // already off; idempotent
        await _host.StopOverlayAsync(radarId, ct);
        _enabled.Remove(radarId);
    }

    /// <summary>Fetch and cache capabilities for every currently-known
    /// radar we don't have caps for yet. Idempotent: previously-fetched
    /// ids are skipped (spec guarantees capabilities are static per
    /// radar). Failures are stored as null so we don't re-request on
    /// every poll -- we re-try on the next add of the same id via the
    /// enable path.</summary>
    private async Task EnsureCapabilitiesAsync(CancellationToken ct)
    {
        foreach (var r in _radars)
        {
            if (string.IsNullOrEmpty(r.Id)) continue;
            if (_capabilities.ContainsKey(r.Id)) continue;
            try
            {
                var caps = await _radarApi.GetCapabilitiesAsync(r.Id, ct);
                _capabilities[r.Id] = caps;
            }
            catch (Exception) { _capabilities[r.Id] = null; }
        }
    }

    /// <summary>Turn the overlay on for any radar that just started
    /// transmitting (unless the user has stickily disabled it), and
    /// tear down the overlay for any radar that's no longer
    /// transmitting (the empty canvas is just noise and the WebSocket
    /// would reconnect-loop forever). The decision rule lives in
    /// <see cref="RadarOverlayAutoToggle"/> so the policy stays
    /// independently unit-testable.</summary>
    private async Task ApplyAutoToggleAsync(CancellationToken ct)
    {
        foreach (var r in _radars)
        {
            if (string.IsNullOrEmpty(r.Id)) continue;
            var action = RadarOverlayAutoToggle.Decide(
                status: r.Status,
                overlayOn: _enabled.Contains(r.Id),
                userDisabled: _userDisabled.Contains(r.Id));
            switch (action)
            {
                case RadarOverlayAction.Enable: await EnableAsync(r, ct); break;
                case RadarOverlayAction.Disable: await DisableAsync(r.Id, ct); break;
            }
        }
    }

    /// <summary>Forward range changes for any active overlay. Skips
    /// radars whose range is unchanged since the last push -- the JS
    /// layer recomputes its bounds + reposition on every range update
    /// and we don't want to thrash that on every poll.</summary>
    private async Task PushRangeUpdatesAsync(CancellationToken ct)
    {
        foreach (var r in _radars)
        {
            if (string.IsNullOrEmpty(r.Id) || r.Range is not int range) continue;
            if (!_enabled.Contains(r.Id)) continue;        // overlay off; nothing to update
            if (_lastRange.TryGetValue(r.Id, out var prev) && prev == range) continue;
            _lastRange[r.Id] = range;
            await _host.UpdateRangeAsync(r.Id, range, ct);
        }
    }
}

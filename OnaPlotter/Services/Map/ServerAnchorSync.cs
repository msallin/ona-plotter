using OnaPlotter.Models;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Drives the on-map server-anchor visualisation in response to
/// <c>NavigationData</c> deltas. Owns the diff state needed to keep
/// JS interop quiet when nothing meaningful changed (anchor not
/// drawn yet, radius unchanged, watchdog not yet armed) and runs the
/// raise-confirmation watchdog.
///
/// Lifecycle: instantiated by Map.razor in <c>OnAfterRenderAsync</c>
/// once the JS module reference is available; per-tick syncs come
/// from <c>HandleDataChanged</c>; raised manually via
/// <see cref="MarkRaisePending"/> when the helm taps the Anchor
/// button so the watchdog window opens at the same moment as the
/// REST round-trip. Disposal is implicit -- the wrapped
/// <see cref="IMapAnchorJs"/> is marked disposed by the page, after
/// which interop calls become silent no-ops.
///
/// Threading: Blazor WASM is single-threaded; the controller assumes
/// every public call runs on the renderer's synchronisation context,
/// so the mutable fields don't need locks.
/// </summary>
public sealed class ServerAnchorSync
{
    private readonly IMapAnchorJs _anchorJs;
    private readonly Func<bool> _isSignalKConnected;
    private readonly Action<string> _raiseTimeoutWarning;
    private readonly Func<DateTime> _utcNow;

    /// <summary>How long to wait for a server-confirmation delta after
    /// the helm tapped raise before surfacing a "didn't confirm" warning
    /// and un-dimming the on-map marker. Matches the value the page
    /// used before the extraction.</summary>
    private const int RaiseTimeoutSec = 5;

    // Diff state.
    private bool _serverAnchorDrawn;
    private double _lastPushedRadius;
    private DateTime? _raisePendingUtc;
    /// <summary>Tracks the last-pushed "incomplete" state (pin set
    /// but radius not yet armed). Diffed against the live state on
    /// each tick so we only push the JS toggle when it actually
    /// changes -- the JS-side `setAnchorIncomplete` is idempotent
    /// but free is free.</summary>
    private bool _incompletePushed;

    /// <summary>Visible state for the page: read-only view of whether the
    /// last sync left a server-anchor visualisation on the map. Used by
    /// the page's <c>ToggleAnchor</c> flow to decide whether the next
    /// tap should raise via REST (server-driven anchor) or clear a
    /// manual drop.</summary>
    public bool ServerAnchorDrawn => _serverAnchorDrawn;

    /// <summary>Optional callback invoked when a server anchor first
    /// appears -- the page uses it to clear an in-progress manual
    /// anchor drop so the helm doesn't see two overlapping rings. Null
    /// in tests that don't care.</summary>
    public Func<Task>? OnServerAnchorAppeared { get; set; }

    public ServerAnchorSync(
        IMapAnchorJs anchorJs,
        Func<bool> isSignalKConnected,
        Action<string> raiseTimeoutWarning,
        Func<DateTime>? utcNow = null)
    {
        _anchorJs = anchorJs ?? throw new ArgumentNullException(nameof(anchorJs));
        _isSignalKConnected = isSignalKConnected ?? throw new ArgumentNullException(nameof(isSignalKConnected));
        _raiseTimeoutWarning = raiseTimeoutWarning ?? throw new ArgumentNullException(nameof(raiseTimeoutWarning));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Arms the raise-confirmation watchdog. Called from the page's
    /// raise path BEFORE <c>AnchorAlarmApi.RaiseAsync</c> awaits so the
    /// helm sees the visual "raising" feedback during the in-flight
    /// PUT (HttpClient.Timeout is 8 s; arming after the response left
    /// the helm with no signal during the wait). The next sync tick
    /// that still sees <c>Data.AnchorActive</c> after
    /// <see cref="RaiseTimeoutSec"/> fires the warning callback.
    /// </summary>
    public void MarkRaisePending() => _raisePendingUtc = _utcNow();

    /// <summary>
    /// Cancels a previously-armed raise watchdog. Called when the PUT
    /// itself fails (network error, 4xx) so the watchdog doesn't fire
    /// a misleading "didn't confirm" warning on top of the failure
    /// toast the page already surfaced.
    /// </summary>
    public void CancelRaisePending() => _raisePendingUtc = null;

    /// <summary>
    /// Per-tick sync: draws the server anchor when it first appears,
    /// pushes radius updates, runs the raise-confirmation watchdog,
    /// and tears the visualisation down when the server clears the
    /// anchor.
    /// </summary>
    public async Task SyncAsync(NavigationData data)
    {
        if (data is null) return;

        if (data.AnchorActive)
        {
            // v2.0.0+ two-step intermediate state: position is pinned
            // (AnchorActive=true) but the helm hasn't set MaxRadius
            // yet (or it cleared in transit). Use a placeholder
            // radius for rendering, but flag the JS layer to render
            // the pin in "incomplete" amber-pulse styling so the
            // helm sees the half-armed state on the chart.
            bool incomplete = data.AnchorMaxRadius is null;
            double radius = data.AnchorMaxRadius ?? 30;
            if (!_serverAnchorDrawn)
            {
                // Server anchor just appeared -- clear any in-progress
                // manual drop first so the helm doesn't see two
                // overlapping rings, then draw the server one. The
                // manual-clear callback is page-side because the manual
                // anchor's rendering is also there (a page field
                // captured at toggle-on time).
                if (OnServerAnchorAppeared is not null)
                {
                    await OnServerAnchorAppeared();
                }
                await _anchorJs.SetAnchorAsync(
                    data.AnchorLatitude ?? 0,
                    data.AnchorLongitude ?? 0,
                    radius);
                _serverAnchorDrawn = true;
                _lastPushedRadius = radius;
            }
            else if (Math.Abs(radius - _lastPushedRadius) > 0.1)
            {
                // Only update JS if radius actually changed -- the JS
                // layer recomputes the watch ring on every update and
                // we don't want to thrash that on every tick.
                await _anchorJs.UpdateAnchorRadiusAsync(radius);
                _lastPushedRadius = radius;
            }

            // Diff the incomplete state separately. The JS toggle
            // is idempotent but the per-tick churn is wasteful, and
            // diffing makes the wire log easier to read.
            if (incomplete != _incompletePushed)
            {
                await _anchorJs.SetAnchorIncompleteAsync(incomplete);
                _incompletePushed = incomplete;
            }

            // Watchdog: if the helm tapped raise and Data.AnchorActive
            // is STILL true beyond the timeout, surface a warning and
            // un-dim the marker so the helm investigates rather than
            // staring at a dimmed-indefinitely anchor.
            //
            // Suppressed while the WebSocket is disconnected -- the
            // delta might have been published server-side but the
            // client isn't subscribed to receive it. Re-arm the timer
            // on every disconnected pass so the helm gets a fresh 5 s
            // grace once we reconnect, instead of a false-positive
            // "server didn't confirm" the moment the connection is
            // restored.
            if (_raisePendingUtc is DateTime t)
            {
                if (!_isSignalKConnected())
                {
                    _raisePendingUtc = _utcNow();        // re-arm
                }
                else if ((_utcNow() - t).TotalSeconds > RaiseTimeoutSec)
                {
                    _raisePendingUtc = null;
                    _raiseTimeoutWarning("Anchor raise not confirmed by server -- still anchored");
                    await _anchorJs.SetAnchorRaisingAsync(false);
                }
            }
        }
        else if (_serverAnchorDrawn)
        {
            // Server anchor was raised -- clear from the map. Also clear
            // any "raising in progress" pending flag (the marker is
            // going away anyway, but the flag should follow truth).
            await _anchorJs.ClearAnchorAsync();
            _serverAnchorDrawn = false;
            _raisePendingUtc = null;
            _incompletePushed = false;
        }
    }
}

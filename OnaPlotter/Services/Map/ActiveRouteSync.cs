using System.Net.Http;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Js;
using OnaPlotter.Utilities;

namespace OnaPlotter.Services.Map;

/// <summary>
/// Drives the on-map active-route visualisation in response to course
/// changes from the SignalK delta stream. Owns:
/// <list type="bullet">
/// <item><description>The href / next-WP / pointIndex diff state used
/// to keep the JS interop quiet when nothing material has changed.</description></item>
/// <item><description>The cached route geometry + total distance so a
/// leg advance redraws without a fresh HTTP fetch.</description></item>
/// <item><description>The "regular polyline suppression" id used to
/// avoid double-stroking the active route's saved polyline beneath the
/// active overlay.</description></item>
/// <item><description>The Stop-Navigation watchdog: warns when
/// <c>CourseApi.ClearAsync</c> returned success but the SK delta hasn't
/// cleared the route within the timeout, so the helm doesn't sit
/// staring at an indefinitely-dimmed polyline.</description></item>
/// </list>
///
/// Lifecycle: the page constructs once the JS module is loaded and
/// forwards <see cref="SyncAsync"/> from <c>HandleDataChanged</c>.
/// Disposal is implicit -- the wrapped <see cref="IMapRouteJs"/> is
/// marked disposed by the page, after which interop becomes a silent
/// no-op.
///
/// Threading: Blazor WASM is single-threaded; all public methods run
/// on the renderer's synchronisation context, so the mutable fields
/// don't need locks.
/// </summary>
public sealed class ActiveRouteSync
{
    private readonly IMapRouteJs _routeJs;
    private readonly IMapControlsJs _controlsJs;
    private readonly IRouteApi _routeApi;
    private readonly Func<bool> _isSignalKConnected;
    private readonly Action<string> _stopTimeoutWarning;
    private readonly Func<DateTime> _utcNow;

    /// <summary>How long to wait for a server-confirmation delta after
    /// the helm tapped Stop Navigation before surfacing a "didn't
    /// confirm" warning and restoring the un-dimmed polyline. Matches
    /// the value the page used before the extraction.</summary>
    private const int StopTimeoutSec = 5;

    // Diff state.
    private string? _lastActiveRouteHref;
    private double? _lastNextWpLat;
    private double? _lastNextWpLon;
    private int? _lastActiveRoutePointIndex;
    private double[][]? _activeRouteCoords;
    private double? _activeRouteDistanceTotal;
    private string? _activeRouteRegularSuppressed;
    private DateTime? _courseStopPendingUtc;

    /// <summary>Visible state for the page: id of the route whose REGULAR
    /// polyline (from <c>addRoute</c>) is currently hidden because it's
    /// also the active route. Null when no suppression is in effect.
    /// Read by the page's route-edit panel binding (an in-place edit of
    /// the active route is detected by id-match).</summary>
    public string? ActiveRouteRegularSuppressed => _activeRouteRegularSuppressed;

    /// <summary>Total leg-by-leg distance (metres) of the active route's
    /// geometry, computed once when the geometry lands. Read by the HUD
    /// card so it can show "passed / total nm" instead of just the SK
    /// course payload's remaining-only value. Null when no active route
    /// or before the fetch completes.</summary>
    public double? ActiveRouteDistanceTotal => _activeRouteDistanceTotal;

    /// <summary>True while a Stop Navigation request is in flight (helm
    /// tapped Stop, REST returned success, SK delta hasn't yet cleared
    /// the active route). Drives the page's "stopping" CSS class so the
    /// HUD chip dims while we wait.</summary>
    public bool CourseStopPending => _courseStopPendingUtc is not null;

    /// <summary>Optional callback to redraw a previously-suppressed
    /// regular route polyline when the active route changes. Invoked
    /// with the suppressed route id; the page side calls
    /// <c>AddRouteToMap(route)</c> if the id is currently enabled.
    /// Null in tests that don't care.</summary>
    public Func<string, Task>? OnRestoreSuppressedRoute { get; set; }

    /// <summary>Optional callback fired when the active overlay is
    /// cleared (route deactivated, switched, or forced refresh). The
    /// page uses it to clear the per-tick frame builder's
    /// <c>CourseLineDrawn</c> flag so the next frame redraws the
    /// course line from scratch.</summary>
    public Action? OnActiveOverlayCleared { get; set; }

    public ActiveRouteSync(
        IMapRouteJs routeJs,
        IMapControlsJs controlsJs,
        IRouteApi routeApi,
        Func<bool> isSignalKConnected,
        Action<string> stopTimeoutWarning,
        Func<DateTime>? utcNow = null)
    {
        _routeJs = routeJs ?? throw new ArgumentNullException(nameof(routeJs));
        _controlsJs = controlsJs ?? throw new ArgumentNullException(nameof(controlsJs));
        _routeApi = routeApi ?? throw new ArgumentNullException(nameof(routeApi));
        _isSignalKConnected = isSignalKConnected ?? throw new ArgumentNullException(nameof(isSignalKConnected));
        _stopTimeoutWarning = stopTimeoutWarning ?? throw new ArgumentNullException(nameof(stopTimeoutWarning));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Arms the Stop Navigation watchdog. Called from the page's Stop
    /// path after <c>CourseApi.ClearAsync</c> returns success; the next
    /// sync tick that still sees a non-null active route after
    /// <see cref="StopTimeoutSec"/> fires the warning callback.
    /// </summary>
    public void MarkCourseStopPending() => _courseStopPendingUtc = _utcNow();

    /// <summary>
    /// Wipes the cached href so the next <see cref="SyncAsync"/>
    /// refetches and redraws even when the SK href is unchanged. Used
    /// after a deliberate "force a redraw now" event -- e.g. just
    /// activated a route via the popup and want the polyline to
    /// appear before the next delta tick.
    /// </summary>
    public void InvalidateActiveRouteHref() => _lastActiveRouteHref = null;

    /// <summary>
    /// Per-tick sync: detects href / next-WP / pointIndex diffs against
    /// the last-pushed state, fetches geometry on activation, redraws
    /// on leg advance, suppresses the saved-route polyline duplicate,
    /// pushes the live route ETA, and runs the stop-confirmation
    /// watchdog.
    /// </summary>
    /// <param name="force">When true, refetches geometry even if href
    /// is unchanged. Used after an in-place edit of an already-active
    /// route, where the href stays the same but the geometry doesn't.</param>
    public async Task SyncAsync(
        NavigationData data,
        IReadOnlySet<string> enabledRoutes,
        IReadOnlyList<SignalkRoute> availableRoutes,
        bool force = false)
    {
        if (data is null) return;

        var currentHref = data.ActiveRouteHref;
        var nextLat = data.CourseNextPointLatitude;
        var nextLon = data.CourseNextPointLongitude;
        var currentPointIndex = data.ActiveRoutePointIndex;

        // Watchdog for a pending Stop Navigation: if the helm tapped
        // Stop and CourseApi.ClearAsync returned success but the SK
        // delta hasn't cleared the active route within the timeout,
        // surface a warning rather than leaving the polyline dimmed
        // forever. Restore the un-dimmed style so the helm can see the
        // route is genuinely still active server-side.
        //
        // Suppressed while the WebSocket is disconnected: the delta
        // might exist server-side but the client isn't listening, so
        // a "server didn't confirm" warning would be misleading. Re-
        // arm the timer on every disconnected pass so the helm gets a
        // fresh 5 s grace once we reconnect.
        if (_courseStopPendingUtc is DateTime stopT && currentHref is not null)
        {
            if (!_isSignalKConnected())
            {
                _courseStopPendingUtc = _utcNow();        // re-arm
            }
            else if ((_utcNow() - stopT).TotalSeconds > StopTimeoutSec)
            {
                _courseStopPendingUtc = null;
                _stopTimeoutWarning("Stop navigation not confirmed by server -- still navigating");
                await _routeJs.SetActiveRouteStoppingAsync(false);
            }
        }
        // If the delta cleared the route, the pending flag should
        // follow truth -- the dimming is moot once the polyline is
        // gone, but the flag drives the watchdog above.
        if (currentHref is null) _courseStopPendingUtc = null;

        bool hrefChanged = currentHref != _lastActiveRouteHref;
        bool nextWpChanged = nextLat != _lastNextWpLat || nextLon != _lastNextWpLon;
        // pointIndex change re-triggers a redraw on its own. Important
        // on page reload: pointIndex tends to land in the connect-time
        // delta snapshot before nextPoint.position, and we want the
        // "passed legs" portion of the polyline to dim straight away
        // -- not wait for the next-point coordinates to arrive.
        bool pointIndexChanged = currentPointIndex != _lastActiveRoutePointIndex;
        if (!force && !hrefChanged && !nextWpChanged && !pointIndexChanged) return;

        if (hrefChanged || force)
        {
            // Route changed, deactivated, or forced refresh: clear
            // previous. For a forced refresh the href stayed the same
            // but the geometry didn't, so we still need to wipe the old
            // polyline.
            if (_lastActiveRouteHref is not null || force)
            {
                await _routeJs.ClearActiveRouteAsync();
                await _routeJs.ClearCourseLineAsync();
                _activeRouteCoords = null;
                OnActiveOverlayCleared?.Invoke();
            }

            _lastActiveRouteHref = currentHref;

            // Hide the bottom-centre range-scale chip while a route
            // is active; the route HUD card occupies the same vertical
            // slot. Restored when the route deactivates (currentHref
            // empty / null).
            await _controlsJs.SetRangeScaleHiddenAsync(!string.IsNullOrEmpty(currentHref));

            // The active overlay drawn by setActiveRoute would
            // otherwise sit on top of the regular polyline (drawn by
            // addRoute) for the same route id, producing a visible
            // double-stroke that confuses progress-vs-plan reading.
            // Hide the regular polyline of the new active route, and
            // restore the previous one if we suppressed it.
            string? newActiveId = string.IsNullOrEmpty(currentHref)
                ? null
                : SignalKUrls.ExtractRouteId(currentHref);
            if (_activeRouteRegularSuppressed is string prev
                && prev != newActiveId
                && enabledRoutes.Contains(prev))
            {
                if (OnRestoreSuppressedRoute is not null)
                {
                    try { await OnRestoreSuppressedRoute(prev); }
                    catch { /* layer may already exist; harmless */ }
                }
                _activeRouteRegularSuppressed = null;
            }
            if (!string.IsNullOrEmpty(newActiveId)
                && _activeRouteRegularSuppressed != newActiveId)
            {
                if (enabledRoutes.Contains(newActiveId))
                {
                    await _routeJs.RemoveRouteAsync(newActiveId);
                }
                _activeRouteRegularSuppressed = newActiveId;
            }

            // Route activated: fetch geometry once. The HTTP round-trip
            // is where dispose races most often -- by the time the fetch
            // completes the page may have navigated away. The wrapped
            // IMapRouteJs treats post-dispose calls as no-ops so
            // SetActiveRouteAsync below can safely run even on
            // teardown.
            if (!string.IsNullOrEmpty(currentHref))
            {
                // Catch transport failures locally so a slow / unreachable
                // SK server (8 s HttpClient timeout -> TaskCanceledException,
                // or HttpRequestException on connection refused) doesn't
                // bubble up to HandleDataChanged's async-void boundary --
                // an unhandled exception there terminates the WASM runtime
                // (".NET runtime already exited with 1") and the helm has
                // to reload. We log to console so the relay still surfaces
                // the failure, leave _activeRouteCoords unchanged so the
                // last-known geometry stays drawn, and let the next tick's
                // diff (href hasn't changed -> early-exit at line 188)
                // skip the refetch until something actually moves. A
                // genuine deactivation (currentHref -> null) will retry
                // and clear correctly.
                try
                {
                    _activeRouteCoords = await _routeApi.GetCoordinatesAsync(currentHref);
                    _activeRouteDistanceTotal = _activeRouteCoords is not null
                        ? RouteProgress.TotalDistanceMeters(_activeRouteCoords)
                        : null;
                }
                catch (Exception ex)
                    when (ex is TaskCanceledException or HttpRequestException or OperationCanceledException)
                {
                    // Console.WriteLine (not Console.Error) so a
                    // transient WS / HTTP outage doesn't surface as
                    // an unhandled error via the relay -- the catch
                    // here already preserves the previously-fetched
                    // geometry.
                    Console.WriteLine(
                        $"[active-route] geometry fetch failed: {ex.GetType().Name}: {ex.Message}");
                    // Leave _activeRouteCoords as-is: a previously-fetched
                    // route stays drawn through the transient outage,
                    // matching the helm's mental model ("the route I
                    // activated is still there").
                }
            }
            else
            {
                _activeRouteDistanceTotal = null;
            }
        }

        // Redraw whenever we have the geometry and at least one
        // signal that tells us which leg we're on. Covers the initial
        // draw (hrefChanged or force), the leg-advance case
        // (nextWpChanged or pointIndexChanged on an unchanged route),
        // and the post-edit refetch (force). The leg index is resolved
        // here (server's pointIndex preferred, lat/lon fallback) so the
        // JS side stays a thin renderer; precedence is unit-tested in
        // RouteProgressTests.
        if (_activeRouteCoords is not null && _activeRouteCoords.Length > 0)
        {
            int? legIdx = RouteProgress.ResolveLegIndex(
                currentPointIndex, _activeRouteCoords, nextLat, nextLon);
            if (legIdx is int idx)
            {
                string routeId = string.IsNullOrEmpty(currentHref)
                    ? string.Empty
                    : SignalKUrls.ExtractRouteId(currentHref);
                // Pass the route name through so the active-route popup
                // (Deactivate / Edit / Delete) can show it as a title.
                // Falls back to Data.ActiveRouteName which the SK delta
                // sets, then to availableRoutes if the name hasn't
                // propagated yet, so the popup never reads "Route abc-12"
                // for a route the helm has named.
                string routeName = data.ActiveRouteName
                    ?? availableRoutes.FirstOrDefault(r => r.Id == routeId)?.Name
                    ?? string.Empty;
                await _routeJs.SetActiveRouteAsync(_activeRouteCoords, idx, routeId, routeName);
            }
        }

        // Push the live route ETA to JS so the active-route popup
        // shows a fresh "ETA HH:MM (in Xh Ym)" each time the helm
        // taps the polyline. Sent every sync tick (cheap one-arg
        // call) so the cached value never lags more than the
        // NavigationData refresh cadence. Null clears the row.
        await _routeJs.SetActiveRouteTtgSecondsAsync(data.ActiveRouteTimeToGo);

        _lastNextWpLat = nextLat;
        _lastNextWpLon = nextLon;
        _lastActiveRoutePointIndex = currentPointIndex;
    }
}

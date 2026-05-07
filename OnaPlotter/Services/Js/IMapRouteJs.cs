namespace OnaPlotter.Services.Js;

/// <summary>
/// Typed C# wrapper over the leafletInterop.js route + active-route
/// surface. Covers the saved-route polylines (add / remove) and the
/// active-course overlay (set / clear / dim-while-stopping / hide
/// during edit / live ETA push). Same JSDisconnected / ObjectDisposed
/// swallow as the other Map* JS wrappers so call sites stop
/// re-implementing the dance.
/// </summary>
public interface IMapRouteJs
{
    /// <summary>Draw a saved route as a polyline. Coords are
    /// Leaflet-ordered <c>[lat, lon]</c> pairs.
    /// <paramref name="totalNm"/> is the precomputed sum-of-haversines
    /// distance in nautical miles, used by the popup's
    /// "<c>N WP &middot; X.X nm</c>" line. Computed C#-side via
    /// <c>RouteProgress.TotalDistanceMeters</c> so the JS render path
    /// doesn't have to re-derive it.</summary>
    Task AddRouteAsync(string id, string? name, double[][] coords, double totalNm);

    /// <summary>Update an existing route's geometry / name / total nm
    /// in place. Mirrors REST PUT semantics: same id, possibly different
    /// waypoint count + name. The leaflet polyline + popup update via
    /// <c>setLatLngs</c> / <c>setPopupContent</c>; vertex dots rebuild.
    /// Click handlers + popupopen wiring survive (no flicker, no
    /// active-route diff thrash). Falls through to <c>addRoute</c> when
    /// the id is unknown so callers can use this as upsert without
    /// branching on cache state.</summary>
    Task UpdateRouteAsync(string id, string? name, double[][] coords, double totalNm);

    /// <summary>Remove a previously-added saved-route polyline.</summary>
    Task RemoveRouteAsync(string id);

    /// <summary>Render the active course overlay - the leg polyline +
    /// pulsing next-WP marker. <paramref name="wpIndex"/> is the
    /// 0-based index of the leg currently being navigated; the JS side
    /// dims earlier legs as "passed".</summary>
    Task SetActiveRouteAsync(double[][] coords, int wpIndex, string routeId, string routeName);

    /// <summary>Clear the active course overlay (used on
    /// deactivation, edit-end, page dispose).</summary>
    Task ClearActiveRouteAsync();

    /// <summary>Hide / show the active-route overlay without dropping
    /// state. Used during in-place route edit so the edit polyline
    /// doesn't double-draw with the active overlay.</summary>
    Task SetActiveOverlayHiddenAsync(bool hidden);

    /// <summary>Dim the active-route polyline while a Stop Navigation
    /// request is in flight; restored by passing <c>false</c> when the
    /// SK delta confirms (or the watchdog times out).</summary>
    Task SetActiveRouteStoppingAsync(bool stopping);

    /// <summary>Push the live route ETA in seconds so the active-route
    /// popup shows a fresh "ETA HH:MM (in Xh Ym)". Null clears the
    /// row.</summary>
    Task SetActiveRouteTtgSecondsAsync(double? seconds);

    /// <summary>Clear the next-leg course-line overlay (the bearing /
    /// XTE tick to the next waypoint). Drawn by <c>applyFrame</c>;
    /// cleared explicitly on deactivation / dispose.</summary>
    Task ClearCourseLineAsync();
}

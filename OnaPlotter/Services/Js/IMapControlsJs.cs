using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// Typed C# wrapper over the leafletInterop.js "imperative map state"
/// surface. Covers the global map flags (follow / orientation / night
/// mode / SK base URL), the camera moves (panTo / fitBounds /
/// zoomToTrack), the keyboard-shortcut bridge, the laylines + guard
/// zone push, the per-tick frame channel, and the MOB pin. These are
/// the calls a helm wires from a button or settings flip; they mutate
/// global map state rather than draw a specific resource (those live
/// on <see cref="IMapResourceJs"/>) or a specific layer
/// (<see cref="IMapOverlaysJs"/>).
/// </summary>
public interface IMapControlsJs
{
    /// <summary>Tell the JS side where the SignalK server lives so its
    /// origin-bound URLs (flag images, future direct hits) target the
    /// configured server rather than the page origin.</summary>
    Task SetSignalKBaseUrlAsync(string url);

    /// <summary>Set the map orientation: <c>"north"</c> / <c>"course"</c>
    /// / <c>"head"</c>. North-up is the default; the other two rotate
    /// the chart so the boat icon stays pointing up.</summary>
    Task SetMapOrientationAsync(string mode);

    /// <summary>Toggle follow-boat mode. When on, every position
    /// update re-centres the chart on own boat.</summary>
    Task SetFollowAsync(bool follow);

    /// <summary>Drop the laylines polylines (close-hauled tack
    /// projections). Push happens automatically on every applyFrame
    /// when laylines are enabled, so there's no setLaylines wrapper
    /// here - only the explicit clear on toggle-off / dispose.</summary>
    Task ClearLaylinesAsync();

    /// <summary>Master gate for own-ship informational lines (COG
    /// vector + tip + label, tidal current arrow, laylines). Set
    /// false to declutter the chart; the JS side tears the existing
    /// lines down immediately so the helm sees the change without
    /// waiting for the next frame.</summary>
    Task SetShipLinesVisibleAsync(bool enabled);

    /// <summary>Push the range-rings config (enabled + ring count)
    /// to every active radar overlay. Rings are concentric circles
    /// on own boat at evenly-spaced fractions of the radar's current
    /// range, helping the helm read "how far is the sweep showing
    /// me" at a glance. The JS side refreshes each overlay
    /// internally so the rings appear / disappear / re-count on the
    /// next render tick.</summary>
    Task SetRadarRangeRingsAsync(bool enabled, int count);

    /// <summary>Push the helm's "Trust wire bearing" opt-in to every
    /// active radar overlay so a live toggle takes effect on the next
    /// sweep without needing the helm to disable + re-enable the
    /// overlay. See <see cref="IMapDisplaySettings.RadarUseWireBearing"/>
    /// for what the flag does.</summary>
    Task SetRadarUseWireBearingAsync(bool value);

    /// <summary>Push the helm's bearing-correction trim (degrees) to
    /// every active radar overlay so the picture re-rotates on the
    /// next sweep. Lets the helm tune visually against the chart with
    /// immediate feedback. See
    /// <see cref="IMapDisplaySettings.RadarBearingCorrectionDeg"/>
    /// for the use case.</summary>
    Task SetRadarBearingCorrectionAsync(double degrees);

    /// <summary>Apply night-mode CSS filter to map tiles + UI chrome.</summary>
    Task SetNightModeAsync(bool enabled);

    /// <summary>Configure the CPA guard zone ring around own boat.
    /// Radius is in nautical miles (the inner red ring), lookahead in
    /// minutes, outerRingMultiplier is the band scale for the amber
    /// outer ring (typically 2.0 - sourced from
    /// <c>Cpa.OuterRingMultiplier</c> so the C# threat classifier and
    /// the JS rendering can't drift on the constant).</summary>
    Task SetGuardZoneAsync(double radiusNm, double lookaheadMin, double outerRingMultiplier);

    /// <summary>Configure how far ahead the COG vectors project, in
    /// minutes. Two distinct values so the helm can shorten target
    /// vectors in busy harbours without losing their own predictor's
    /// reach. Both default to 10 min on first run.</summary>
    Task SetCogVectorMinutesAsync(double ownMinutes, double aisMinutes);

    /// <summary>One-shot pan to the given lat/lon at the current zoom
    /// level. Distinct from <see cref="SetFollowAsync"/>, which
    /// continuously re-centres.</summary>
    Task PanToAsync(double lat, double lon);

    /// <summary>Zoom + pan to the bounding box of the local own-track
    /// polyline. Bound to the keyboard-shortcut "t".</summary>
    Task ZoomToTrackAsync();

    /// <summary>Pan + zoom to fit the given bounding box. Used by the
    /// FocusRoute deep link.</summary>
    Task FitBoundsAsync(double minLat, double minLon, double maxLat, double maxLon);

    /// <summary>Wire keyboard shortcuts to the given .NET reference so
    /// JS-side keydown handlers can call back into <c>OnKeyShortcut</c>.
    /// Generic on the call site's component type so this Services
    /// namespace doesn't need to know about <c>Map</c>.</summary>
    Task EnableKeyboardShortcutsAsync<T>(DotNetObjectReference<T> dotNetRef) where T : class;

    /// <summary>Unhook the keyboard-shortcut handlers (called on
    /// dispose).</summary>
    Task DisableKeyboardShortcutsAsync();

    /// <summary>Push a per-tick frame snapshot (position, track
    /// segment, course line, current arrow, laylines). One bridge
    /// crossing per tick instead of N individual interop calls.</summary>
    Task ApplyFrameAsync(object frame);

    /// <summary>Drop the current-arrow overlay (tide / set+drift
    /// indicator). Cleared explicitly on dispose; the per-tick draw
    /// rides on <see cref="ApplyFrameAsync"/>.</summary>
    Task ClearCurrentArrowAsync();

    /// <summary>Returns the current map centre as <c>[lat, lon]</c>.
    /// Null when the JS side has torn down or when the JS map ref
    /// hasn't initialised yet; callers fall back to the previous
    /// target. Used by the FAB menu's "Create at map centre" actions
    /// to seed the same target fields the long-press context-menu
    /// handlers already write.</summary>
    Task<double[]?> GetMapCenterAsync();

    /// <summary>Hide / show the bottom-centre range-scale chip. The
    /// chip occupies the same vertical column as the active-route
    /// HUD card; we hide the chip while a route is active so they
    /// don't overlap. Called from ActiveRouteSync on activate /
    /// deactivate.</summary>
    Task SetRangeScaleHiddenAsync(bool hidden);
}

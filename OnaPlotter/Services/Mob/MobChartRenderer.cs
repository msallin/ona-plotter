using OnaPlotter.Services.Js;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Services.Mob;

/// <summary>
/// Drives the MOB chart marker (mobLayer.js) from
/// <see cref="ServerNotificationStore"/> changes. Subscribes to the
/// store's <see cref="ServerNotificationStore.OnPathChanged"/> event
/// and translates each <c>notifications.mob.*</c> mutation to a
/// <c>setMob</c> / <c>clearMob</c> JS call so the chart visual
/// stays in lockstep with the alarm pipeline -- no separate
/// "is mobActive" boolean to drift out of sync.
///
/// <para>v1 limit: the JS <c>mobLayer</c> single-marker
/// implementation only paints the FIRST active MOB. When a second
/// MOB lands while another is already drawn, the second one's
/// banner still fires (alarm pipeline is multi-armed) but the
/// marker stays on the first. Multi-marker support is a follow-on;
/// most practical scenarios are single-casualty.</para>
/// </summary>
public sealed class MobChartRenderer : IDisposable
{
    internal const string MobPathPrefix = "notifications.mob.";

    private readonly ServerNotificationStore _store;

    /// <summary>Provider for the helm vessel's MMSI -- shown in
    /// the MOB marker's popup so the helm can read MMSI + position
    /// onto the VHF mic without leaving the chart. Optional: tests
    /// pass null and the popup just omits the MMSI row. Production
    /// wiring uses SignalkClient.OwnMmsi via a thunk so the renderer
    /// stays out of the SignalkClient dependency tree.</summary>
    private readonly Func<string?>? _ownMmsi;

    /// <summary>JS bridge -- nullable because the renderer is
    /// constructed at app start while the JS module reference only
    /// becomes available after the Map page mounts. The map page
    /// hands it over via <see cref="AttachJs"/>; before that, the
    /// renderer just buffers state and pushes nothing.</summary>
    private IMapControlsJs? _js;

    /// <summary>Path of the MOB whose marker is currently rendered
    /// on the chart, or null when no marker is up. Stored so the
    /// renderer can avoid redundant <c>setMob</c> calls when a
    /// later mutation on the same path doesn't change the
    /// position.</summary>
    private string? _renderedPath;
    private double? _renderedLat;
    private double? _renderedLon;
    private DateTime? _renderedCreatedAt;

    public MobChartRenderer(ServerNotificationStore store, Func<string?>? ownMmsi = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ownMmsi = ownMmsi;
        _store.OnPathChanged += HandlePathChanged;
    }

    /// <summary>Called from Map.razor's OnAfterRenderAsync once the
    /// JS module reference is available. Idempotent: a second call
    /// just swaps the bridge handle. Re-renders the current state
    /// so a MOB raised before the page mounted shows up on the
    /// chart immediately.</summary>
    public void AttachJs(IMapControlsJs js)
    {
        _js = js ?? throw new ArgumentNullException(nameof(js));
        Console.WriteLine("[mob-renderer] AttachJs called -- invalidating dedup + resyncing");
        // Helm regression: navigating Chart -> Dashboard -> Chart
        // returned to a chart with no MOB marker even though the
        // store still had the MOB. Cause was the dedup state below
        // (_renderedPath / _renderedLat / etc) surviving across
        // pages -- the new JS bridge got "same MOB, no change,
        // nothing to do" and the marker never painted on the
        // freshly-mounted Leaflet container. Invalidate the dedup
        // cache before resyncing so the new bridge always gets a
        // fresh setMob.
        _renderedPath = null;
        _renderedLat = null;
        _renderedLon = null;
        _renderedCreatedAt = null;
        // Recompute -- if a MOB landed while the JS handle was
        // null, this paints it now. The synthesised store entry
        // already has the lat/lon.
        ResyncRender();
    }

    /// <summary>Forget the JS handle (page unmount). Keeps store
    /// subscription so the next AttachJs picks up the latest
    /// state.</summary>
    public void DetachJs()
    {
        Console.WriteLine("[mob-renderer] DetachJs called");
        _js = null;
        _renderedPath = null;
    }

    private void HandlePathChanged(string path)
    {
        if (!path.StartsWith(MobPathPrefix, StringComparison.Ordinal)) return;
        Console.WriteLine($"[mob-renderer] HandlePathChanged path={path} _js={(_js is null ? "NULL" : "set")}");
        ResyncRender();
    }

    private void ResyncRender()
    {
        if (_js is null)
        {
            Console.WriteLine("[mob-renderer] ResyncRender: _js NULL, skip");
            return;
        }
        // Pick the first active MOB. For v1 we render at most one
        // marker; the alarm banner stack still surfaces every
        // active MOB independently.
        ServerNotification? first = null;
        foreach (var n in _store.Active)
        {
            if (n.Path.StartsWith(MobPathPrefix, StringComparison.Ordinal))
            {
                first = n;
                break;
            }
        }
        if (first is null)
        {
            Console.WriteLine("[mob-renderer] ResyncRender: no MOB in store");
            // No active MOB -> tear the marker down if one's up.
            if (_renderedPath is not null)
            {
                _ = _js.ClearMobAsync();
                _renderedPath = null;
                _renderedLat = null;
                _renderedLon = null;
            }
            return;
        }
        Console.WriteLine($"[mob-renderer] ResyncRender: first.Path={first.Path} lat={first.Latitude} lon={first.Longitude}");
        // Skip when nothing changed (same path + coords + createdAt).
        // The store fires OnPathChanged on every Apply (including
        // status-only updates) and we don't want to re-pulse the
        // marker on each tick.
        if (string.Equals(_renderedPath, first.Path, StringComparison.Ordinal)
            && _renderedLat == first.Latitude
            && _renderedLon == first.Longitude
            && _renderedCreatedAt == first.CreatedAt)
        {
            Console.WriteLine("[mob-renderer] ResyncRender: dedup hit, skipping");
            return;
        }
        // Position is optional on the wire (server can emit MOB
        // without a fix). Without one, the marker can't be drawn
        // -- the alarm banner still fires; the chart just shows
        // no marker until the helm does something else.
        if (first.Latitude is not double lat || first.Longitude is not double lon)
        {
            return;
        }
        // Hand the server-stamped raise time across as ISO-8601 so
        // every connected plotter shows the same "MOB HH:MM:SS"
        // label. Null when the server is pre-v2 (no createdAt
        // field) -- the JS layer falls back to the local clock.
        var iso = first.CreatedAt is DateTime ts
            ? ts.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            : null;
        var selfMmsi = _ownMmsi?.Invoke();
        Console.WriteLine($"[mob-renderer] SetMobAsync(lat={lat}, lon={lon}, iso={iso}, mmsi={selfMmsi})");
        _ = _js.SetMobAsync(lat, lon, iso, selfMmsi);
        _renderedPath = first.Path;
        _renderedLat = lat;
        _renderedLon = lon;
        _renderedCreatedAt = first.CreatedAt;
    }

    public void Dispose()
    {
        _store.OnPathChanged -= HandlePathChanged;
        _js = null;
    }
}

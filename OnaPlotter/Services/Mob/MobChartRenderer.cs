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

    /// <summary>Current JS attachment (bridge handle + dedup state),
    /// or null when no Map page is mounted. Bundling the bridge with
    /// its dedup slots means each <see cref="AttachJs"/> creates a
    /// fresh dedup slate by construction -- no chance of a previous
    /// page's "rendered path / coords / createdAt" memory bleeding
    /// into the new bridge and short-circuiting its first paint.</summary>
    private Attachment? _attachment;

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
        if (js is null) throw new ArgumentNullException(nameof(js));
        // Always construct a new Attachment so the dedup slots
        // start null. ResyncRender then pushes the current MOB
        // unconditionally on the first call -- the JS layer comes
        // up clean and gets a complete paint without any prior
        // dedup state short-circuiting it.
        _attachment = new Attachment(js);
        ResyncRender();
    }

    /// <summary>Forget the JS handle (page unmount). Keeps store
    /// subscription so the next AttachJs picks up the latest
    /// state.</summary>
    public void DetachJs()
    {
        _attachment = null;
    }

    private void HandlePathChanged(string path)
    {
        if (!path.StartsWith(MobPathPrefix, StringComparison.Ordinal)) return;
        ResyncRender();
    }

    private void ResyncRender()
    {
        if (_attachment is not { } att) return;
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
            // No active MOB -> tear the marker down if one's up.
            if (att.RenderedPath is not null)
            {
                _ = att.Js.ClearMobAsync();
                att.RenderedPath = null;
                att.RenderedLat = null;
                att.RenderedLon = null;
            }
            return;
        }
        // Skip when nothing changed (same path + coords + createdAt).
        // The store fires OnPathChanged on every Apply (including
        // status-only updates) and we don't want to re-pulse the
        // marker on each tick.
        if (string.Equals(att.RenderedPath, first.Path, StringComparison.Ordinal)
            && att.RenderedLat == first.Latitude
            && att.RenderedLon == first.Longitude
            && att.RenderedCreatedAt == first.CreatedAt)
        {
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
        _ = att.Js.SetMobAsync(lat, lon, iso, selfMmsi);
        att.RenderedPath = first.Path;
        att.RenderedLat = lat;
        att.RenderedLon = lon;
        att.RenderedCreatedAt = first.CreatedAt;
    }

    public void Dispose()
    {
        _store.OnPathChanged -= HandlePathChanged;
        _attachment = null;
    }

    /// <summary>Bundles the JS bridge with the dedup slots that
    /// belong to that bridge. A new <see cref="MobChartRenderer.AttachJs"/>
    /// constructs a fresh instance, so dedup state can never leak
    /// across page mounts and the JS layer always gets a clean
    /// repaint when it comes up.</summary>
    private sealed class Attachment
    {
        public IMapControlsJs Js { get; }
        public string? RenderedPath { get; set; }
        public double? RenderedLat { get; set; }
        public double? RenderedLon { get; set; }
        public DateTime? RenderedCreatedAt { get; set; }

        public Attachment(IMapControlsJs js)
        {
            Js = js;
        }
    }
}

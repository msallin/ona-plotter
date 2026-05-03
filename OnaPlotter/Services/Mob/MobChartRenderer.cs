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

    public MobChartRenderer(ServerNotificationStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
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
        _js = null;
        _renderedPath = null;
    }

    private void HandlePathChanged(string path)
    {
        if (!path.StartsWith(MobPathPrefix, StringComparison.Ordinal)) return;
        ResyncRender();
    }

    private void ResyncRender()
    {
        if (_js is null) return;
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
            if (_renderedPath is not null)
            {
                _ = _js.ClearMobAsync();
                _renderedPath = null;
                _renderedLat = null;
                _renderedLon = null;
            }
            return;
        }
        // Skip when nothing changed (same path + same coords). The
        // store fires OnPathChanged on every Apply (including
        // status-only updates) and we don't want to re-pulse the
        // marker on each tick.
        if (string.Equals(_renderedPath, first.Path, StringComparison.Ordinal)
            && _renderedLat == first.Latitude
            && _renderedLon == first.Longitude)
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
        _ = _js.SetMobAsync(lat, lon);
        _renderedPath = first.Path;
        _renderedLat = lat;
        _renderedLon = lon;
    }

    public void Dispose()
    {
        _store.OnPathChanged -= HandlePathChanged;
        _js = null;
    }
}

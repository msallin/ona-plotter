namespace OnaPlotter.Services.Api;

/// <summary>
/// Thin client for the v2.0.0+ <c>signalk-anchoralarm-plugin</c>.
/// Two-step flow:
///
/// <list type="number">
///   <item>Helm taps "Drop" -> <see cref="DropAsync"/> POSTs the
///   plugin's drop endpoint (empty body). Plugin captures the
///   current GPS as the anchor position; <c>maxRadius</c> stays
///   null (unarmed state). The Incomplete Anchor Alarm timer
///   starts ticking server-side.</item>
///   <item>Helm backs down + lets out chain, then commits a
///   radius via <see cref="SetMaxRadiusAsync"/> (PUT
///   <c>navigation.anchor.maxRadius</c>). Numeric chips PUT the
///   exact helm-picked value; the Auto chip PUTs current-radius
///   + 5 m (or distance-derived + 5 m on first arming) for the
///   "grow on drift" gesture -- the 5 m increment lives on the
///   panel, the API call is the same PUT.</item>
///   <item><see cref="RaiseAsync"/> PUTs
///   <c>navigation.anchor.position</c> with <c>{value: null}</c>
///   to clear the anchor and stop monitoring.</item>
/// </list>
///
/// <para>Drop is plugin-specific (<c>POST /plugins/anchoralarm/dropAnchor</c>)
/// rather than the SK-spec PUT because the helm preferred the
/// plugin's flow -- it captures position from the SK bus
/// internally (no client lat/lon needed) and matches the plugin's
/// admin UI behaviour. The other two methods stay on the SK PUT
/// handlers (cross-plotter friendly, helm-pickable on a future
/// non-anchor-plugin install).</para>
///
/// <para>Docs: https://github.com/sbender9/signalk-anchoralarm-plugin
/// (v2.0.0 README -- "REST API" + "Signal K PUT Handlers"
/// sections).</para>
/// </summary>
public interface IAnchorAlarmApi
{
    /// <summary>Step 1: POST <c>/plugins/anchoralarm/dropAnchor</c>
    /// with empty body. Plugin captures current GPS as the anchor
    /// position; <c>navigation.anchor.maxRadius</c> stays null
    /// until step 2 lands. The plugin's Incomplete Anchor Alarm
    /// timer starts on this call.</summary>
    Task<ApiResult> DropAsync(CancellationToken ct = default);

    /// <summary>Step 2: PUT <c>navigation.anchor.maxRadius</c> with
    /// the helm-chosen value (metres). Server arms the alarm
    /// circle at this radius and starts publishing
    /// <c>navigation.anchor.currentRadius</c> for drift monitoring.
    /// Subsequent calls adjust the radius in place (no raise +
    /// re-drop needed).</summary>
    Task<ApiResult> SetMaxRadiusAsync(int radiusMeters, CancellationToken ct = default);

    /// <summary>Raise: PUT <c>{value: null}</c> on
    /// <c>navigation.anchor.position</c>. Plugin clears position +
    /// maxRadius from the SK bus and stops drift monitoring.</summary>
    Task<ApiResult> RaiseAsync(CancellationToken ct = default);
}

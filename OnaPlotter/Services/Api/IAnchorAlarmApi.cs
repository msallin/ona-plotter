namespace OnaPlotter.Services.Api;

/// <summary>
/// Thin client for the v2.0.0+ <c>signalk-anchoralarm-plugin</c> using
/// the standard SignalK PUT handlers wherever possible. Two-step flow:
///
/// <list type="number">
///   <item>Helm taps "Drop" -> <see cref="DropAtCurrentPositionAsync"/>
///   PUTs <c>navigation.anchor.position</c> with the current GPS lat/lon
///   + altitude. Plugin captures the drop point. The Incomplete Anchor
///   Alarm starts ticking server-side; if step 2 never lands, the bridge
///   surfaces the warning as a banner.</item>
///   <item>Helm backs down + lets out chain, then taps "Set radius" ->
///   either <see cref="SetMaxRadiusAsync"/> with a chosen value (PUT
///   <c>navigation.anchor.maxRadius</c>) or <see cref="AutoSetRadiusAsync"/>
///   (POST <c>/plugins/anchoralarm/setRadius</c> with empty body) so the
///   plugin computes the radius from the current GPS distance to the
///   drop point + its configured fudge factor.</item>
///   <item><see cref="RaiseAsync"/> PUTs <c>navigation.anchor.position</c>
///   with <c>{value: null}</c> -- the SK-spec way to clear an anchor.</item>
/// </list>
///
/// <para>v1.x plugin compatibility: v1 doesn't register the PUT handlers
/// and returns 404 / 405. The Map page treats any non-success on the
/// PUTs as "plugin v2.0.0+ required" and toasts an upgrade hint --
/// fail-loud beats a silent fallback that doubles the code paths and
/// hides a genuine misconfig.</para>
///
/// <para>The setRadius endpoint stays plugin-specific because no SK-spec
/// PUT exists for "compute the radius from current distance". One
/// non-PUT call is the smallest possible deviation from the standard.</para>
///
/// <para>Docs: https://github.com/sbender9/signalk-anchoralarm-plugin
/// (v2.0.0 README -- "Signal K PUT Handlers" section).</para>
/// </summary>
public interface IAnchorAlarmApi
{
    /// <summary>Step 1 of the drop. PUTs the current GPS lat/lon to
    /// <c>navigation.anchor.position</c>. Altitude is the depth at
    /// the drop point -- SK convention is BELOW water = negative
    /// metres; we negate the absolute value of <paramref name="depthMeters"/>.
    /// Pass null for depth when the helm hasn't got a depth source
    /// configured -- the plugin tolerates a missing altitude and
    /// derives one from the depth path itself when available.</summary>
    Task<ApiResult> DropAtCurrentPositionAsync(
        double latitude, double longitude, double? depthMeters,
        CancellationToken ct = default);

    /// <summary>Step 2 of the drop. PUTs the helm-chosen radius to
    /// <c>navigation.anchor.maxRadius</c>. Server arms the alarm circle
    /// at this radius and starts publishing
    /// <c>navigation.anchor.currentRadius</c> for drift monitoring.</summary>
    Task<ApiResult> SetMaxRadiusAsync(int radiusMeters, CancellationToken ct = default);

    /// <summary>Auto-compute step 2: POSTs an empty body to
    /// <c>/plugins/anchoralarm/setRadius</c>. Plugin uses its current
    /// distance from the drop point + the configured fudge factor +
    /// any safety margin to derive the radius. Helpful when the helm
    /// has backed down a known distance and just wants the alarm armed
    /// without picking a specific number.</summary>
    Task<ApiResult> AutoSetRadiusAsync(CancellationToken ct = default);

    /// <summary>Raise: PUTs <c>{value: null}</c> to
    /// <c>navigation.anchor.position</c>. Plugin clears position +
    /// maxRadius from the SK bus and stops drift monitoring.</summary>
    Task<ApiResult> RaiseAsync(CancellationToken ct = default);
}

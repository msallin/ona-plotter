namespace OnaPlotter.Services.Js;

/// <summary>
/// Typed C# wrapper over the leafletInterop.js anchor-watch surface.
/// Demonstrates the ARCH-005 pattern from the architecture review:
/// instead of bare <c>module.InvokeVoidAsync("setAnchor", ...)</c>
/// scattered across Map.razor and its partials (each call site
/// re-implementing JSDisconnected / ObjectDisposed / JSException
/// tolerance), the interop surface is grouped by feature area into
/// a typed C# contract. A typo in a JS function name now fails at
/// compile time rather than silently at runtime.
///
/// <para>The remaining feature areas (AIS, route, edit, resource,
/// overlays, controls) follow the same shape in subsequent
/// iterations. Each implementation centralises the "JS-side
/// disconnect during teardown" tolerance so call sites no longer
/// repeat the try/catch dance.</para>
/// </summary>
public interface IMapAnchorJs
{
    /// <summary>Drop the anchor at the given lat/lon with the
    /// stated swing-watch radius (metres).</summary>
    Task SetAnchorAsync(double lat, double lon, double radiusMeters);

    /// <summary>Clear the on-map anchor marker, ring, and trail.</summary>
    Task ClearAnchorAsync();

    /// <summary>Visually mark the anchor as "raising" while waiting
    /// for the server's cleared-anchor delta to land. Dims the
    /// marker + ring + trail without removing them so the helm sees
    /// the action took effect before the server confirms.</summary>
    Task SetAnchorRaisingAsync(bool raising);

    /// <summary>Update the swing-watch radius (metres) without
    /// re-dropping the anchor. Used when the helm dials the radius
    /// up after dropping.</summary>
    Task UpdateAnchorRadiusAsync(double radiusMeters);

    /// <summary>Visually mark the anchor as "drop committed but
    /// radius not yet set" - amber pulsing pin + dashed ring
    /// placeholder. Mirrors v2.0.0+'s two-step flow: once the helm
    /// taps Drop, the SK plugin's Incomplete Anchor Alarm starts
    /// ticking server-side, but visually the chart previously
    /// showed the same pin as a fully-armed anchor. Field-study
    /// finding (Margaret + Jordan): the dropped-but-unarmed state
    /// needs to LOOK incomplete on the chart so the helm doesn't
    /// walk away thinking they're done. The AnchorEditPanel
    /// SetRadius dialog is the helm-facing instruction surface
    /// during this state.</summary>
    Task SetAnchorIncompleteAsync(bool incomplete);

    /// <summary>Toggle manual-move mode. When enabled, a draggable
    /// handle is overlaid on the pin; dragging it repositions the
    /// pin + watch circle + radius line live (visual preview only -
    /// the PUT happens via <c>IAnchorAlarmApi.SetPositionAsync</c>
    /// when the helm commits). When disabled, the handle is removed;
    /// callers that disable without committing should first call
    /// <see cref="SetAnchorPositionAsync"/> to snap the visuals back
    /// to the server position.</summary>
    Task SetAnchorMoveModeAsync(bool enable);

    /// <summary>Reposition the pin, watch circle and radius line to a
    /// new anchor position without tearing the overlay down (used by
    /// the move-cancel revert path so the swing trail survives).</summary>
    Task SetAnchorPositionAsync(double lat, double lon);

    /// <summary>Read the latest dragged position while move mode is
    /// active, or null when move mode is off / nothing was dragged.
    /// The page reads this on Set to decide whether the position
    /// actually moved before committing a PUT.</summary>
    Task<AnchorLatLng?> GetAnchorMovedLatLngAsync();
}

/// <summary>Lat/lon pair returned from the anchor move-handle JS.
/// Property names match the JS object's <c>{ lat, lon }</c> shape
/// (Blazor's interop JSON is case-insensitive).</summary>
public readonly record struct AnchorLatLng(double Lat, double Lon);

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
}

namespace OnaPlotter.Services.Map;

using OnaPlotter.Services.Js;

/// <summary>
/// Builds the JS-side AtoN snapshot from <see cref="AtonStore"/> and
/// pushes it through <see cref="IMapAisJs"/> (AtoN payloads share the
/// AIS interop wrapper because they're rendered by the same marker
/// pipeline on the JS side).
///
/// Resolves symbol kind + cardinal/lateral side via
/// <see cref="AtonTypeCatalog"/> here in C# so JS stays a dumb
/// renderer. Pulled out of Map.razor so the same payload-shape logic
/// can be regression-tested without bUnit / IJSRuntime.
///
/// Lifecycle: instantiated by Map.razor in <c>OnAfterRenderAsync</c>
/// once the JS module reference is available. The page invokes
/// <see cref="PushAsync"/> once after init plus from the
/// <c>AtonStore.OnAtonsUpdated</c> event (no timer; AtoNs are static
/// enough not to need polling).
/// </summary>
public sealed class AtonPushService
{
    private readonly IMapAisJs _aisJs;
    private readonly AtonStore _atonStore;

    public AtonPushService(IMapAisJs aisJs, AtonStore atonStore)
    {
        _aisJs = aisJs ?? throw new ArgumentNullException(nameof(aisJs));
        _atonStore = atonStore ?? throw new ArgumentNullException(nameof(atonStore));
    }

    /// <summary>
    /// Builds + pushes the current AtoN snapshot. Idempotent re-pushes
    /// are cheap (the JS side diffs by id), so the caller doesn't
    /// debounce; the C# AtonStore.GetAtons cache makes a re-push on
    /// no-data-change a snapshot read.
    /// </summary>
    public async Task PushAsync()
    {
        try
        {
            // Resolve the symbol kind + cardinal/lateral side here in C#
            // via AtonTypeCatalog.Lookup so JS stays a dumb renderer
            // (per the project's "decisions in C# with tests" rule).
            // Virtual flag: prefer the AtoN's own published value;
            // otherwise fall back to the catalog's "20-25 are virtual"
            // hint so an older plugin that omits the flag still
            // renders dashed.
            var atons = _atonStore.GetAtons();
            var jsAtons = atons
                .Where(a => a.Latitude is not null && a.Longitude is not null)
                .Select(a =>
                {
                    var info = AtonTypeCatalog.Lookup(a.TypeId);
                    bool isVirtual = a.Virtual ?? info.VirtualHint;
                    return (object)new
                    {
                        context = a.Context,
                        name = a.Name,
                        mmsi = a.Mmsi,
                        lat = a.Latitude!.Value,
                        lon = a.Longitude!.Value,
                        typeId = a.TypeId,
                        typeName = a.TypeName,
                        symbol = info.Symbol.ToString(),
                        side = info.Side.ToString(),
                        @virtual = isVirtual,
                    };
                })
                .ToArray();
            await _aisJs.SetAtonsAsync(jsAtons);
        }
        catch (Microsoft.JSInterop.JSException ex)
        {
            Console.Error.WriteLine($"[interop] PushAtons: {ex.Message}");
        }
    }
}

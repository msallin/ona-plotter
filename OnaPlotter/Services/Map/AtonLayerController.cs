using System.Text.Json.Serialization;
using OnaPlotter.Models;
using OnaPlotter.Services.Js;

namespace OnaPlotter.Services.Map;

/// <summary>
/// One AtoN as <c>atonLayer.setAtons</c> renders it: symbol kind and
/// side resolved through <see cref="AtonTypeCatalog"/>, popup text
/// pre-built.
/// </summary>
public sealed record AtonPayload(
    [property: JsonPropertyName("context")] string Context,
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lon")] double Lon,
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("virtual")] bool Virtual,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("subtitle"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Subtitle,
    [property: JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail);

/// <summary>
/// Builds the AtoN snapshot from <see cref="AtonStore"/> and pushes it
/// through <see cref="IMapAisJs"/> (AtoNs share the AIS interop wrapper
/// because the same marker pipeline renders them).
///
/// <para>Coalesced: <see cref="AtonStore.OnAtonsUpdated"/> fires per
/// delta, and a reconnect replays every AtoN, so pushing per event costs
/// O(N) per delta, O(N^2) per replay. The page calls
/// <see cref="MarkDirty"/> from the event and <see cref="PushIfDirtyAsync"/>
/// once per AIS tick, so a burst becomes one push.</para>
///
/// Lifecycle: instantiated by Map.razor once the JS module reference is
/// available; <see cref="PushAsync"/> once after init.
/// </summary>
public sealed class AtonLayerController
{
    private readonly IMapAisJs _aisJs;
    private readonly AtonStore _atonStore;
    private bool _dirty;
    private int _lastPushedVersion = -1;

    public AtonLayerController(IMapAisJs aisJs, AtonStore atonStore)
    {
        _aisJs = aisJs ?? throw new ArgumentNullException(nameof(aisJs));
        _atonStore = atonStore ?? throw new ArgumentNullException(nameof(atonStore));
    }

    /// <summary>The store changed; the next <see cref="PushIfDirtyAsync"/>
    /// pushes. Cheap enough to call from every store event.</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>Pushes when something changed since the last push.</summary>
    public Task PushIfDirtyAsync() => _dirty ? PushAsync() : Task.CompletedTask;

    /// <summary>
    /// Builds and pushes the current snapshot, unless the store version is
    /// the one already pushed. A failed push leaves the service dirty so
    /// the next tick retries.
    /// </summary>
    public async Task PushAsync()
    {
        _dirty = false;
        // GetAtons may prune, which bumps the version: read it after.
        var atons = _atonStore.GetAtons();
        int version = _atonStore.Version;
        if (version == _lastPushedVersion) return;
        try
        {
            await _aisJs.SetAtonsAsync(BuildSnapshot(atons));
            _lastPushedVersion = version;
        }
        catch (Microsoft.JSInterop.JSException ex)
        {
            _dirty = true;
            // Console.WriteLine (not Console.Error) so the handled JS
            // regression doesn't surface as an unhandled error via
            // errorRelayBoot.js's console.error wrapper.
            Console.WriteLine($"[interop] PushAtons: {ex.Message}");
        }
    }

    internal static AtonPayload[] BuildSnapshot(IReadOnlyList<Aton> atons)
    {
        var result = new List<AtonPayload>(atons.Count);
        foreach (var a in atons)
        {
            if (a.Latitude is not double lat || a.Longitude is not double lon) continue;
            var info = AtonTypeCatalog.Lookup(a.TypeId);
            // Prefer the AtoN's own virtual flag; fall back to the
            // catalog's "types 20-25 are virtual" hint for plugins that
            // omit it, so the mark still renders dashed.
            bool isVirtual = a.Virtual ?? info.VirtualHint;
            result.Add(new AtonPayload(
                Context: a.Context,
                Lat: lat,
                Lon: lon,
                Symbol: info.Symbol.ToString(),
                Side: info.Side.ToString(),
                Virtual: isVirtual,
                Title: Title(a.Name, a.Mmsi, isVirtual),
                Subtitle: !string.IsNullOrEmpty(a.TypeName) ? a.TypeName
                    : a.TypeId is int id ? $"Type {id}" : null,
                Detail: !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.Mmsi)
                    ? $"MMSI {a.Mmsi}" : null));
        }
        return result.ToArray();
    }

    /// <summary>Popup title: name, else MMSI, else "AtoN"; virtual marks
    /// say so, since there is nothing in the water to look for.</summary>
    internal static string Title(string? name, string? mmsi, bool isVirtual)
    {
        string title = !string.IsNullOrEmpty(name) ? name
            : !string.IsNullOrEmpty(mmsi) ? mmsi
            : "AtoN";
        return isVirtual ? title + " (virtual)" : title;
    }
}

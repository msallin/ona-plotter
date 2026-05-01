using System.Text.Json;
using Microsoft.Extensions.Logging;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// One-shot REST fetch of <c>/signalk/v1/api/vessels</c>, walking the
/// tree for every vessel the server knows about and feeding the
/// static-data leaves (name / mmsi / callsign / ship type / position)
/// into <see cref="AisStore"/> as if they had arrived on the delta
/// stream.
///
/// <para>Bridges the gap that the SignalK subscription model leaves
/// open: the websocket only delivers values as they CHANGE, so a
/// vessel whose name was set in vessel.json before our connection
/// opened never lands on the stream and would otherwise show as a
/// bare MMSI on first paint.</para>
/// </summary>
public sealed class SignalkVesselNamesSeeder
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly AisStore _ais;
    private readonly ILogger<SignalkVesselNamesSeeder> _logger;

    public SignalkVesselNamesSeeder(
        HttpClient http,
        ISignalKBaseUrl baseUrl,
        AisStore ais,
        ILogger<SignalkVesselNamesSeeder> logger)
    {
        _http = http;
        _baseUrl = baseUrl;
        _ais = ais;
        _logger = logger;
    }

    /// <summary>
    /// Walk the vessels tree and apply each vessel's static leaves to
    /// <see cref="AisStore"/>. Best-effort: a failure leaves names to
    /// trickle in via live deltas instead.
    /// </summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        try
        {
            var url = _baseUrl.Combine("/signalk/v1/api/vessels");
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogDebug("vessels endpoint {Url} returned {Status}", url, (int)res.StatusCode);
                return;
            }

            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            int seeded = 0;
            foreach (var vesselProp in doc.RootElement.EnumerateObject())
            {
                if (ct.IsCancellationRequested) break;
                // Each property key is the short vessel id ("urn:mrn:imo:mmsi:...");
                // AIS contexts on the delta stream use "vessels." prefix.
                string context = vesselProp.Name.StartsWith("vessels.", StringComparison.Ordinal)
                    ? vesselProp.Name
                    : $"vessels.{vesselProp.Name}";
                if (ApplyRestVesselTree(context, vesselProp.Value)) seeded++;
            }
            if (seeded > 0)
                _logger.LogInformation("Seeded static data for {Count} vessels from REST snapshot", seeded);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "REST vessel-snapshot seed failed (names will trickle in via deltas)");
        }
    }

    /// <summary>
    /// Walks the value tree for a single vessel and forwards the leaves
    /// <see cref="AisVessel.Apply"/> understands. Returns true if any leaf
    /// was applied so the caller can count "interesting" vessels.
    /// </summary>
    private bool ApplyRestVesselTree(string context, JsonElement vessel)
    {
        if (vessel.ValueKind != JsonValueKind.Object) return false;
        bool any = false;

        // SignalK wraps leaf values as { "value": ..., "timestamp": ... }
        // -- so "name" might be under vessel.name.value or vessel.name
        // depending on the server. Try both shapes.
        any |= TryApplyScalar(context, "name", vessel, "name");
        any |= TryApplyScalar(context, "mmsi", vessel, "mmsi");
        if (vessel.TryGetProperty("communication", out var comm)
            && comm.ValueKind == JsonValueKind.Object)
        {
            any |= TryApplyScalar(context, "communication.callsignVhf", comm, "callsignVhf");
        }
        if (vessel.TryGetProperty("design", out var design)
            && design.ValueKind == JsonValueKind.Object
            && design.TryGetProperty("aisShipType", out var typeWrap))
        {
            var typeVal = SignalkDraftSeeder.UnwrapValue(typeWrap);
            if (typeVal.ValueKind != JsonValueKind.Undefined && typeVal.ValueKind != JsonValueKind.Null)
            {
                _ais.Apply(context, "design.aisShipType", typeVal);
                any = true;
            }
        }
        if (vessel.TryGetProperty("navigation", out var nav)
            && nav.ValueKind == JsonValueKind.Object
            && nav.TryGetProperty("position", out var posWrap))
        {
            var posVal = SignalkDraftSeeder.UnwrapValue(posWrap);
            if (posVal.ValueKind == JsonValueKind.Object)
            {
                _ais.Apply(context, OnaPlotter.Utilities.SkPaths.Navigation.Position, posVal);
                any = true;
            }
        }
        return any;
    }

    private bool TryApplyScalar(string context, string path, JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var wrap)) return false;
        var val = SignalkDraftSeeder.UnwrapValue(wrap);
        if (val.ValueKind != JsonValueKind.String) return false;
        _ais.Apply(context, path, val);
        return true;
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services.Signalk;

/// <summary>
/// One-shot REST fetch of <c>design.draft</c> for the self vessel.
/// Draft is declared statically in vessel.json on almost every
/// install, so the delta stream never replays it after subscribe.
/// No v2 equivalent exists yet - v1 is the only source - so this
/// stays on the v1 REST surface.
///
/// <para>Started on every connect by
/// <see cref="SignalkConnectSeeding"/>, alongside the vessel-names,
/// course and self-context seeders; each owns one REST round-trip and
/// its parsing rules.</para>
/// </summary>
public sealed class SignalkDraftSeeder
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly NavigationData _data;
    private readonly ILogger<SignalkDraftSeeder> _logger;
    private readonly Action _onDataChanged;

    public SignalkDraftSeeder(
        HttpClient http,
        ISignalKBaseUrl baseUrl,
        NavigationData data,
        ILogger<SignalkDraftSeeder> logger,
        Action onDataChanged)
    {
        _http = http;
        _baseUrl = baseUrl;
        _data = data;
        _logger = logger;
        _onDataChanged = onDataChanged;
    }

    /// <summary>
    /// Fetch <c>/signalk/v1/api/vessels/self/design/draft</c> and
    /// feed the result into <see cref="NavigationData.Apply"/>.
    /// Best-effort: 404 (no design data) or parse failure stays at
    /// Debug level so an empty vessel.json doesn't spam the log.
    /// </summary>
    /// <remarks>
    /// Response shape on signalk-server:
    /// <code>{"meta": {...}, "value": {"maximum": 1.25, "current": 1.0},
    ///         "$source": "...", "timestamp": "..."}</code>
    /// The {current, maximum} numbers live INSIDE the value envelope.
    /// Two fallbacks stay wired so the method still works on older or
    /// alternate server builds that emit sibling leaves or bare numbers.
    /// </remarks>
    public async Task SeedAsync(CancellationToken ct)
    {
        JsonDocument? doc = null;
        try
        {
            var url = _baseUrl.Combine("/signalk/v1/api/vessels/self/design/draft");
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogDebug("design.draft endpoint {Url} returned {Status}", url, (int)res.StatusCode);
                return;
            }

            using var stream = await res.Content.ReadAsStreamAsync(ct);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            // Canonical shape: { value: {current, maximum}, ... }.
            // Older / alternate servers may put current+maximum at root
            // or as sibling leaves; the fallbacks keep both shapes wired.
            JsonElement source = doc.RootElement;
            if (doc.RootElement.TryGetProperty("value", out var valueEnvelope)
                && valueEnvelope.ValueKind == JsonValueKind.Object
                && (valueEnvelope.TryGetProperty("current", out _)
                    || valueEnvelope.TryGetProperty("maximum", out _)))
            {
                source = valueEnvelope;
            }

            // Prefer current over maximum to match Apply's delta-path
            // precedence. Unwrap inner {value, timestamp} envelopes so
            // the per-sibling-leaf shape still parses.
            bool seeded = false;
            if (source.TryGetProperty("current", out var cur))
            {
                var val = UnwrapValue(cur);
                if (val.ValueKind == JsonValueKind.Number)
                {
                    _data.Apply("design.draft.current", val);
                    seeded = true;
                }
            }
            if (!seeded && source.TryGetProperty("maximum", out var max))
            {
                var val = UnwrapValue(max);
                if (val.ValueKind == JsonValueKind.Number)
                {
                    _data.Apply("design.draft.maximum", val);
                    seeded = true;
                }
            }

            if (seeded)
            {
                _onDataChanged.Invoke();
                _logger.LogInformation("Seeded design.draft from v1 REST (DraftFromSignalK = {Draft} m)",
                    _data.DraftFromSignalK);
            }
            else
            {
                _logger.LogDebug("design.draft REST response had no current/maximum leaf to seed");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "design.draft REST seed failed (falls back to Settings manual override)");
        }
        finally { doc?.Dispose(); }
    }

    /// <summary>
    /// Some SK servers emit leaves as <c>{"value": x, "timestamp": "..."}</c>
    /// instead of bare scalars; this unwraps so downstream consumers
    /// see the scalar regardless of the envelope shape. Public-static
    /// so future seed extracts can share the same helper.
    /// </summary>
    public static JsonElement UnwrapValue(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("value", out var inner))
        {
            return inner;
        }
        return el;
    }
}

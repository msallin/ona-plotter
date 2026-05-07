using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Client for the Signal K Radar API v3.1 REST surface. Everything
/// that's not spoke data goes through here: device discovery,
/// capabilities / legend, and control read + write. The binary spoke
/// stream is opened JS-side (we hand it the URL constructed via
/// <see cref="SignalKUrls.RadarSpokeWs"/>). ARPA target polling is
/// not exposed yet - the consumer (overlay markers) doesn't exist;
/// adding the call now would force an API shape (3-state null,
/// discriminated result, or pre-cached capability) without a real
/// caller to design for. Re-add when the targets UI lands.
/// </summary>
public interface IRadarApi
{
    /// <summary>Lists every radar the server knows about. Returns an
    /// empty list (rather than null) when the API is unavailable;
    /// UI can then show "no radars detected" without special-casing.</summary>
    Task<IReadOnlyList<RadarInfo>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Static per-device capabilities + legend + control
    /// definitions. Fetch once per session per radar; the spec
    /// guarantees this is stable during operation. Null when the
    /// radar id isn't known.</summary>
    Task<RadarCapabilities?> GetCapabilitiesAsync(string radarId, CancellationToken ct = default);

    /// <summary>Current value of every control on the radar, keyed
    /// by control id. Use alongside the capabilities control map to
    /// render an adaptive UI.</summary>
    Task<Dictionary<string, ControlValue>?> GetControlsAsync(string radarId, CancellationToken ct = default);

    /// <summary>Writes a single control. Body shape varies by
    /// <c>dataType</c>; callers construct the right <see cref="ControlValue"/>
    /// fields (value only for numbers, value+endValue for sectors, etc.).
    /// Returns an <see cref="ApiResult"/> whose <c>Error</c> is the
    /// server's <c>error</c> field when present (e.g. "Control range
    /// value 12000 is not a legal value" - worth surfacing verbatim
    /// so the helm knows which value the server rejected).</summary>
    Task<ApiResult> SetControlAsync(string radarId, string controlId, ControlValue value, CancellationToken ct = default);
}

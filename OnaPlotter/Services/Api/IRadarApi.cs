using OnaPlotter.Models;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Client for the Signal K Radar API v3.1 REST surface. Everything
/// that's not spoke data goes through here: device discovery,
/// capabilities / legend, control read + write, and ARPA target
/// polling. The binary spoke stream is opened JS-side (we just hand
/// it the <see cref="RadarInfo.SpokeDataUrl"/>).
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
    /// Returns a <see cref="RadarSetControlResult"/> with success +
    /// an optional error message extracted from the server's JSON
    /// body (e.g. "Control range value 12000 is not a legal value"
    /// -- worth surfacing verbatim so the helm knows which value
    /// the server rejected).</summary>
    Task<RadarSetControlResult> SetControlAsync(string radarId, string controlId, ControlValue value, CancellationToken ct = default);

    /// <summary>All currently tracked ARPA targets for this radar.
    /// Empty list when the radar isn't tracking anything; null when
    /// the provider returned 501 (ARPA unsupported).</summary>
    Task<IReadOnlyList<RadarArpaTarget>?> GetTargetsAsync(string radarId, CancellationToken ct = default);
}

/// <summary>Outcome of a PUT /controls/{id}. On failure, <see cref="Error"/>
/// is the server's <c>error</c> field when present, else the raw response
/// body or a transport-level message.</summary>
public sealed record RadarSetControlResult(bool Success, string? Error = null)
{
    public static RadarSetControlResult Ok { get; } = new(true);
    public static RadarSetControlResult Fail(string? err) => new(false, err);
}

using OnaPlotter.Models;

namespace OnaPlotter.Services.Radar;

/// <summary>
/// Abstraction over the JS-side radar overlay (canvas + WebSocket
/// driver) that <see cref="RadarOverlayManager"/> talks to. Pulled
/// out of the manager so the orchestration loop can be tested
/// without bUnit, IJSRuntime, or a real browser - a fake host
/// captures the calls instead.
///
/// The production implementation (<see cref="JsRadarOverlayHost"/>)
/// wraps the loaded radar JS module. Errors are reported back via
/// the throw / no-throw contract in each method's docs so the
/// manager can decide whether to toast or swallow.
/// </summary>
public interface IRadarOverlayHost
{
    /// <summary>Open the overlay canvas and spoke WebSocket for one
    /// radar. Throws <see cref="RadarOverlayException"/> on JS interop
    /// failure so the manager can surface the message to the helm.
    /// No-op when the host hasn't been wired to a JS module yet
    /// (early in page lifecycle).</summary>
    Task StartOverlayAsync(RadarOverlayStartConfig cfg, CancellationToken ct = default);

    /// <summary>Tear down the overlay for one radar. Errors are
    /// swallowed - teardown is best-effort and should never block a
    /// UI flow (the user already moved on).</summary>
    Task StopOverlayAsync(string radarId, CancellationToken ct = default);

    /// <summary>Push a new range value to an active overlay. Errors
    /// are swallowed - the next poll re-attempts.</summary>
    Task UpdateRangeAsync(string radarId, int range, CancellationToken ct = default);
}

/// <summary>Configuration handed to the JS layer when starting an
/// overlay. JSON-shape matches the JS module's <c>startRadarOverlay</c>
/// argument; named record so callers don't drift from the contract.</summary>
public sealed record RadarOverlayStartConfig(
    string RadarId,
    string SpokeDataUrl,
    int SpokesPerRevolution,
    int MaxSpokeLength,
    int Range,
    RadarLegend? Legend,
    double Opacity);

/// <summary>Surfaced to the manager when starting an overlay fails;
/// the manager translates this to a user-visible toast.</summary>
public sealed class RadarOverlayException(string message, Exception? inner = null)
    : Exception(message, inner);

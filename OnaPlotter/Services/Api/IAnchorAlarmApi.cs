namespace OnaPlotter.Services.Api;

/// <summary>
/// Thin client for the <c>signalk-anchoralarm-plugin</c> REST API.
/// Only the endpoints we actually drive from the map UI are exposed;
/// the plugin has more (setRodeLength, setManualAnchor, ...) but the
/// plotter only wires the ones with a UI surface. Add new methods
/// here when a new UI surface needs them; this interface intentionally
/// stays minimal.
///
/// Docs: https://github.com/sbender9/signalk-anchoralarm-plugin
/// </summary>
public interface IAnchorAlarmApi
{
    /// <summary>POSTs to <c>/plugins/anchoralarm/dropAnchor</c> with
    /// <c>{ "radius": meters }</c>. Plugin reads own-ship position
    /// itself (bow-offset corrected via the plugin's own settings),
    /// publishes <c>navigation.anchor.position</c> + <c>maxRadius</c>,
    /// and starts drift monitoring. Non-success means plugin missing
    /// or rejected -- caller toasts the message so the helm knows
    /// whether to fall back to the JS-only manual flow.</summary>
    Task<ApiResult> DropAsync(int radiusMeters, CancellationToken ct = default);

    /// <summary>POSTs to <c>/plugins/anchoralarm/raiseAnchor</c>.
    /// Clears the anchor position and disables drift monitoring.
    /// Non-success returns surface the server's error envelope so the
    /// caller can toast a specific reason (plugin not installed, no
    /// auth, etc.).</summary>
    Task<ApiResult> RaiseAsync(CancellationToken ct = default);
}

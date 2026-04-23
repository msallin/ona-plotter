namespace OnaPlotter.Services.Api;

/// <summary>
/// Thin client for the <c>signalk-anchoralarm-plugin</c> REST API.
/// Only the endpoints we actually drive from the map UI are exposed;
/// the plugin has more (setRadius, setRodeLength, ...) but the plotter
/// only needs to raise a live anchor for now.
///
/// Docs: https://github.com/sbender9/signalk-anchoralarm-plugin
/// </summary>
public interface IAnchorAlarmApi
{
    /// <summary>POSTs to <c>/plugins/anchoralarm/raiseAnchor</c>.
    /// Clears the anchor position and disables drift monitoring.
    /// Non-success returns surface the server's error envelope so the
    /// caller can toast a specific reason (plugin not installed, no
    /// auth, etc.).</summary>
    Task<ApiResult> RaiseAsync(CancellationToken ct = default);
}

using Microsoft.JSInterop;
using OnaPlotter.Models;

namespace OnaPlotter.Services.Radar;

/// <summary>
/// Production <see cref="IRadarOverlayHost"/> implementation. Wraps
/// the loaded radar JS module (<c>radarLayer.js</c> via the page's
/// imported leaflet interop module). The page calls
/// <see cref="SetModule"/> exactly once after the module promise
/// resolves; until then every call no-ops, which matches the natural
/// lifecycle (no module loaded yet -> nothing to drive).
/// </summary>
public sealed class JsRadarOverlayHost : IRadarOverlayHost
{
    private IJSObjectReference? _module;

    /// <summary>Page-side wiring step: hand over the loaded module
    /// reference once it's available. Subsequent calls overwrite,
    /// which matches the page disposal / re-creation lifecycle on
    /// nav.</summary>
    public void SetModule(IJSObjectReference module) => _module = module;

    public async Task StartOverlayAsync(RadarOverlayStartConfig cfg, CancellationToken ct = default)
    {
        if (_module is null) return;
        try
        {
            // The JS module expects a camelCase object literal; the
            // record is serialised via System.Text.Json which honours
            // [JsonPropertyName] on its members. Pass an anonymous
            // shape so we don't have to maintain naming attributes
            // on the record fields.
            await _module.InvokeVoidAsync("startRadarOverlay", ct, new
            {
                radarId = cfg.RadarId,
                spokeDataUrl = cfg.SpokeDataUrl,
                spokesPerRevolution = cfg.SpokesPerRevolution,
                maxSpokeLength = cfg.MaxSpokeLength,
                range = cfg.Range,
                legend = cfg.Legend,
                opacity = cfg.Opacity,
                useWireBearing = cfg.UseWireBearing,
                bearingAlignmentRad = cfg.BearingAlignmentRad,
            });
        }
        catch (JSDisconnectedException)
        {
            // Page navigating away mid-call. Treat as silent success.
        }
        catch (JSException ex)
        {
            throw new RadarOverlayException(ex.Message, ex);
        }
    }

    public async Task StopOverlayAsync(string radarId, CancellationToken ct = default)
    {
        if (_module is null) return;
        try { await _module.InvokeVoidAsync("stopRadarOverlay", ct, radarId); }
        catch (JSDisconnectedException) { }
        catch (JSException) { /* teardown should not block */ }
    }

    public async Task UpdateRangeAsync(string radarId, int range, CancellationToken ct = default)
    {
        if (_module is null) return;
        try { await _module.InvokeVoidAsync("updateRadarRange", ct, radarId, range); }
        catch (JSDisconnectedException) { }
        catch (JSException) { /* JS gone; next push retries */ }
    }
}

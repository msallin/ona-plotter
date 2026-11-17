using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapAisJs"/> backed by an <see cref="IJSObjectReference"/>
/// handle to the leafletInterop.js module. Centralises the
/// JSDisconnected / ObjectDisposed swallow so call sites don't each
/// re-implement it.
///
/// <para>Lifetime mirrors <see cref="MapAnchorJs"/>: created with the
/// module reference once Map.razor has loaded the JS module, and lives
/// until the page disposes the module. After dispose every method
/// becomes a silent no-op so in-flight callers don't need their own
/// try/catch wrappers.</para>
/// </summary>
public sealed class MapAisJs : IMapAisJs
{
    private readonly SafeJsModule _js;

    public MapAisJs(IJSObjectReference module)
    {
        _js = new SafeJsModule(module);
    }

    /// <summary>Mark the wrapper as disposed so subsequent calls
    /// silently no-op. Called by Map.razor's <c>DisposeAsync</c> path
    /// before it disposes the module reference.</summary>
    public void MarkDisposed() => _js.MarkDisposed();

    /// <summary>A disposed or disconnected page reports "not accepted"
    /// so the next live push, if any, starts from a full snapshot.</summary>
    public async Task<bool> UpdateAisTargetsAsync(OnaPlotter.Services.Map.AisUpdatePayload update)
    {
        return await _js.InvokeAsync(false, "updateAisTargets", update);
    }

    public Task SetAtonsAsync(OnaPlotter.Services.Map.AtonPayload[] atons)
        => _js.InvokeVoidAsync("setAtons", (object)atons);

    public Task SetAtonsVisibleAsync(bool visible)
        => _js.InvokeVoidAsync("setAtonsVisible", visible);

    public Task SetAisLabelsVisibleAsync(bool visible)
        => _js.InvokeVoidAsync("setAisLabelsVisible", visible);

    public Task SetOwnMmsiAsync(string mmsi)
        => _js.InvokeVoidAsync("setOwnMmsi", mmsi);

    public Task SetOwnCallsignAsync(string callsign)
        => _js.InvokeVoidAsync("setOwnCallsign", callsign);

    public Task SetHarborModeAsync(bool enabled)
        => _js.InvokeVoidAsync("setHarborMode", enabled);

    /// <summary>Returns false during disposal too - the only caller
    /// uses the false branch to surface a "vessel no longer on the
    /// chart" toast, which is a benign no-op when the page is unmounting
    /// and the toast system has already torn down.</summary>
    public async Task<bool> FocusVesselAsync(string context)
    {
        return await _js.InvokeAsync(false, "focusVessel", context);
    }
}

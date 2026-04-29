using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapAisJs"/> backed by an <see cref="IJSObjectReference"/>
/// handle to the leafletInterop.js module. Centralises the
/// JSDisconnected / ObjectDisposed swallow that every call site
/// previously re-implemented.
///
/// <para>Lifetime mirrors <see cref="MapAnchorJs"/>: created with the
/// module reference once Map.razor has loaded the JS module, and lives
/// until the page disposes the module. After dispose every method
/// becomes a silent no-op so in-flight callers don't need their own
/// try/catch wrappers.</para>
/// </summary>
public sealed class MapAisJs : IMapAisJs
{
    private readonly IJSObjectReference _module;
    private bool _disposed;

    public MapAisJs(IJSObjectReference module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    /// <summary>Mark the wrapper as disposed so subsequent calls
    /// silently no-op. Called by Map.razor's <c>DisposeAsync</c> path
    /// before it disposes the module reference.</summary>
    public void MarkDisposed() => _disposed = true;

    public Task UpdateAisTargetsAsync(object[] vessels)
        => InvokeSafe("updateAisTargets", (object)vessels);

    public Task SetAtonsAsync(object[] atons)
        => InvokeSafe("setAtons", (object)atons);

    public Task SetAtonsVisibleAsync(bool visible)
        => InvokeSafe("setAtonsVisible", visible);

    public Task SetOwnMmsiAsync(string mmsi)
        => InvokeSafe("setOwnMmsi", mmsi);

    public Task SetHarborModeAsync(bool enabled)
        => InvokeSafe("setHarborMode", enabled);

    public Task SetGuardZoneVisibleAsync(bool visible)
        => InvokeSafe("setGuardZoneVisible", visible);

    /// <summary>Returns false during disposal too -- the only caller
    /// uses the false branch to surface a "vessel no longer on the
    /// chart" toast, which is a benign no-op when the page is unmounting
    /// and the toast system has already torn down.</summary>
    public async Task<bool> FocusVesselAsync(string context)
    {
        if (_disposed) return false;
        try
        {
            return await _module.InvokeAsync<bool>("focusVessel", context);
        }
        catch (JSDisconnectedException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    /// <summary>
    /// Single safe-call helper. Swallows the two "page is unmounting"
    /// exceptions that otherwise scatter try/catch blocks across every
    /// call site; lets <see cref="JSException"/> through so a real
    /// JS-side regression still surfaces.
    /// </summary>
    private async Task InvokeSafe(string identifier, params object?[] args)
    {
        if (_disposed) return;
        try
        {
            await _module.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException) { /* page is unmounting */ }
        catch (ObjectDisposedException) { /* JS module disposed first */ }
    }
}

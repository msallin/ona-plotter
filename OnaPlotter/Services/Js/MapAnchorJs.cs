using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapAnchorJs"/> backed by an
/// <see cref="IJSObjectReference"/> handle to the leafletInterop.js
/// module. Centralises the JSDisconnected / ObjectDisposed / JSException
/// tolerance so call sites don't each re-implement it.
///
/// <para>Lifetime: created with the module reference once Map.razor
/// has loaded the JS module, and lives until the page disposes the
/// module. After dispose, every method becomes a silent no-op so
/// in-flight callers don't need their own try/catch wrappers.</para>
/// </summary>
public sealed class MapAnchorJs : IMapAnchorJs
{
    private readonly SafeJsModule _js;

    public MapAnchorJs(IJSObjectReference module)
    {
        _js = new SafeJsModule(module);
    }

    /// <summary>Mark the wrapper as disposed so subsequent calls
    /// silently no-op. Called by Map.razor's <c>DisposeAsync</c>
    /// path before it disposes the module reference; without this,
    /// in-flight Sync* methods could otherwise still reach the
    /// already-disposed JS reference.</summary>
    public void MarkDisposed() => _js.MarkDisposed();

    public Task SetAnchorAsync(double lat, double lon, double radiusMeters)
        => _js.InvokeVoidAsync("setAnchor", lat, lon, radiusMeters);

    public Task ClearAnchorAsync()
        => _js.InvokeVoidAsync("clearAnchor");

    public Task SetAnchorRaisingAsync(bool raising)
        => _js.InvokeVoidAsync("setAnchorRaising", raising);

    public Task UpdateAnchorRadiusAsync(double radiusMeters)
        => _js.InvokeVoidAsync("updateAnchorRadius", radiusMeters);

    public Task SetAnchorIncompleteAsync(bool incomplete)
        => _js.InvokeVoidAsync("setAnchorIncomplete", incomplete);

    public Task SetAnchorMoveModeAsync(bool enable)
        => _js.InvokeVoidAsync("setAnchorMoveMode", enable);

    public Task SetAnchorPositionAsync(double lat, double lon)
        => _js.InvokeVoidAsync("setAnchorPosition", lat, lon);

    public Task<AnchorLatLng?> GetAnchorMovedLatLngAsync()
        => _js.InvokeAsync<AnchorLatLng?>(null, "getAnchorMovedLatLng");
}

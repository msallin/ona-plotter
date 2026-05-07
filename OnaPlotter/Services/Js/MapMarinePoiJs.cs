using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapMarinePoiJs"/> backed by an
/// <see cref="IJSObjectReference"/> handle to the leafletInterop.js
/// module. Centralises the JSDisconnected / ObjectDisposed swallow
/// matching <see cref="MapAisJs"/>.
/// </summary>
public sealed class MapMarinePoiJs : IMapMarinePoiJs
{
    private readonly IJSObjectReference _module;
    private bool _disposed;

    public MapMarinePoiJs(IJSObjectReference module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    /// <summary>Mark the wrapper as disposed so subsequent calls
    /// silently no-op. Called by Map.razor's <c>DisposeAsync</c> path
    /// before it disposes the module reference.</summary>
    public void MarkDisposed() => _disposed = true;

    public Task SetMarinePoisAsync(object[] pois)
        => InvokeSafe("setMarinePois", (object)pois);

    public Task SetMarinePoisVisibleAsync(bool visible)
        => InvokeSafe("setMarinePoisVisible", visible);

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

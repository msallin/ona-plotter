using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// <see cref="IMapAnchorJs"/> backed by an
/// <see cref="IJSObjectReference"/> handle to the leafletInterop.js
/// module. Centralises the JSDisconnected / ObjectDisposed / JSException
/// tolerance that every call site previously re-implemented.
///
/// <para>Lifetime: created with the module reference once Map.razor
/// has loaded the JS module, and lives until the page disposes the
/// module. After dispose, every method becomes a silent no-op so
/// in-flight callers don't need their own try/catch wrappers.</para>
/// </summary>
public sealed class MapAnchorJs : IMapAnchorJs
{
    private readonly IJSObjectReference _module;
    private bool _disposed;

    public MapAnchorJs(IJSObjectReference module)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
    }

    /// <summary>Mark the wrapper as disposed so subsequent calls
    /// silently no-op. Called by Map.razor's <c>DisposeAsync</c>
    /// path before it disposes the module reference; without this,
    /// in-flight Sync* methods could otherwise still reach the
    /// already-disposed JS reference.</summary>
    public void MarkDisposed() => _disposed = true;

    public Task SetAnchorAsync(double lat, double lon, double radiusMeters)
        => InvokeSafe("setAnchor", lat, lon, radiusMeters);

    public Task ClearAnchorAsync()
        => InvokeSafe("clearAnchor");

    public Task SetAnchorRaisingAsync(bool raising)
        => InvokeSafe("setAnchorRaising", raising);

    public Task UpdateAnchorRadiusAsync(double radiusMeters)
        => InvokeSafe("updateAnchorRadius", radiusMeters);

    /// <summary>
    /// Single safe-call helper for every interop in this wrapper.
    /// Swallows the three "page is unmounting" exceptions that
    /// otherwise scatter try/catch blocks across every call site.
    /// JSException is rethrown so a real JS-side regression still
    /// surfaces (the wrapper is for lifecycle noise only, not for
    /// hiding bugs).
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

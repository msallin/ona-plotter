using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// Lifecycle-safe handle to a JS module. Once <see cref="MarkDisposed"/>
/// has run, or while the page is unmounting (<see cref="JSDisconnectedException"/>
/// / <see cref="ObjectDisposedException"/>), calls are silent no-ops so
/// in-flight callers need no try/catch of their own. Every <c>Map*Js</c>
/// wrapper composes one.
/// </summary>
public sealed class SafeJsModule
{
    private readonly IJSObjectReference _module;
    private readonly Action<string, JSException>? _onJsException;
    private bool _disposed;

    /// <param name="module">The imported JS module.</param>
    /// <param name="onJsException">Handler for a <see cref="JSException"/>
    /// thrown by a void call (a JS-side regression rather than lifecycle
    /// noise). When null the exception propagates.</param>
    public SafeJsModule(IJSObjectReference module, Action<string, JSException>? onJsException = null)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        _onJsException = onJsException;
    }

    /// <summary>Subsequent calls silently no-op. Called before the page
    /// disposes the module reference.</summary>
    public void MarkDisposed() => _disposed = true;

    public async Task InvokeVoidAsync(string identifier, params object?[] args)
    {
        if (_disposed) return;
        try
        {
            await _module.InvokeVoidAsync(identifier, args);
        }
        catch (JSDisconnectedException) { /* page is unmounting */ }
        catch (ObjectDisposedException) { /* JS module disposed first */ }
        catch (JSException ex) when (_onJsException is not null)
        {
            _onJsException(identifier, ex);
        }
    }

    /// <summary>Returns <paramref name="whenUnavailable"/> if the wrapper
    /// or the JS module is gone, so callers reading interop state during
    /// teardown get a defined fallback instead of an exception.</summary>
    public async Task<T> InvokeAsync<T>(T whenUnavailable, string identifier, params object?[] args)
    {
        if (_disposed) return whenUnavailable;
        try
        {
            return await _module.InvokeAsync<T>(identifier, args);
        }
        catch (JSDisconnectedException) { return whenUnavailable; }
        catch (ObjectDisposedException) { return whenUnavailable; }
    }
}

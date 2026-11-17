using Microsoft.JSInterop;

namespace OnaPlotter.Services.Js;

/// <summary>
/// Quiet interop for components that hold a raw
/// <see cref="IJSObjectReference"/>: a page that is tearing down
/// (<see cref="JSDisconnectedException"/>, <see cref="ObjectDisposedException"/>)
/// is not an error, and a null module (not imported yet) is a no-op.
/// </summary>
public static class JsModuleExtensions
{
    public static async ValueTask InvokeVoidQuietAsync(
        this IJSObjectReference? module, string identifier, params object?[] args)
    {
        if (module is null) return;
        try { await module.InvokeVoidAsync(identifier, args); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }

    public static async ValueTask DisposeQuietlyAsync(this IJSObjectReference? module)
    {
        if (module is null) return;
        try { await module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
    }
}

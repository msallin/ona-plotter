using Microsoft.JSInterop;

namespace OnaPlotter.Services;

/// <summary>
/// C# helper for the client-side error relay. The actual relay is
/// installed synchronously from <c>wwwroot/js/errorRelayBoot.js</c>
/// (loaded by index.html before the Blazor runtime), which captures
/// <c>window.error</c> + <c>unhandledrejection</c> + a
/// <c>console.error</c> wrapper + a banner observer. That bootstrap
/// exposes <c>window.__onaErrorRelay.report({ message, stack })</c>
/// for explicit relays from C# code.
///
/// This service is a thin wrapper around that JS function. Use it
/// from catch blocks where the exception was handled but is still
/// worth recording for later triage:
///
///   try { ... } catch (Exception ex) { await relay.ReportAsync("X failed", ex); }
///
/// The previous version of this file imported a sibling ES module
/// (<c>errorRelay.js</c>) that delegated back to the bootstrap; that
/// shim is gone - one less file, one less round-trip on cold start.
/// </summary>
public sealed class ClientErrorRelay
{
    private readonly IJSRuntime _js;

    public ClientErrorRelay(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>Manually relay a caught exception with a contextual
    /// message. Returns <c>true</c> if the JS bootstrap was reachable
    /// and the report was queued (network reachability is not
    /// guaranteed; the bootstrap silently swallows fetch failures by
    /// design). Used by Settings' "Send test log" button to gate the
    /// success toast on actually-queued reports rather than always-
    /// claim-success.</summary>
    public async Task<bool> ReportAsync(string message, Exception? ex = null)
    {
        try
        {
            await _js.InvokeVoidAsync("__onaErrorRelay.report", new
            {
                message,
                stack = ex?.ToString() ?? "",
            });
            return true;
        }
        catch
        {
            // Relay failures must never propagate - the relay exists
            // to surface OTHER errors, not generate its own.
            return false;
        }
    }
}

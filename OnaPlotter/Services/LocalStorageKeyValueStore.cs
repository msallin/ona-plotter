using Microsoft.JSInterop;

namespace OnaPlotter.Services;

/// <summary>
/// localStorage-backed key-value store. Keys are namespaced with "ona." to
/// avoid collisions with other apps on the same origin.
/// Storage access failures (private browsing, quota exceeded) surface as
/// <see cref="JSException"/> so the caller can decide whether to recover.
/// <see cref="JSDisconnectedException"/> is absorbed: it only fires while
/// the JS runtime is being torn down, when there is no storage left to
/// read from or write to, so reads report "unset" and writes are dropped
/// rather than surfacing in Blazor's error UI during a page teardown.
/// </summary>
public sealed class LocalStorageKeyValueStore : IKeyValueStore
{
    private const string Prefix = "ona.";
    private readonly IJSRuntime _js;

    public LocalStorageKeyValueStore(IJSRuntime js) => _js = js;

    public async ValueTask<string?> GetAsync(string key, CancellationToken ct = default)
    {
        try { return await _js.InvokeAsync<string?>("localStorage.getItem", ct, Prefix + key); }
        catch (JSDisconnectedException) { return null; }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", ct, Prefix + key, value); }
        catch (JSDisconnectedException) { }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        try { await _js.InvokeVoidAsync("localStorage.removeItem", ct, Prefix + key); }
        catch (JSDisconnectedException) { }
    }

    // Adapt ValueTask<string?> to Task<string?> for IKeyValueStore.
    Task<string?> IKeyValueStore.GetAsync(string key, CancellationToken ct) =>
        GetAsync(key, ct).AsTask();
}

using Microsoft.JSInterop;

namespace OnaPlotter.Services;

/// <summary>
/// localStorage-backed key-value store. Keys are namespaced with "ona." to
/// avoid collisions with other apps on the same origin.
/// Storage access failures (private browsing, quota exceeded) surface as
/// <see cref="JSException"/> so the caller can decide whether to recover.
/// </summary>
public sealed class LocalStorageKeyValueStore : IKeyValueStore
{
    private const string Prefix = "ona.";
    private readonly IJSRuntime _js;

    public LocalStorageKeyValueStore(IJSRuntime js) => _js = js;

    public ValueTask<string?> GetAsync(string key, CancellationToken ct = default) =>
        _js.InvokeAsync<string?>("localStorage.getItem", ct, Prefix + key);

    public async Task SetAsync(string key, string value, CancellationToken ct = default) =>
        await _js.InvokeVoidAsync("localStorage.setItem", ct, Prefix + key, value);

    public async Task RemoveAsync(string key, CancellationToken ct = default) =>
        await _js.InvokeVoidAsync("localStorage.removeItem", ct, Prefix + key);

    // Adapt ValueTask<string?> to Task<string?> for IKeyValueStore.
    Task<string?> IKeyValueStore.GetAsync(string key, CancellationToken ct) =>
        GetAsync(key, ct).AsTask();
}

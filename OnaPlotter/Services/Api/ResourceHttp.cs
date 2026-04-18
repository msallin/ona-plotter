using System.Net.Http.Json;

namespace OnaPlotter.Services.Api;

/// <summary>
/// Shared plumbing for SignalK <c>/resources/*</c> HTTP clients.
/// Routes, waypoints, notes, and regions all follow the same
/// dict-keyed-by-id shape on GET and the same URL-encoded DELETE; this
/// helper factors those two primitives so each resource API stays a
/// thin layer over its domain-specific concerns (payload shape,
/// geometry parsing) instead of re-implementing transport semantics.
///
/// Error handling matches every existing API: a non-success status
/// returns the empty-result sentinel (null dict / false for delete) so
/// callers can pass that through the SafeLoad wrapper unchanged.
/// </summary>
internal static class ResourceHttp
{
    /// <summary>
    /// GETs a SignalK resource collection and deserialises the
    /// returned map to <c>Dictionary&lt;string, T&gt;</c>. Null on any
    /// non-2xx or malformed body so callers can early-return with an
    /// empty list.
    /// </summary>
    public static async Task<Dictionary<string, T>?> GetDictAsync<T>(
        HttpClient http, string url, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<Dictionary<string, T>>(cancellationToken: ct);
    }

    /// <summary>
    /// DELETEs a specific resource by URL. Returns true on 2xx.
    /// </summary>
    public static async Task<bool> DeleteAsync(
        HttpClient http, string url, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync(url, ct);
        return response.IsSuccessStatusCode;
    }
}

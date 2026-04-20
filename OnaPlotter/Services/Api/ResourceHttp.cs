using System.Net.Http.Json;
using System.Text.Json;

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

    /// <summary>
    /// Parses the id of a just-created resource from the POST response
    /// body. SignalK servers use two historical shapes, picked per
    /// version/plugin combination, and our clients have to tolerate
    /// both:
    ///
    /// <list type="bullet">
    ///   <item><description>Bare JSON string: <c>"abc-123"</c> --
    ///     older SK core.</description></item>
    ///   <item><description>Status envelope: <c>{"state":"COMPLETED",
    ///     "statusCode":201,"id":"abc-123"}</c> -- newer SK / v2 REST.
    ///     </description></item>
    /// </list>
    ///
    /// The old approach of <c>body.Trim('"')</c> worked for the first
    /// shape but produced the ENTIRE envelope as an "id" for the second,
    /// which then blew up Delete (URL-encoded JSON in the path) and
    /// hid newly-saved resources from the Layers UI (the diff against
    /// prevIds matched nothing).
    /// </summary>
    public static string? ParseCreatedId(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
                return root.GetString();
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("id", out var idEl) &&
                idEl.ValueKind == JsonValueKind.String)
                return idEl.GetString();
        }
        catch (JsonException)
        {
            // Fall through to bare-string trim below.
        }
        return body.Trim().Trim('"');
    }
}

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
    /// <summary>SignalK uses camelCase JSON property names; our DTO
    /// records are Pascal-case (no [JsonPropertyName] attributes
    /// for terseness). Default System.Text.Json options are
    /// case-sensitive in .NET 10, so the deserialiser silently
    /// dropped every camelCased value onto the floor and the
    /// caller saw `default` for every property. Helm regression:
    /// the MOB recovery on reload returned an envelope where
    /// every State / Position / CreatedAt was null, so the
    /// `if (dto.State is null) continue;` guard skipped every
    /// entry. Setting PropertyNameCaseInsensitive once here
    /// covers every GetDict caller without per-DTO attribute
    /// churn; existing [JsonPropertyName] attributes still take
    /// priority where they exist.</summary>
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// GETs a SignalK resource collection and deserialises the
    /// returned map to <c>Dictionary&lt;string, T&gt;</c>. Null on any
    /// non-2xx or malformed body so callers can early-return with an
    /// empty list.
    /// </summary>
    public static async Task<Dictionary<string, T>?> GetDictAsync<T>(
        HttpClient http, string url, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<Dictionary<string, T>>(s_jsonOptions, ct);
        }
        catch (HttpRequestException) { return null; }
        // Caller-initiated cancel still rethrows; the HttpClient timeout
        // (no caller CT) returns the empty-result sentinel so the
        // calling SafeLoad path doesn't have to know about transport
        // failure modes.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    /// <summary>
    /// DELETEs a specific resource by URL. Returns an
    /// <see cref="ApiResult"/> so callers can surface the server's
    /// error body on failure; happy path is the singleton
    /// <see cref="ApiResult.Ok"/>.
    /// </summary>
    public static async Task<ApiResult> DeleteAsync(
        HttpClient http, string url, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.DeleteAsync(url, ct);
            if (response.IsSuccessStatusCode) return ApiResult.Ok;
            return ApiResult.Fail(await ReadErrorAsync(response, ct)
                ?? $"HTTP {(int)response.StatusCode}");
        }
        catch (HttpRequestException ex) { return ApiResult.Fail(ex.Message); }
        // Caller-initiated cancellation rethrows so a deliberate cancel
        // surfaces as cancel; HttpClient's internal timeout (the
        // 8-second default; ct is never user-driven for fire-and-forget
        // callers like AutoAdvanceAsync) lands here as a Fail rather
        // than bubbling up to Blazor's renderer error UI.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return ApiResult.Fail("request timed out"); }
    }

    /// <summary>
    /// POSTs a JSON body. Used for every resource-create flow that
    /// doesn't need the generated id; <see cref="PostCreateAsync"/>
    /// is the id-returning variant used by
    /// Waypoint/Note/Region creates.
    /// </summary>
    public static async Task<ApiResult> PostAsync(
        HttpClient http, string url, object body, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(url, body, ct);
            if (response.IsSuccessStatusCode) return ApiResult.Ok;
            return ApiResult.Fail(
                await ReadErrorAsync(response, ct) ?? $"HTTP {(int)response.StatusCode}",
                (int)response.StatusCode);
        }
        catch (HttpRequestException ex) { return ApiResult.Fail(ex.Message); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return ApiResult.Fail("request timed out"); }
    }

    /// <summary>
    /// PUTs a JSON body. Used for in-place update flows
    /// (RouteApi.UpdateAsync, WaypointApi.UpdateAsync, radar control
    /// writes go through their own helper for the structured error
    /// envelope they emit).
    /// </summary>
    public static async Task<ApiResult> PutAsync(
        HttpClient http, string url, object body, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PutAsJsonAsync(url, body, ct);
            if (response.IsSuccessStatusCode) return ApiResult.Ok;
            return ApiResult.Fail(
                await ReadErrorAsync(response, ct) ?? $"HTTP {(int)response.StatusCode}",
                (int)response.StatusCode);
        }
        catch (HttpRequestException ex) { return ApiResult.Fail(ex.Message); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return ApiResult.Fail("request timed out"); }
    }

    /// <summary>
    /// POSTs a resource-create body and returns the new resource id
    /// in <c>Value</c>. Handles both SK response shapes via
    /// <see cref="ParseCreatedId"/>; a 2xx response with an
    /// unparseable body is a failure because the caller has no id to
    /// do anything with.
    /// </summary>
    public static async Task<ApiResult<string>> PostCreateAsync(
        HttpClient http, string url, object body, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(url, body, ct);
            if (!response.IsSuccessStatusCode)
            {
                return ApiResult<string>.Fail(await ReadErrorAsync(response, ct)
                    ?? $"HTTP {(int)response.StatusCode}");
            }
            var rawBody = await response.Content.ReadAsStringAsync(ct);
            var id = ParseCreatedId(rawBody);
            if (string.IsNullOrEmpty(id))
                return ApiResult<string>.Fail("server accepted the create but returned no id");
            return ApiResult<string>.Ok(id);
        }
        catch (HttpRequestException ex) { return ApiResult<string>.Fail(ex.Message); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return ApiResult<string>.Fail("request timed out"); }
    }

    /// <summary>
    /// Extracts the server's error string from a non-2xx body. SK
    /// v2 uses the <c>{"error": "..."}</c> envelope (also present
    /// on radar-control rejections); older endpoints send a bare
    /// text body. Both are tolerated; null when nothing readable.
    /// </summary>
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("error", out var e) &&
                    e.ValueKind == JsonValueKind.String)
                {
                    return e.GetString();
                }
            }
            catch (JsonException)
            {
                // Non-JSON body - trim and cap to keep a wild 4k
                // HTML error page from blowing a toast popover.
                return body.Length > 200 ? body[..200] : body;
            }
        }
        // Narrow to the genuine network / IO failure modes. A bare
        // catch silenced future bugs (typo on the json path, a
        // ReadAsStringAsync regression) and obscured ops debugging
        // when "HTTP 502" toasts hid an unrelated failure.
        catch (System.Net.Http.HttpRequestException ex)
        {
            Console.WriteLine($"[resourceHttp] body read failed (network): {ex.Message}");
        }
        catch (System.IO.IOException ex)
        {
            Console.WriteLine($"[resourceHttp] body read failed (io): {ex.Message}");
        }
        catch (OperationCanceledException) { /* request cancelled */ }
        return null;
    }

    /// <summary>
    /// Parses the id of a just-created resource from the POST response
    /// body. SignalK servers use two historical shapes, picked per
    /// version/plugin combination, and our clients have to tolerate
    /// both:
    ///
    /// <list type="bullet">
    ///   <item><description>Bare JSON string: <c>"abc-123"</c> -
    ///     older SK core.</description></item>
    ///   <item><description>Status envelope: <c>{"state":"COMPLETED",
    ///     "statusCode":201,"id":"abc-123"}</c> - newer SK / v2 REST.
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

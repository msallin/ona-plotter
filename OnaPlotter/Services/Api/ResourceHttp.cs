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
/// Every request goes through one failure policy: a non-success
/// status, transport failure or HttpClient timeout becomes a failed
/// <see cref="ApiResult"/> / <see cref="ApiResult{T}"/> (or the null
/// sentinel of <see cref="GetDictAsync{T}"/>), never an exception into
/// Blazor's renderer. Only a caller-initiated cancellation propagates.
/// </summary>
internal static class ResourceHttp
{
    /// <summary>SignalK uses camelCase JSON property names; our DTO
    /// records are Pascal-case (no [JsonPropertyName] attributes
    /// for terseness). Default System.Text.Json options are
    /// case-sensitive in .NET 10, so the deserialiser would silently
    /// drop every camelCased value and the caller would see
    /// `default` for every property (MOB recovery on reload, for
    /// example, would get a null State on every entry and skip
    /// them all). Setting PropertyNameCaseInsensitive once here
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
    /// failure (non-2xx, transport error, timeout, malformed body) so
    /// callers can early-return with an empty list. Failure policy is
    /// <see cref="GetJsonAsync{T}(HttpClient, string, ILogger, CancellationToken, JsonSerializerOptions?)"/>'s.
    /// </summary>
    public static async Task<Dictionary<string, T>?> GetDictAsync<T>(
        HttpClient http, string url, ILogger logger, CancellationToken ct = default)
    {
        var result = await GetJsonAsync<Dictionary<string, T>>(http, url, logger, ct);
        return result.Success ? result.Value : null;
    }

    /// <summary>
    /// GETs <paramref name="url"/> and deserialises the body to
    /// <typeparamref name="T"/>. The shared read policy for every
    /// SignalK client:
    /// <list type="bullet">
    ///   <item>2xx: <c>Ok(value)</c> (value may be null for a JSON
    ///   <c>null</c> body).</item>
    ///   <item>non-2xx: <c>Fail</c> carrying the server's error text
    ///   and <see cref="ApiResult{T}.StatusCode"/>; not logged, since
    ///   404 is how optional plugins say "not installed".</item>
    ///   <item>transport failure, HttpClient timeout, malformed JSON:
    ///   <c>Fail</c> without status code, logged as a warning.</item>
    ///   <item>caller-initiated cancellation: rethrown, so a deliberate
    ///   cancel surfaces as a cancel.</item>
    /// </list>
    /// Defaults to case-insensitive property matching; pass
    /// <paramref name="options"/> to override (e.g.
    /// <see cref="JsonSerializerOptions.Web"/> for DTOs that rely on the
    /// <c>System.Net.Http.Json</c> defaults).
    /// </summary>
    public static Task<ApiResult<T>> GetJsonAsync<T>(
        HttpClient http, string url, ILogger logger, CancellationToken ct = default,
        JsonSerializerOptions? options = null)
    {
        return GetCoreAsync(http, url, logger,
            static async (content, opts, token) =>
                await content.ReadFromJsonAsync<T>(opts, token),
            options ?? s_jsonOptions, ct);
    }

    /// <summary>
    /// Same policy as <see cref="GetJsonAsync{T}(HttpClient, string, ILogger, CancellationToken, JsonSerializerOptions?)"/>
    /// for bodies whose shape needs hand parsing (multi-shape payloads,
    /// positional history rows). <paramref name="parse"/> runs against
    /// the root element of a document that is disposed afterwards, so
    /// it must not let the element escape. A <see cref="JsonException"/>
    /// thrown by <paramref name="parse"/> is treated as a malformed body.
    /// </summary>
    public static Task<ApiResult<T>> GetJsonAsync<T>(
        HttpClient http, string url, ILogger logger, Func<JsonElement, T> parse,
        CancellationToken ct = default)
    {
        return GetCoreAsync(http, url, logger,
            static async (content, parser, token) =>
            {
                await using var stream = await content.ReadAsStreamAsync(token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                return parser(doc.RootElement);
            },
            parse, ct);
    }

    /// <summary>
    /// Same policy as <see cref="GetJsonAsync{T}(HttpClient, string, ILogger, CancellationToken, JsonSerializerOptions?)"/>
    /// for endpoints whose body is not reliably JSON (e.g.
    /// <c>/signalk/v1/api/self</c>, which some servers answer with a
    /// bare, unquoted URN). Returns the raw body text.
    /// </summary>
    public static Task<ApiResult<string>> GetTextAsync(
        HttpClient http, string url, ILogger logger, CancellationToken ct = default)
    {
        return GetCoreAsync<string, object?>(http, url, logger,
            static async (content, _, token) => await content.ReadAsStringAsync(token),
            null, ct);
    }

    private static async Task<ApiResult<T>> GetCoreAsync<T, TState>(
        HttpClient http, string url, ILogger logger,
        Func<HttpContent, TState, CancellationToken, Task<T?>> read, TState state,
        CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                return ApiResult<T>.Fail(
                    await ReadErrorAsync(response, ct) ?? $"HTTP {status}", status);
            }
            var value = await read(response.Content, state, ct);
            return new ApiResult<T>(true, value);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("GET {Url} failed: {Message}", url, ex.Message);
            return ApiResult<T>.Fail(ex.Message);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("GET {Url} returned a malformed body: {Message}", url, ex.Message);
            return ApiResult<T>.Fail("malformed response");
        }
        // HttpClient's own timeout (no caller cancel) is a failure the
        // UI can render; a caller cancel propagates.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("GET {Url} timed out", url);
            return ApiResult<T>.Fail("request timed out");
        }
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
            // Pass StatusCode through (matches PutAsync / PostAsync) so
            // retry loops can treat 404 as "already gone, success" rather
            // than spinning forever on a notification or waypoint the
            // server has already cleaned up.
            return ApiResult.Fail(
                await ReadErrorAsync(response, ct) ?? $"HTTP {(int)response.StatusCode}",
                (int)response.StatusCode);
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
    /// writes). <paramref name="options"/> overrides the
    /// <c>System.Net.Http.Json</c> web defaults for bodies that need
    /// e.g. null-omission.
    /// </summary>
    public static async Task<ApiResult> PutAsync(
        HttpClient http, string url, object body, CancellationToken ct = default,
        JsonSerializerOptions? options = null)
    {
        try
        {
            using var response = options is null
                ? await http.PutAsJsonAsync(url, body, ct)
                : await http.PutAsync(url, JsonContent.Create(body, body.GetType(), options: options), ct);
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
    /// Extracts the server's error string from a non-2xx body. Two
    /// JSON shapes tolerated:
    /// <list type="bullet">
    ///   <item><c>{"error": "..."}</c> - SK v2 radar-control rejections
    ///   and older custom endpoints.</item>
    ///   <item><c>{"state": "FAILED", "statusCode": ..., "message": "..."}</c>
    ///   - the SK v2 standard error envelope used by the notifications,
    ///   resources, and course routers. Without this branch the helm
    ///   sees a bare "HTTP 400" in the log when the server already
    ///   spelled the failure out (e.g. "Alarm already acknowledged!").</item>
    /// </list>
    /// Bare text bodies are accepted as-is. Null when nothing readable.
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
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("error", out var e) &&
                        e.ValueKind == JsonValueKind.String)
                    {
                        return e.GetString();
                    }
                    // SK v2 standard error envelope: try `message`
                    // when `error` isn't present.
                    if (doc.RootElement.TryGetProperty("message", out var m) &&
                        m.ValueKind == JsonValueKind.String)
                    {
                        return m.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // Non-JSON body - trim and cap to keep a wild 4k
                // HTML error page from blowing a toast popover.
                return body.Length > 200 ? body[..200] : body;
            }
        }
        // Narrow to the genuine network / IO failure modes so an
        // unrelated bug still surfaces. The caller already has the
        // status code, which becomes the error text when the body
        // can't be read.
        catch (HttpRequestException) { }
        catch (System.IO.IOException) { }
        catch (OperationCanceledException) { }
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
    /// A plain <c>body.Trim('"')</c> handles the first shape but yields
    /// the ENTIRE envelope as an "id" for the second, which breaks
    /// Delete (URL-encoded JSON in the path) and hides newly-saved
    /// resources from the Layers UI (the diff against prevIds matches
    /// nothing).
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

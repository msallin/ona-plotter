namespace OnaPlotter.Services.Api;

/// <summary>
/// Concrete client for SignalK v2 notifications. Fire-and-forget
/// writes; the success state of the request matters less than the
/// delta the server emits in response (every active subscriber sees
/// the new <c>status.acknowledged</c> within a tick, and that's what
/// drives the banner state).
/// <para>
/// Every call applies a per-call 5 s timeout via a linked
/// <see cref="CancellationTokenSource"/>. HttpClient's default 100 s
/// is far too long for an alarm-attention path - a half-baked TLS
/// handshake or RST on a flaky helm Wi-Fi would otherwise stall the
/// single-threaded WASM dispatcher up to 100 s, queueing every
/// subsequent OnAlarmsChanged behind the dead socket. 5 s is the
/// engineering judgement: tight enough that helm gestures still feel
/// fluid, generous enough to absorb a normal LAN round-trip plus a
/// retry on a marginal connection.
/// </para>
/// </summary>
public sealed class NotificationsApi : INotificationsApi
{
    /// <summary>Per-call timeout applied to every SignalK v2
    /// notification REST call. See class summary for sizing rationale.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public NotificationsApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    /// <inheritdoc/>
    public async Task<ApiResult> AcknowledgeAsync(string notificationId, CancellationToken ct = default)
    {
        // Server expects a body even for a no-arg action; an empty
        // object is the documented shape and is what every other v2
        // POST endpoint accepts. ResourceHttp.PostAsync sends it as
        // application/json so the server's content-type check passes.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        try
        {
            return await ResourceHttp.PostAsync(_http,
                _baseUrl.Combine(SignalKUrls.NotificationAcknowledge(notificationId)),
                new { }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TimedOutResult();
        }
    }

    /// <inheritdoc/>
    public async Task<ApiResult> SilenceAsync(string notificationId, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        try
        {
            return await ResourceHttp.PostAsync(_http,
                _baseUrl.Combine(SignalKUrls.NotificationSilence(notificationId)),
                new { }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TimedOutResult();
        }
    }

    /// <inheritdoc/>
    public async Task<ApiResult<string>> RaiseAsync(string path, NotificationPayload body, CancellationToken ct = default)
    {
        // SignalK v2 raise: POST /notifications with { path, value }.
        // Server derives the id from (context, path, $source) so
        // re-raising the same path overlays rather than duplicating
        // (the cross-plotter sync property the publisher relies on).
        // PostCreateAsync handles the {id} response shape; if a
        // future server change drops the id from the response we get
        // a clean Fail rather than a half-applied state.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        var envelope = new { path, value = body };
        try
        {
            return await ResourceHttp.PostCreateAsync(_http,
                _baseUrl.Combine(SignalKUrls.NotificationsPath),
                envelope, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TimedOutValueResult<string>();
        }
    }

    /// <inheritdoc/>
    public async Task<ApiResult> ClearAsync(string notificationId, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        try
        {
            return await ResourceHttp.DeleteAsync(_http,
                _baseUrl.Combine(SignalKUrls.NotificationById(notificationId)),
                cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TimedOutResult();
        }
    }

    /// <inheritdoc/>
    public async Task<ApiResult<string>> RaiseMobAsync(string? message, CancellationToken ct = default)
    {
        // Body shape: empty when message is null, otherwise { message }.
        // The server still expects an application/json content-type so
        // we always send an object literal.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        object body = message is null ? new { } : new { message };
        try
        {
            return await ResourceHttp.PostCreateAsync(_http,
                _baseUrl.Combine(SignalKUrls.NotificationMobRaise),
                body, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TimedOutValueResult<string>();
        }
    }

    // The per-call linked CTS makes ResourceHttp's own
    // `when (!ct.IsCancellationRequested)` filter blind to the timeout
    // case: the linked token IS cancelled when CallTimeout fires, so
    // ResourceHttp's catch is skipped and the OperationCanceledException
    // propagates instead of returning ApiResult.Fail. The wrapper here
    // catches it again with the OUTER ct as the discriminator: if the
    // caller didn't ask to cancel, this is the per-call timeout and we
    // surface it as a structured Fail (so MOB retry loops, alarm
    // publish flows, and Dismiss-All paths see a clean result instead
    // of a faulted Task).
    private static ApiResult TimedOutResult() =>
        ApiResult.Fail($"request timed out after {(int)CallTimeout.TotalSeconds}s");

    private static ApiResult<T> TimedOutValueResult<T>() =>
        ApiResult<T>.Fail($"request timed out after {(int)CallTimeout.TotalSeconds}s");

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, ServerNotificationEnvelope>?> ListActiveAsync(CancellationToken ct = default)
    {
        // Uses the same per-call timeout as the action verbs; a slow
        // GET on the boot path shouldn't stall the alarm pipeline.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        try
        {
            return await ResourceHttp.GetDictAsync<ServerNotificationEnvelope>(_http,
                _baseUrl.Combine(SignalKUrls.NotificationsPath),
                cts.Token).ConfigureAwait(false);
        }
        catch (System.Net.Http.HttpRequestException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
        catch (OperationCanceledException) { return null; }
    }
}

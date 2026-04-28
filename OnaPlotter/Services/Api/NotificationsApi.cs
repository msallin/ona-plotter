namespace OnaPlotter.Services.Api;

/// <summary>
/// Concrete client for SignalK v2 notifications. Fire-and-forget
/// writes; the success state of the request matters less than the
/// delta the server emits in response (every active subscriber sees
/// the new <c>status.acknowledged</c> within a tick, and that's what
/// drives the banner state).
/// </summary>
public sealed class NotificationsApi : INotificationsApi
{
    private readonly HttpClient _http;
    private readonly ISignalKBaseUrl _baseUrl;

    public NotificationsApi(HttpClient http, ISignalKBaseUrl baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl;
    }

    /// <inheritdoc/>
    public Task<ApiResult> AcknowledgeAsync(string notificationId, CancellationToken ct = default) =>
        // Server expects a body even for a no-arg action; an empty
        // object is the documented shape and is what every other v2
        // POST endpoint accepts. ResourceHttp.PostAsync sends it as
        // application/json so the server's content-type check passes.
        ResourceHttp.PostAsync(_http,
            _baseUrl.Combine(SignalKUrls.NotificationAcknowledge(notificationId)),
            new { }, ct);

    /// <inheritdoc/>
    public Task<ApiResult> SilenceAsync(string notificationId, CancellationToken ct = default) =>
        ResourceHttp.PostAsync(_http,
            _baseUrl.Combine(SignalKUrls.NotificationSilence(notificationId)),
            new { }, ct);

    /// <inheritdoc/>
    public Task<ApiResult<string>> RaiseAsync(string path, NotificationPayload body, CancellationToken ct = default)
    {
        // SignalK v2 raise: POST /notifications with { path, value }.
        // Server derives the id from (context, path, $source) so
        // re-raising the same path overlays rather than duplicating
        // (the cross-plotter sync property the publisher relies on).
        // PostCreateAsync handles the {id} response shape; if a
        // future server change drops the id from the response we get
        // a clean Fail rather than a half-applied state.
        var envelope = new { path, value = body };
        return ResourceHttp.PostCreateAsync(_http,
            _baseUrl.Combine(SignalKUrls.NotificationsPath),
            envelope, ct);
    }

    /// <inheritdoc/>
    public Task<ApiResult> ClearAsync(string notificationId, CancellationToken ct = default) =>
        ResourceHttp.DeleteAsync(_http,
            _baseUrl.Combine(SignalKUrls.NotificationById(notificationId)),
            ct);
}

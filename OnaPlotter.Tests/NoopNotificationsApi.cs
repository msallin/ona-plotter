using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// <see cref="INotificationsApi"/> that accepts every call and records
/// nothing. For tests that need the dependency satisfied but don't
/// observe the server round-trip (bridge-rule wiring, alarm-driver
/// fixtures). Tests that assert on the wire shape use
/// <see cref="NotificationsApi"/> over <see cref="ApiTestHelpers.MockClient"/>
/// instead.
/// </summary>
internal sealed class NoopNotificationsApi : INotificationsApi
{
    public static readonly NoopNotificationsApi Instance = new();

    private NoopNotificationsApi() { }

    public Task<ApiResult> AcknowledgeAsync(string notificationId, CancellationToken ct = default) =>
        Task.FromResult(ApiResult.Ok);

    public Task<ApiResult> SilenceAsync(string notificationId, CancellationToken ct = default) =>
        Task.FromResult(ApiResult.Ok);

    public Task<ApiResult<string>> RaiseAsync(string path, NotificationPayload body, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<string>.Ok("noop-id"));

    public Task<ApiResult> ClearAsync(string notificationId, CancellationToken ct = default) =>
        Task.FromResult(ApiResult.Ok);

    public Task<ApiResult<string>> RaiseMobAsync(string? message, CancellationToken ct = default) =>
        Task.FromResult(ApiResult<string>.Ok("noop-mob-id"));

    public Task<IReadOnlyDictionary<string, ServerNotificationEnvelope>?> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ServerNotificationEnvelope>?>(
            new Dictionary<string, ServerNotificationEnvelope>());
}

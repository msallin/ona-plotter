namespace OnaPlotter.Services.Mob;

/// <summary>
/// Local-first MOB pipeline. Provides immediate visual + audible
/// confirmation when the helm taps the MOB button -- the alarm
/// banner, chart marker and chime fire on the synthetic local
/// notification while the REST POST happens in the background and
/// retries until the server confirms. The server's WS echo
/// reconciles in place, never producing a duplicate.
/// <para>
/// Contract for callers (Map.razor + the alarm banner buttons):
/// <list type="bullet">
///   <item><see cref="RaiseAsync"/> on a new MOB.</item>
///   <item><see cref="AcknowledgeAsync"/> when the helm taps Ack
///   on a MOB banner. Idempotent.</item>
///   <item><see cref="ClearAsync"/> when the helm taps Clear.
///   Idempotent.</item>
///   <item><see cref="InitializeAsync"/> at startup -- replays the
///   pending-raise queue from localStorage and pulls the active
///   notification list from the server so any MOB raised pre-
///   reload is recovered.</item>
/// </list>
/// </para>
/// </summary>
public interface IMobService
{
    /// <summary>Raise a MOB. Synthesises a local notification
    /// matching the SK v2 wire shape and queues a background POST
    /// to <c>/signalk/v2/api/notifications/mob</c>. The server's
    /// WS echo reconciles in place via <see cref="OnServerEcho"/>.
    /// Local-first: returns immediately after the local entry is
    /// in place; the helm sees the alarm + chart marker even when
    /// the network is down.</summary>
    /// <param name="message">Helm-readable banner copy. Defaults
    /// to "Person Overboard!" when null.</param>
    /// <param name="latitude">Helm-side fix at trigger time. Null
    /// when the GPS hasn't reported yet -- the synthetic entry
    /// goes out without a position.</param>
    /// <param name="longitude">Companion to <paramref name="latitude"/>.</param>
    /// <returns>The local UUID. Used by tests + diagnostics.</returns>
    Task<string> RaiseAsync(string? message, double? latitude, double? longitude, CancellationToken ct = default);

    /// <summary>Acknowledge a MOB by id. Idempotent: a second
    /// acknowledge on the same id is a no-op. The server's WS
    /// echo carries the authoritative <c>status.acknowledged</c>
    /// flag back; local state flips optimistically and rolls back
    /// if the POST fails.</summary>
    Task<bool> AcknowledgeAsync(string serverId, CancellationToken ct = default);

    /// <summary>Clear a MOB by id. Idempotent.</summary>
    Task<bool> ClearAsync(string serverId, CancellationToken ct = default);

    /// <summary>Replays any pending POSTs from the persisted queue
    /// (offline-emit recovery) and pulls the server's active
    /// notification list (cross-reload recovery). Called once after
    /// SignalkClient declares the connection up. Safe to call
    /// repeatedly: pending posts ride their existing backoff and
    /// the list call is just a refresh of the server-known set.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Notify the service that a notification delta
    /// arrived on the WS. Distinct from the generic store.Apply
    /// path because MobService also wants to drop the synthetic
    /// local entry once the server's echo lands.</summary>
    void OnServerEcho(string path, string? serverId);
}

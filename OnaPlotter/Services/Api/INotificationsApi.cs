namespace OnaPlotter.Services.Api;

/// <summary>
/// Thin client for the SignalK v2 notifications REST surface
/// (<see cref="SignalKUrls.NotificationsPath"/>). Available on
/// signalk-server ≥ 2.21.0; older servers respond 404 to every call
/// and the plotter falls back to client-side dismiss only.
/// <para>
/// Three flows exposed:
/// </para>
/// <list type="number">
///   <item>
///     <term>Acknowledge</term>
///     <description>Mark a notification "I've seen this" -- server
///     re-emits the delta with <c>status.acknowledged = true</c>, every
///     connected plotter drops its banner. Cross-plotter alarm sync
///     hangs off this verb.</description>
///   </item>
///   <item>
///     <term>Silence</term>
///     <description>Hush the audible portion only; the visual banner
///     stays. Maps onto helm-style "I hear it, stop the klaxon".</description>
///   </item>
///   <item>
///     <term>Raise / Clear (Phase B)</term>
///     <description>Publish a client-computed alarm (CPA, wind shift,
///     shallow) into the server's notification space so other plotters
///     see it. Not yet wired -- declared on the interface so Phase B
///     doesn't need a breaking-change PR.</description>
///   </item>
/// </list>
/// </summary>
public interface INotificationsApi
{
    /// <summary>Mark a notification acknowledged. Server clears the
    /// audible flags but keeps the visual; the entry stays in the
    /// active set until <see cref="ClearAsync"/> is called or the
    /// underlying condition resolves. Idempotent on the server side.</summary>
    Task<ApiResult> AcknowledgeAsync(string notificationId, CancellationToken ct = default);

    /// <summary>Silence a notification (audible only). Visual banner
    /// stays; helm still sees it.</summary>
    Task<ApiResult> SilenceAsync(string notificationId, CancellationToken ct = default);

    /// <summary>Raise a new server-side notification at the given
    /// path. Server assigns a UUID derived from
    /// <c>(context, path, $source)</c>, so re-raising the same path +
    /// $source overlays the existing entry rather than duplicating.
    /// Returns the server-assigned id on success (used by the
    /// publisher to track "I raised this" for later
    /// <see cref="ClearAsync"/>).</summary>
    Task<ApiResult<string>> RaiseAsync(string path, NotificationPayload body, CancellationToken ct = default);

    /// <summary>Clear a server notification by id (state ->
    /// <c>normal</c>; server GCs after 60 s). Used by the publisher
    /// when the local rule that raised a notification stops firing.</summary>
    Task<ApiResult> ClearAsync(string notificationId, CancellationToken ct = default);

    /// <summary>Raise a Man Overboard safety alarm via the dedicated
    /// <c>POST /signalk/v2/api/notifications/mob</c> endpoint.
    /// The server generates the UUID; clients cannot inject their
    /// own. Returns the server-issued id on success so the local
    /// pending entry can reconcile against the WS echo.
    /// <para>The action is distinct from <see cref="RaiseAsync"/>
    /// (the path-keyed publisher used by client-side rules):
    /// <c>/notifications/mob</c> emits a server-side
    /// <c>state: "emergency"</c> notification with
    /// <c>method: ["visual","sound"]</c> and the helm's current
    /// position attached, all without the client having to wrangle
    /// path / state / method.</para></summary>
    Task<ApiResult<string>> RaiseMobAsync(string? message, CancellationToken ct = default);

    /// <summary>GET <c>/signalk/v2/api/notifications</c>. Returns
    /// the active notification map keyed by id, or null on transport
    /// failure / 4xx / 5xx. Used at SignalkClient connect-time so
    /// any MOB raised before the WS subscription was up still lands
    /// on the local store. The envelope shape mirrors what
    /// signalk-server actually returns from GET /notifications:
    /// each entry is a delta-style wrapper { context, path, value }
    /// where the notification payload sits under value, NOT at the
    /// top level. The earlier flat-shape DTO silently produced
    /// every field as null, MobService skipped every entry, and
    /// the alarm vanished on reload because REST recovery did
    /// nothing.</summary>
    Task<IReadOnlyDictionary<string, ServerNotificationEnvelope>?> ListActiveAsync(CancellationToken ct = default);
}

/// <summary>Envelope SignalK wraps each /notifications entry in:
/// <c>{ context, path, value: { state, message, status, position,
/// createdAt, id } }</c>. Match the actual wire shape so the
/// deserialiser doesn't drop the whole payload onto the floor.</summary>
public sealed record ServerNotificationEnvelope(
    string? Context,
    string? Path,
    ServerNotificationDto? Value);

/// <summary>
/// Wire shape for a single notification entry in the list-active
/// response. Mirrors the WS-delta value block (state / method /
/// message / id / status / position / createdAt). Only the fields
/// the MOB pipeline actually consumes are typed; extra fields the
/// server may add stay un-bound.
/// </summary>
public sealed record ServerNotificationDto(
    string? Id,
    string? State,
    string? Message,
    string[]? Method,
    NotificationStatusDto? Status,
    NotificationPositionDto? Position,
    DateTime? CreatedAt);

public sealed record NotificationStatusDto(
    bool Silenced,
    bool Acknowledged,
    bool CanSilence,
    bool CanAcknowledge,
    bool CanClear);

public sealed record NotificationPositionDto(
    double Latitude,
    double Longitude);

/// <summary>
/// JSON shape for <see cref="INotificationsApi.RaiseAsync"/>. Mirrors
/// the SignalK v2 wire payload so we don't need a separate DTO layer
/// -- camelCase serialisation is the default for Blazor JsonOptions.
/// </summary>
/// <param name="State">SignalK state vocab: <c>emergency</c> /
/// <c>alarm</c> / <c>warn</c> / <c>alert</c>. Lowercase. The server
/// maps these to severity classes server-side; the plotter's own
/// MapSeverity uses the same buckets, so a notification round-tripped
/// through the server lands in the same banner colour.</param>
/// <param name="Method">Delivery channels: <c>visual</c> and / or
/// <c>sound</c>. Empty array = silent visual only.</param>
/// <param name="Message">Helm-readable message body. Long enough to
/// say what's wrong, short enough to fit in a banner.</param>
public sealed record NotificationPayload(
    string State,
    IReadOnlyList<string> Method,
    string Message);

namespace OnaPlotter.Services.ServerNotifications;

/// <summary>
/// SignalK v2 notification status flags. Server-side state that the
/// helm can drive via the v2 REST API
/// (<see cref="OnaPlotter.Services.Api.INotificationsApi"/>); we read
/// these from each delta's <c>value.status</c> when present.
/// <para>
/// All five fields are server-controlled. <c>Acknowledged</c> means
/// "some plotter (or the server) marked this seen", and is global -
/// every connected client sees the same value, which is the v2
/// behaviour OnaPlotter relies on for cross-plotter alarm sync.
/// </para>
/// </summary>
/// <remarks>
/// Older SK servers (&lt; 2.21) don't emit a status block; the parser
/// stores null in that case and the UI falls back to client-side
/// dismiss only.
/// </remarks>
public sealed record NotificationStatus(
    bool Silenced,
    bool Acknowledged,
    bool CanSilence,
    bool CanAcknowledge,
    bool CanClear);

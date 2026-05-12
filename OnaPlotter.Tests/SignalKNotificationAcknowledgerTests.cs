using OnaPlotter.Services.Alarms;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Focused tests for the SK v2 acknowledger's benign-rejection
/// classifier. The transport plumbing lives in
/// <see cref="NotificationsApiTests"/>; this file pins the
/// "duplicate ack is not an error" UX contract the helm relies on
/// for the multi-plotter alarm-sync race.
/// </summary>
public class SignalKNotificationAcknowledgerTests
{
    [Test]
    public async Task IsBenignAckRejection_RecognisesAlreadyAcknowledged()
    {
        // The signalk-server message verbatim from alarm.ts -
        // throw new Error('Alarm already acknowledged!'). Server
        // wraps it as 400 + {state, statusCode, message}; the
        // helper pattern-matches on the message text after
        // ReadErrorAsync has extracted it.
        var r = ApiResult.Fail("Alarm already acknowledged!", 400);
        await Assert.That(SignalKNotificationAcknowledger.IsBenignAckRejection(r)).IsTrue();
    }

    [Test]
    public async Task IsBenignAckRejection_RecognisesCannotBeAcknowledged()
    {
        // Second 400 shape - notification.canAcknowledge is false
        // (server emitted the alarm with that flag off). Treated
        // the same way: the helm's local dismiss already took
        // effect and the server insists nothing to do server-side.
        var r = ApiResult.Fail("Alarm cannot be acknowledged!", 400);
        await Assert.That(SignalKNotificationAcknowledger.IsBenignAckRejection(r)).IsTrue();
    }

    [Test]
    public async Task IsBenignAckRejection_CaseInsensitive()
    {
        // Defensive: a future server change that lowercases the
        // message wording shouldn't make this helper miss the
        // pattern. The substring check is OrdinalIgnoreCase.
        var r = ApiResult.Fail("alarm ALREADY acknowledged!", 400);
        await Assert.That(SignalKNotificationAcknowledger.IsBenignAckRejection(r)).IsTrue();
    }

    [Test]
    public async Task IsBenignAckRejection_Rejects_OtherFailures()
    {
        // Anything that isn't the documented duplicate-ack shape
        // must surface as a real error so the helm's SK log isn't
        // hiding new failure modes behind the benign-rejection
        // softening.
        await Assert.That(SignalKNotificationAcknowledger.IsBenignAckRejection(
            ApiResult.Fail("server timed out", 504))).IsFalse();
        await Assert.That(SignalKNotificationAcknowledger.IsBenignAckRejection(
            ApiResult.Fail("Not Found", 404))).IsFalse();
        await Assert.That(SignalKNotificationAcknowledger.IsBenignAckRejection(
            ApiResult.Fail("Unauthorized", 401))).IsFalse();
        // 400 with a different message (e.g. a future SK ack rule)
        // must stay loud - we're not blanket-suppressing all 400s.
        await Assert.That(SignalKNotificationAcknowledger.IsBenignAckRejection(
            ApiResult.Fail("Notification id is malformed", 400))).IsFalse();
    }

    [Test]
    public async Task IsBenignAckRejection_HandlesNullMessage()
    {
        // ApiResult.Fail normalises null/empty to null - the helper
        // must short-circuit cleanly rather than NPE on the contains
        // call. ReadErrorAsync returning null with a 400 status
        // (empty body) is rare but possible.
        var r = ApiResult.Fail((string?)null, 400);
        await Assert.That(SignalKNotificationAcknowledger.IsBenignAckRejection(r)).IsFalse();
    }

    [Test]
    public async Task IsBenignAckRejection_RequiresStatus400()
    {
        // The same message text in a non-400 (theoretically a
        // misbehaving server) must NOT be softened - 400 is the
        // documented duplicate-ack signal; other status codes
        // are unexpected and worth surfacing.
        var r = ApiResult.Fail("Alarm already acknowledged!", 500);
        await Assert.That(SignalKNotificationAcknowledger.IsBenignAckRejection(r)).IsFalse();
    }
}

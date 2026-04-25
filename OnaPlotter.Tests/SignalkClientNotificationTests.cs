using Microsoft.Extensions.Logging.Abstractions;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Tests;

/// <summary>
/// Drives real notification deltas through SignalkClient.ProcessMessage
/// with the exact payload shape observed on openplotter.local, and
/// pins that PerpendicularPassed / ArrivalCircleEntered track the
/// state transitions a course-provider plugin emits.
///
/// Motivation: users keep reporting "auto-advance didn't fire" even
/// when the websocket probe confirms notifications DO arrive at the
/// subscribed client. This test removes guesswork about whether the
/// handler misreads the wrapped { state, method, message, id, status }
/// envelope signalk-server 2.x produces.
/// </summary>
public class SignalkClientNotificationTests
{
    private sealed class FakeBaseUrl : ISignalKBaseUrl
    {
        public string BaseUrl => "http://test.local";
        public string RadarBaseUrl => BaseUrl;
        public Uri StreamUri(string subscribe = "none") => new("ws://test.local");
        public string Combine(string path) => BaseUrl + path;
        public string CombineRadar(string path) => BaseUrl + path;
    }

    private static SignalkClient NewClient()
        => NewClientWithStore().client;

    private static (SignalkClient client, OnaPlotter.Services.ServerNotifications.ServerNotificationStore store) NewClientWithStore()
    {
        var store = new OnaPlotter.Services.ServerNotifications.ServerNotificationStore();
        var c = new SignalkClient(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: new AisStore(),
            http: new HttpClient(),
            settings: new FakeSettings(),
            serverNotifs: store,
            atons: new OnaPlotter.Services.AtonStore(),
            time: TimeProvider.System);
        // Hello would normally set self-context; do it explicitly so
        // context-matching on the URN delta below is deterministic.
        c.SetSelfContext("vessels.urn:mrn:imo:mmsi:261006533");
        return (c, store);
    }

    // Helpers to build the exact shapes observed against openplotter:
    //   - placeholder: {"state":"normal","method":[],"message":"","id":"...","status":{...}}
    //   - armed:      {"state":"alert","method":["visual"],"message":"...","id":"...","status":{...}}
    //   - null cleared: value=null
    private static string Delta(string path, string? state)
    {
        string value;
        if (state is null)
        {
            value = "null";
        }
        else
        {
            value = $@"{{
              ""state"":""{state}"",
              ""method"":[""visual""],
              ""message"":""synthetic"",
              ""id"":""00000000-0000-0000-0000-000000000000"",
              ""status"":{{""silenced"":false,""acknowledged"":false}}
            }}";
        }
        return $@"{{
          ""context"":""vessels.urn:mrn:imo:mmsi:261006533"",
          ""updates"":[{{
            ""timestamp"":""2026-04-22T22:00:00.000Z"",
            ""values"":[{{ ""path"":""{path}"", ""value"":{value} }}]
          }}]
        }}";
    }

    [Test]
    public async Task Perpendicular_StateNormal_Keeps_Flag_False()
    {
        // Initial state published on subscribe is {state:"normal"};
        // this must map to PerpendicularPassed=false so the first
        // armed delta that follows registers as an edge.
        var c = NewClient();
        c.ProcessMessage(Delta("notifications.navigation.course.perpendicularPassed", "normal"));
        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(false);
    }

    [Test]
    public async Task Perpendicular_StateAlert_Sets_Flag_True()
    {
        var c = NewClient();
        c.ProcessMessage(Delta("notifications.navigation.course.perpendicularPassed", "alert"));
        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(true);
    }

    [Test]
    public async Task Perpendicular_NullValue_Clears_Flag()
    {
        var c = NewClient();
        // arm, then clear with value=null (plugin emits on exit)
        c.ProcessMessage(Delta("notifications.navigation.course.perpendicularPassed", "alert"));
        c.ProcessMessage(Delta("notifications.navigation.course.perpendicularPassed", null));
        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(false);
    }

    [Test]
    public async Task Arrival_StateAlert_Sets_Flag_True()
    {
        var c = NewClient();
        c.ProcessMessage(Delta("notifications.navigation.course.arrivalCircleEntered", "alert"));
        await Assert.That(c.Data.ArrivalCircleEntered).IsEqualTo(true);
    }

    [Test]
    public async Task OnDataChanged_Fires_For_Notification_Delta()
    {
        // MainLayout's MaybeAutoAdvanceWaypoint runs inside
        // HandleDataChanged which subscribes to OnDataChanged. If
        // the notification handler doesn't set changed=true, the
        // event won't fire and auto-advance never sees the edge.
        var c = NewClient();
        int fired = 0;
        c.OnDataChanged += () => fired++;
        c.ProcessMessage(Delta("notifications.navigation.course.perpendicularPassed", "alert"));
        await Assert.That(fired).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task Edge_Sequence_Matches_Openplotter_Wire_Shape()
    {
        // Full sequence an approaching vessel goes through:
        //   1. hello -> subscribe
        //   2. initial placeholder state:"normal" delivered
        //   3. boat crosses perpendicular -> state:"alert"
        //   4. boat continues past -> value:null (cleared)
        // Pin every step so a handler regression or a plugin-format
        // shift (e.g. server dropping the wrapper, or adding extra
        // fields) is caught by this test rather than by a failing
        // auto-advance in the field.
        var c = NewClient();
        var path = "notifications.navigation.course.perpendicularPassed";

        c.ProcessMessage(Delta(path, "normal"));
        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(false);

        c.ProcessMessage(Delta(path, "alert"));
        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(true);

        c.ProcessMessage(Delta(path, null));
        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(false);
    }

    // --- fail-safe: unknown / missing severity states ---

    [Test]
    public async Task Perpendicular_UnknownState_FailsSafeArmed()
    {
        // A plugin inventing a new severity (emergency / critical / etc.)
        // must not be silently dropped. The handler treats anything that
        // isn't an explicit "normal" / "cleared" as armed so the helm
        // isn't misled into thinking a situation is quiet when it isn't.
        var c = NewClient();
        c.ProcessMessage(Delta("notifications.navigation.course.perpendicularPassed", "emergency"));
        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(true);
    }

    [Test]
    public async Task Perpendicular_ClearedState_Disarms()
    {
        // "cleared" is another commonly-used clearance word alongside
        // "normal". Both must disarm the flag.
        var c = NewClient();
        c.ProcessMessage(Delta("notifications.navigation.course.perpendicularPassed", "alert"));
        c.ProcessMessage(Delta("notifications.navigation.course.perpendicularPassed", "cleared"));
        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(false);
    }

    [Test]
    public async Task Perpendicular_ObjectWithoutState_FailsSafeArmed()
    {
        // Some older plugin builds emit the notification envelope without
        // an explicit state property when a rule fires. Missing state was
        // previously silently dropped (armed=false); now it fails safe
        // as armed so the alarm actually gets through.
        var c = NewClient();
        string payload = $@"{{
          ""context"":""vessels.urn:mrn:imo:mmsi:261006533"",
          ""updates"":[{{
            ""timestamp"":""2026-04-22T22:00:00.000Z"",
            ""values"":[{{ ""path"":""notifications.navigation.course.perpendicularPassed"",
              ""value"":{{ ""method"":[""visual""], ""message"":""no state field"" }} }}]
          }}]
        }}";
        c.ProcessMessage(payload);
        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(true);
    }

    [Test]
    public async Task Perpendicular_BareBoolTrue_FailsSafeArmed()
    {
        // v3 plugin builds occasionally push a bare boolean for
        // notification paths. True -> armed.
        var c = NewClient();
        string payload = $@"{{
          ""context"":""vessels.urn:mrn:imo:mmsi:261006533"",
          ""updates"":[{{
            ""timestamp"":""2026-04-22T22:00:00.000Z"",
            ""values"":[{{ ""path"":""notifications.navigation.course.perpendicularPassed"",
              ""value"":true }}]
          }}]
        }}";
        c.ProcessMessage(payload);
        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(true);
    }

    // --- calcValues bare-boolean form ---
    //
    // Stock signalk-server (without the course-provider notifications
    // plugin) and some forks publish the leg-advance flag as a plain
    // bool under navigation.course.calcValues.{flag}. The bare-bool
    // fall-through in ProcessSelfDelta routes it to the same flag the
    // notifications form sets, so MaybeAutoAdvanceWaypoint sees the
    // same edge regardless of which shape the server uses. Without
    // this, auto-advance silently dies on a server that doesn't ship
    // the notifications.

    [Test]
    public async Task Perpendicular_CalcValues_BareBool_True_SetsFlag()
    {
        var c = NewClient();
        string payload = $@"{{
          ""context"":""vessels.urn:mrn:imo:mmsi:261006533"",
          ""updates"":[{{
            ""timestamp"":""2026-04-22T22:00:00.000Z"",
            ""values"":[{{ ""path"":""navigation.course.calcValues.perpendicularPassed"",
              ""value"":true }}]
          }}]
        }}";
        c.ProcessMessage(payload);
        await Assert.That(c.Data.PerpendicularPassed).IsTrue();
    }

    [Test]
    public async Task Perpendicular_CalcValues_BareBool_False_ClearsFlag()
    {
        var c = NewClient();
        string armedPayload = $@"{{
          ""context"":""vessels.urn:mrn:imo:mmsi:261006533"",
          ""updates"":[{{
            ""timestamp"":""2026-04-22T22:00:00.000Z"",
            ""values"":[{{ ""path"":""navigation.course.calcValues.perpendicularPassed"",
              ""value"":true }}]
          }}]
        }}";
        string clearedPayload = $@"{{
          ""context"":""vessels.urn:mrn:imo:mmsi:261006533"",
          ""updates"":[{{
            ""timestamp"":""2026-04-22T22:00:01.000Z"",
            ""values"":[{{ ""path"":""navigation.course.calcValues.perpendicularPassed"",
              ""value"":false }}]
          }}]
        }}";
        c.ProcessMessage(armedPayload);
        c.ProcessMessage(clearedPayload);
        await Assert.That(c.Data.PerpendicularPassed).IsFalse();
    }

    [Test]
    public async Task Arrival_CalcValues_BareBool_True_SetsFlag()
    {
        var c = NewClient();
        string payload = $@"{{
          ""context"":""vessels.urn:mrn:imo:mmsi:261006533"",
          ""updates"":[{{
            ""timestamp"":""2026-04-22T22:00:00.000Z"",
            ""values"":[{{ ""path"":""navigation.course.calcValues.arrivalCircleEntered"",
              ""value"":true }}]
          }}]
        }}";
        c.ProcessMessage(payload);
        await Assert.That(c.Data.ArrivalCircleEntered).IsTrue();
    }

    [Test]
    public async Task CalcValues_BareBool_FiresOnDataChanged()
    {
        // Auto-advance only fires inside HandleDataChanged. If the
        // calcValues bare-bool form didn't set changed=true on the
        // delta processor, MaybeAutoAdvanceWaypoint would never wake
        // up and the new subscription would be cosmetic. Pin that the
        // event fires so the trigger chain is intact end-to-end.
        var c = NewClient();
        int fired = 0;
        c.OnDataChanged += () => fired++;
        string payload = $@"{{
          ""context"":""vessels.urn:mrn:imo:mmsi:261006533"",
          ""updates"":[{{
            ""timestamp"":""2026-04-22T22:00:00.000Z"",
            ""values"":[{{ ""path"":""navigation.course.calcValues.perpendicularPassed"",
              ""value"":true }}]
          }}]
        }}";
        c.ProcessMessage(payload);
        await Assert.That(fired).IsGreaterThanOrEqualTo(1);
    }

    // --- Generic server-notification routing -------------------------
    //
    // The notifications.* wildcard subscription delivers anything a
    // plugin armed under notifications/. ProcessSelfDelta should drop
    // the parsed shape into ServerNotificationStore so the alarm rule
    // can surface it on the next Evaluate tick. The course-specific
    // paths above are handled BEFORE this generic path; this group
    // pins the everything-else route.

    private static string GenericNotificationDelta(string path, string? state, string? message)
    {
        string value;
        if (state is null)
        {
            value = "null";
        }
        else
        {
            // Reflect the real plugin envelope shape (state + method
            // + message). message is optional; the server can omit it.
            string msgField = message is null ? "" : $@",""message"":""{message}""";
            value = $@"{{
              ""state"":""{state}"",
              ""method"":[""visual""]{msgField}
            }}";
        }
        return $@"{{
          ""context"":""vessels.urn:mrn:imo:mmsi:261006533"",
          ""updates"":[{{
            ""timestamp"":""2026-04-22T22:00:00.000Z"",
            ""values"":[{{ ""path"":""{path}"", ""value"":{value} }}]
          }}]
        }}";
    }

    [Test]
    public async Task ServerNotification_AlarmStateAndMessage_LandsInStore()
    {
        var (c, store) = NewClientWithStore();

        c.ProcessMessage(GenericNotificationDelta(
            "notifications.environment.depth.belowTransducer", "alarm", "Below 3m"));

        await Assert.That(store.Count).IsEqualTo(1);
        var n = store.Active.Single();
        await Assert.That(n.State).IsEqualTo("alarm");
        await Assert.That(n.Message).IsEqualTo("Below 3m");
    }

    [Test]
    public async Task ServerNotification_NormalState_ClearsExisting()
    {
        var (c, store) = NewClientWithStore();
        c.ProcessMessage(GenericNotificationDelta(
            "notifications.navigation.anchor.position", "alarm", "dragging"));
        await Assert.That(store.Count).IsEqualTo(1);

        c.ProcessMessage(GenericNotificationDelta(
            "notifications.navigation.anchor.position", "normal", null));

        await Assert.That(store.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ServerNotification_NullValue_ClearsExisting()
    {
        // Some plugins clear by publishing JSON null on the value
        // rather than state="normal". Both shapes have to remove the
        // entry.
        var (c, store) = NewClientWithStore();
        c.ProcessMessage(GenericNotificationDelta(
            "notifications.navigation.anchor.position", "alarm", "dragging"));

        c.ProcessMessage(GenericNotificationDelta(
            "notifications.navigation.anchor.position", state: null, message: null));

        await Assert.That(store.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ServerNotification_MultiplePaths_AllStored()
    {
        // Concurrent depth + anchor + custom plugin: the store
        // accumulates one entry per path, all surface in the alarm
        // banner via ServerNotificationsAlarmRule.CheckMany.
        var (c, store) = NewClientWithStore();

        c.ProcessMessage(GenericNotificationDelta(
            "notifications.environment.depth.belowTransducer", "alarm", "shallow"));
        c.ProcessMessage(GenericNotificationDelta(
            "notifications.navigation.anchor.position", "alarm", "dragging"));
        c.ProcessMessage(GenericNotificationDelta(
            "notifications.plugin.custom.alarm", "warn", "custom"));

        await Assert.That(store.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ServerNotification_FiresOnDataChanged()
    {
        // The receive loop's OnDataChanged drives MainLayout's
        // HandleDataChanged -> Alarms.Evaluate -> banner repaint.
        // If notifications routing didn't set changed=true the new
        // notification would sit in the store invisibly until the
        // next vessel position update.
        var (c, _) = NewClientWithStore();
        int fired = 0;
        c.OnDataChanged += () => fired++;

        c.ProcessMessage(GenericNotificationDelta(
            "notifications.navigation.anchor.position", "alarm", "dragging"));

        await Assert.That(fired).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task ServerNotification_CourseFlagPathsDoNotDoubleFire()
    {
        // The course-specific paths are handled BEFORE the generic
        // notifications routing in ProcessSelfDelta. The course
        // handler 'continue's, so the generic store path doesn't see
        // the same delta. Pin that: a perpendicularPassed delta sets
        // the NavigationData flag but does NOT show up as a server
        // notification in the store (otherwise we'd see "ALARM"
        // banners every time the boat reaches a leg waypoint).
        var (c, store) = NewClientWithStore();

        c.ProcessMessage(Delta(
            "notifications.navigation.course.perpendicularPassed", "alert"));

        await Assert.That(c.Data.PerpendicularPassed).IsEqualTo(true);
        await Assert.That(store.Count).IsEqualTo(0);
    }
}

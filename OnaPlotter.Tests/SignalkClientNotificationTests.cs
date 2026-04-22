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
    {
        var c = new SignalkClient(
            baseUrl: new FakeBaseUrl(),
            logger: NullLogger<SignalkClient>.Instance,
            track: new TrackBuffer(),
            ais: new AisStore(),
            http: new HttpClient(),
            settings: new FakeSettings());
        // Hello would normally set self-context; do it explicitly so
        // context-matching on the URN delta below is deterministic.
        c.SetSelfContext("vessels.urn:mrn:imo:mmsi:261006533");
        return c;
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
}

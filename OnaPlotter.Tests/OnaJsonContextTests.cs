using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the source-gen behaviour of both
/// <see cref="OnaJsonContext"/> (compact wire-protocol DTOs) and
/// <see cref="OnaPlotter.Services.Json.OnaGeoJsonContext"/>
/// (indented helm-facing GeoJSON exports + share blobs). The cases
/// here cover round-trip, case-insensitive deserialisation, wire-
/// shape conformance with downstream specs (SignalK subscribe
/// envelopes), and the null-elision contract that share blobs
/// inherit from the previous per-call <c>DefaultIgnoreCondition</c>
/// option. We don't compare byte-for-byte against reflection
/// output -- property ordering is an implementation detail -- but
/// every type round-trips and every documented contract holds.
/// </summary>
public class OnaJsonContextTests
{
    [Test]
    public async Task SignalkDelta_RoundTrips()
    {
        const string json = """
            {"context":"vessels.urn:mrn:imo:mmsi:244000001",
             "updates":[{
               "timestamp":"2026-04-22T22:00:00.000Z",
               "values":[
                 {"path":"navigation.speedOverGround","value":3.5},
                 {"path":"navigation.headingTrue","value":1.57}
               ]
             }]}
            """;
        var delta = JsonSerializer.Deserialize(json, OnaJsonContext.Default.SignalkDelta);
        await Assert.That(delta).IsNotNull();
        await Assert.That(delta!.Context).IsEqualTo("vessels.urn:mrn:imo:mmsi:244000001");
        await Assert.That(delta.Updates).IsNotNull();
        await Assert.That(delta.Updates!.Count).IsEqualTo(1);
        await Assert.That(delta.Updates[0].Values!.Count).IsEqualTo(2);
        await Assert.That(delta.Updates[0].Values![0].Path).IsEqualTo("navigation.speedOverGround");
    }

    [Test]
    public async Task LoginStatus_CaseInsensitive_Matches_Earlier_Reflection_Behaviour()
    {
        // The pre-source-gen call site set
        // PropertyNameCaseInsensitive=true. A server that capitalises
        // "Status" / "AuthenticationRequired" used to parse fine; the
        // source-gen context preserves that via
        // [JsonSourceGenerationOptions(PropertyNameCaseInsensitive=true)].
        const string json = """
            {"Status":"loggedIn","Username":"admin",
             "AuthenticationRequired":true,"ReadOnlyAccess":false}
            """;
        var status = JsonSerializer.Deserialize(json, OnaJsonContext.Default.LoginStatus);
        await Assert.That(status).IsNotNull();
        await Assert.That(status!.Status).IsEqualTo("loggedIn");
        await Assert.That(status.Username).IsEqualTo("admin");
        await Assert.That(status.AuthenticationRequired).IsTrue();
        await Assert.That(status.ReadOnlyAccess).IsFalse();
        await Assert.That(status.ShouldShowLoginWarning).IsFalse();
    }

    [Test]
    public async Task RouteDraft_RoundTrips_Coords_LatLon_Order()
    {
        var original = new RouteDraft(
            RouteId: "route-123",
            Name: "Test Passage",
            Coords: new[]
            {
                new[] { 47.4, 8.5 },
                new[] { 47.5, 8.6 },
            },
            SavedAtIso: "2026-04-22T10:30:00Z");

        var json = JsonSerializer.Serialize(original, OnaJsonContext.Default.RouteDraft);
        var roundtripped = JsonSerializer.Deserialize(json, OnaJsonContext.Default.RouteDraft);

        await Assert.That(roundtripped).IsNotNull();
        await Assert.That(roundtripped!.RouteId).IsEqualTo(original.RouteId);
        await Assert.That(roundtripped.Name).IsEqualTo(original.Name);
        await Assert.That(roundtripped.SavedAtIso).IsEqualTo(original.SavedAtIso);
        await Assert.That(roundtripped.Coords.Length).IsEqualTo(2);
        await Assert.That(roundtripped.Coords[0][0]).IsEqualTo(47.4);
        await Assert.That(roundtripped.Coords[1][1]).IsEqualTo(8.6);
    }

    [Test]
    public async Task SnoozedTargetArray_RoundTrips()
    {
        var now = new DateTime(2026, 4, 22, 12, 0, 0, DateTimeKind.Utc);
        var original = new[]
        {
            new SnoozedTarget("ais-244000001", "Brave Wind", now.AddMinutes(10)),
            new SnoozedTarget("ais-244000002", "Sea Eagle",  now.AddMinutes(15)),
        };

        var json = JsonSerializer.Serialize(original, OnaJsonContext.Default.SnoozedTargetArray);
        var roundtripped = JsonSerializer.Deserialize(json, OnaJsonContext.Default.SnoozedTargetArray);

        await Assert.That(roundtripped).IsNotNull();
        await Assert.That(roundtripped!.Length).IsEqualTo(2);
        await Assert.That(roundtripped[0].TargetKey).IsEqualTo("ais-244000001");
        await Assert.That(roundtripped[0].Label).IsEqualTo("Brave Wind");
        await Assert.That(roundtripped[1].ExpiresAt).IsEqualTo(now.AddMinutes(15));
    }

    [Test]
    public async Task SignalkSubscribeRequest_Wire_Shape_Matches_Spec()
    {
        // Pin the on-the-wire shape: keys "context" / "subscribe", and
        // each subscribe row carries "path" / "period" / "policy".
        // The earlier anon-type Serialize emitted exactly this shape;
        // the named-record migration must not drift.
        var request = new SignalkSubscribeRequest(
            "vessels.self",
            new[]
            {
                new SignalkSubscribePath("navigation.position", 1000, "ideal"),
                new SignalkSubscribePath("navigation.speedOverGround", 1000, "ideal"),
            });

        var json = JsonSerializer.Serialize(request, OnaJsonContext.Default.SignalkSubscribeRequest);

        // Parse it back as a JsonDocument and assert key shape rather
        // than a string compare, which would be brittle on insignificant
        // whitespace / property-order differences.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("context").GetString()).IsEqualTo("vessels.self");
        var subs = root.GetProperty("subscribe");
        await Assert.That(subs.GetArrayLength()).IsEqualTo(2);
        await Assert.That(subs[0].GetProperty("path").GetString()).IsEqualTo("navigation.position");
        await Assert.That(subs[0].GetProperty("period").GetInt32()).IsEqualTo(1000);
        await Assert.That(subs[0].GetProperty("policy").GetString()).IsEqualTo("ideal");
    }

    [Test]
    public async Task SignalkUnsubscribeRequest_Wire_Shape_Matches_Spec()
    {
        var request = new SignalkUnsubscribeRequest(
            "vessels.self",
            new[]
            {
                new SignalkUnsubscribePath("navigation.position"),
            });

        var json = JsonSerializer.Serialize(request, OnaJsonContext.Default.SignalkUnsubscribeRequest);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        await Assert.That(root.GetProperty("context").GetString()).IsEqualTo("vessels.self");
        var unsubs = root.GetProperty("unsubscribe");
        await Assert.That(unsubs.GetArrayLength()).IsEqualTo(1);
        await Assert.That(unsubs[0].GetProperty("path").GetString()).IsEqualTo("navigation.position");
        // Unsubscribe rows carry only "path" -- "period" / "policy" are absent.
        await Assert.That(unsubs[0].TryGetProperty("period", out _)).IsFalse();
        await Assert.That(unsubs[0].TryGetProperty("policy", out _)).IsFalse();
    }

    [Test]
    public async Task GeoJsonShareWaypointFeature_Elides_Null_Properties()
    {
        // Share blobs use [JsonIgnore(Condition=WhenWritingNull)] on
        // both Name and CreatedAt; the previous reflection serializer
        // achieved the same via DefaultIgnoreCondition. Pin: when both
        // are null, the properties bag is "{}". When one is set, only
        // that one is present.
        var feature = new GeoJsonShareWaypointFeature(
            "Feature",
            new GeoJsonPointGeometry("Point", [8.5, 47.4]),
            new GeoJsonShareWaypointProperties(Name: null, CreatedAt: null));
        var json = JsonSerializer.Serialize(
            feature,
            OnaPlotter.Services.Json.OnaGeoJsonContext.Default.GeoJsonShareWaypointFeature);

        using var doc = JsonDocument.Parse(json);
        var props = doc.RootElement.GetProperty("properties");
        await Assert.That(props.TryGetProperty("name", out _)).IsFalse();
        await Assert.That(props.TryGetProperty("createdAt", out _)).IsFalse();

        // Sanity check: a non-null Name renders.
        var named = feature with
        {
            Properties = new GeoJsonShareWaypointProperties(Name: "Buoy", CreatedAt: null),
        };
        var json2 = JsonSerializer.Serialize(
            named,
            OnaPlotter.Services.Json.OnaGeoJsonContext.Default.GeoJsonShareWaypointFeature);
        using var doc2 = JsonDocument.Parse(json2);
        await Assert.That(doc2.RootElement.GetProperty("properties").GetProperty("name").GetString())
            .IsEqualTo("Buoy");
    }

    [Test]
    public async Task SegmentPayload_SerializeKeys_StayPascalCase()
    {
        // History.razor's playback JS reads s.Coords / s.IsStationary /
        // s.Tooltip directly (PascalCase, no naming policy). The
        // SegmentPayload record carries no [JsonPropertyName] so
        // source-gen emits the C# property names verbatim. A future
        // contributor adding a [JsonPropertyName("coords")] (camelCase)
        // would silently break History playback at runtime; this test
        // catches the rename at CI time before it reaches a helm.
        var payload = new List<OnaPlotter.Utilities.HistorySegmentRender.SegmentPayload>
        {
            new(new[] { new[] { 47.4, 8.5 }, new[] { 47.5, 8.6 } }, IsStationary: false, Tooltip: "Trip A"),
        };
        var json = JsonSerializer.Serialize(payload, OnaJsonContext.Default.ListSegmentPayload);

        using var doc = JsonDocument.Parse(json);
        var first = doc.RootElement[0];
        await Assert.That(first.TryGetProperty("Coords", out _)).IsTrue();
        await Assert.That(first.TryGetProperty("IsStationary", out _)).IsTrue();
        await Assert.That(first.TryGetProperty("Tooltip", out _)).IsTrue();
        // Negative assertion: lowercase variants must NOT appear -- a
        // partial PascalCase migration (only some props renamed) is
        // worse than a full rename because the JS reads silently fail.
        await Assert.That(first.TryGetProperty("coords", out _)).IsFalse();
        await Assert.That(first.TryGetProperty("isStationary", out _)).IsFalse();
        await Assert.That(first.TryGetProperty("tooltip", out _)).IsFalse();
    }

    [Test]
    public async Task GeoJsonExportContext_Indents_Output()
    {
        // The indent contract matters: helms occasionally open the
        // .geojson before sharing. WriteIndented=true lives on the
        // OnaGeoJsonContext source-gen options; pin it here so a
        // future drop reverting that flag fails loudly.
        var feature = new GeoJsonWaypointFeature(
            "Feature",
            new GeoJsonNameProperties("Marker"),
            new GeoJsonPointGeometry("Point", [8.5, 47.4]));
        var json = JsonSerializer.Serialize(
            feature,
            OnaPlotter.Services.Json.OnaGeoJsonContext.Default.GeoJsonWaypointFeature);
        // Indented JSON contains newlines + leading whitespace on
        // child lines; non-indented does not.
        await Assert.That(json.Contains('\n')).IsTrue();
    }
}

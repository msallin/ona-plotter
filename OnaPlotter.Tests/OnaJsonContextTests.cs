using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Json;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins that the source-gen <see cref="OnaJsonContext"/> serialiser
/// produces results that round-trip cleanly and (for the types where
/// it matters) match the reflection-based behaviour the codebase
/// previously depended on. We don't compare byte-for-byte against
/// reflection output -- ordering of properties is implementation-
/// detail -- but every type round-trips and every documented
/// case-insensitivity contract holds.
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
}

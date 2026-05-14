using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the wire shape of the trip-share text payload. This is the
/// string the helm sends to a friend over chat, so the format is a
/// user-visible contract: any tweak that re-orders / renames lines
/// changes what the recipient sees. Tests are line-oriented (Contains
/// + StartsWith) rather than full-string equality so a future addition
/// of an extra stat line doesn't force a rewrite of every test.
/// </summary>
public class TripSummaryTests
{
    [Test]
    public async Task Build_AllFieldsPresent_EmitsHeaderFromToDistanceDurationSogDepth()
    {
        var s = MakeSegment(
            startLat: 47.4, startLon: 8.5,
            endLat: 47.6, endLon: 8.7,
            distanceM: 5000,
            duration: TimeSpan.FromHours(2) + TimeSpan.FromMinutes(15),
            sogAvgMs: 5.0, sogMinMs: 0.5, sogMaxMs: 7.0,
            depthMinM: 4.2);

        var text = TripSummary.Build("Trip 2026-05-13 14:30", s);

        await Assert.That(text).Contains("Trip 2026-05-13 14:30");
        await Assert.That(text).Contains("From: 47.40000°N 8.50000°E");
        await Assert.That(text).Contains("To: 47.60000°N 8.70000°E");
        // 5000m -> 2.70nm (Format.Nm: under 10 nm => 2 decimals)
        await Assert.That(text).Contains("Distance: 2.70 nm");
        await Assert.That(text).Contains("Duration: 2h15m");
        // 5.0 m/s ~= 9.7 kn, 0.5 m/s ~= 1.0 kn, 7.0 m/s ~= 13.6 kn
        await Assert.That(text).Contains("SOG: avg 9.7 kn, min 1.0 kn, max 13.6 kn");
        await Assert.That(text).Contains("Min depth: 4.2 m");
    }

    [Test]
    public async Task Build_StartsWithName_FollowedByBlankLine()
    {
        var s = MakeSegment();
        var text = TripSummary.Build("My Trip", s);

        // Header on its own paragraph so chat apps that auto-link the
        // first line don't smear it into the next stat row.
        var expectedPrefix = "My Trip" + Environment.NewLine + Environment.NewLine;
        await Assert.That(text.StartsWith(expectedPrefix)).IsTrue();
    }

    [Test]
    public async Task Build_NoSogSamples_OmitsSogLine()
    {
        var s = MakeSegment(sogAvgMs: null, sogMinMs: null, sogMaxMs: null);
        var text = TripSummary.Build("Trip", s);

        await Assert.That(text).DoesNotContain("SOG:");
    }

    [Test]
    public async Task Build_NoDepthSamples_OmitsMinDepthLine()
    {
        var s = MakeSegment(depthMinM: null);
        var text = TripSummary.Build("Trip", s);

        await Assert.That(text).DoesNotContain("Min depth:");
    }

    [Test]
    public async Task Build_PartialSog_KeepsOnlyAvailableAggregates()
    {
        // Realistic real-world variant: a feed dropped min mid-trip
        // so the segmenter only carries avg + max.
        var s = MakeSegment(sogAvgMs: 5.0, sogMinMs: null, sogMaxMs: 7.0);
        var text = TripSummary.Build("Trip", s);

        await Assert.That(text).Contains("SOG: avg 9.7 kn, max 13.6 kn");
        // "min " must not appear inside the SOG line; assert it's
        // absent from the whole payload (no other line uses "min ").
        await Assert.That(text).DoesNotContain("min ");
    }

    [Test]
    public async Task Build_DistanceOver10Nm_OneDecimal()
    {
        // 30000 m ~= 16.2 nm; Format.Nm switches to one decimal at 10.
        var s = MakeSegment(distanceM: 30_000);
        var text = TripSummary.Build("Trip", s);

        await Assert.That(text).Contains("Distance: 16.2 nm");
    }

    [Test]
    public async Task Build_DurationUnderOneHour_UsesMinutesSecondsFormat()
    {
        // 5m30s is Format.TimeToGo's sub-hour shape.
        var s = MakeSegment(duration: TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));
        var text = TripSummary.Build("Trip", s);

        await Assert.That(text).Contains("Duration: 5m30s");
    }

    [Test]
    public async Task Build_NegativeLatLon_UsesSouthAndWestHemispheres()
    {
        var s = MakeSegment(
            startLat: -33.86, startLon: -151.20,  // off Sydney
            endLat:   -34.00, endLon:   -151.50);
        var text = TripSummary.Build("Trip", s);

        await Assert.That(text).Contains("From: 33.86000°S 151.20000°W");
        await Assert.That(text).Contains("To: 34.00000°S 151.50000°W");
    }

    /// <summary>Helper to build a moving TrackSegment with realistic
    /// defaults; named arguments at the call site override only the
    /// dimensions a given test cares about.</summary>
    private static TrackSegment MakeSegment(
        double startLat = 47.4, double startLon = 8.5,
        double endLat = 47.6, double endLon = 8.7,
        double distanceM = 5000,
        TimeSpan? duration = null,
        double? sogAvgMs = 5.0, double? sogMinMs = 0.5, double? sogMaxMs = 7.0,
        double? depthMinM = 4.2)
    {
        var start = new DateTime(2026, 5, 13, 14, 30, 0, DateTimeKind.Utc);
        var end = start + (duration ?? TimeSpan.FromMinutes(45));
        return new TrackSegment(
            StartUtc: start,
            EndUtc: end,
            StartLat: startLat, StartLon: startLon,
            EndLat: endLat, EndLon: endLon,
            DistanceMetres: distanceM,
            SogAvgMs: sogAvgMs,
            SogMaxMs: sogMaxMs,
            SogMinMs: sogMinMs,
            WindSpeedAvgMs: null,
            IsStationary: false,
            PointCount: 100,
            DepthMinM: depthMinM);
    }
}

using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the History-page segment payload + tooltip formatter. Covers
/// the empty-aggregate cases (no SOG, no wind) where rendering "0.0"
/// would mislead the helm, the moving / stationary tooltip lead text,
/// and the slice consistency between point arrays and segment time
/// windows.
/// </summary>
public class HistorySegmentRenderTests
{
    private static readonly DateTime T0 =
        new(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static TrackPoint Pt(TimeSpan offset, double lat, double lon,
        double? sog = null, double? tws = null)
        => new(T0 + offset, lat, lon, sog, null, null, null, null, null, tws);

    private static TrackSegment Seg(
        DateTime start, DateTime end, bool stationary,
        double distanceM = 0,
        double? sogAvg = null, double? sogMax = null, double? sogMin = null,
        double? twsAvg = null, int pts = 2)
        => new(
            StartUtc: start, EndUtc: end,
            StartLat: 0, StartLon: 0, EndLat: 0, EndLon: 0,
            DistanceMetres: distanceM,
            SogAvgMs: sogAvg, SogMaxMs: sogMax, SogMinMs: sogMin,
            WindSpeedAvgMs: twsAvg,
            IsStationary: stationary,
            PointCount: pts);

    [Test]
    public async Task BuildTooltip_Stationary_LeadsWithDurationAndPointCount()
    {
        var s = Seg(T0, T0.AddHours(2).AddMinutes(15), stationary: true, pts: 270);
        var tip = HistorySegmentRender.BuildTooltip(s);

        await Assert.That(tip).StartsWith("<b>Stationary</b>");
        // Format.TimeToGo on 2h15m = "2h15m"
        await Assert.That(tip).Contains("2h15m");
        await Assert.That(tip).Contains("Pts: 270");
        // Stationary tooltips don't show SOG / TWS rows; the helm
        // only cares about how long the boat sat there.
        await Assert.That(tip.Contains("SOG")).IsFalse();
        await Assert.That(tip.Contains("TWS")).IsFalse();
    }

    [Test]
    public async Task BuildTooltip_Moving_WithFullStats_ShowsAllRows()
    {
        // 2 hours, 12 nm = 12 * 1852 m. SOG avg 3 m/s = ~5.83 kn.
        var s = Seg(T0, T0.AddHours(2),
            stationary: false,
            distanceM: 12 * 1852.0,
            sogAvg: 3.0, sogMax: 4.5, sogMin: 1.5,
            twsAvg: 6.0);
        var tip = HistorySegmentRender.BuildTooltip(s);

        await Assert.That(tip).StartsWith("<b>Moving</b>");
        await Assert.That(tip).Contains("12.0 nm");
        await Assert.That(tip).Contains("2h00m");
        // Format.Speed(3.0) ≈ 5.8 kn; pin the avg and an existence
        // check on max/min so a future Format change updates here too.
        await Assert.That(tip).Contains("SOG: 5.8 avg");
        await Assert.That(tip).Contains("max");
        await Assert.That(tip).Contains("min kn");
        await Assert.That(tip).Contains("TWS avg:");
    }

    [Test]
    public async Task BuildTooltip_Moving_WithoutSog_OmitsSogRow()
    {
        // Moving segment but no SOG samples landed in the window
        // (e.g. AIS-feed-only position with no speed plumbing). The
        // tooltip MUST NOT print "SOG: 0.0 avg / 0.0 max / 0.0 min kn"
        // because that's a lie -- the helm would conclude the boat
        // was stationary while moving.
        var s = Seg(T0, T0.AddHours(1), stationary: false,
            distanceM: 5 * 1852.0,
            sogAvg: null, sogMax: null, sogMin: null,
            twsAvg: null);
        var tip = HistorySegmentRender.BuildTooltip(s);

        await Assert.That(tip).Contains("Moving");
        await Assert.That(tip).Contains("5.00 nm");
        await Assert.That(tip.Contains("SOG")).IsFalse();
        await Assert.That(tip.Contains("TWS")).IsFalse();
    }

    [Test]
    public async Task BuildTooltip_Moving_WithSogButNoWind_OmitsTwsRowOnly()
    {
        var s = Seg(T0, T0.AddHours(1), stationary: false,
            distanceM: 5 * 1852.0,
            sogAvg: 3.0, sogMax: 4.0, sogMin: 2.0,
            twsAvg: null);
        var tip = HistorySegmentRender.BuildTooltip(s);

        await Assert.That(tip).Contains("SOG:");
        await Assert.That(tip.Contains("TWS")).IsFalse();
    }

    [Test]
    public async Task BuildSegmentPayload_SlicesMatchSegmentBoundsOnePerSegment()
    {
        // Three contiguous segments: [0..9 stationary], [10..19 moving],
        // [20..29 stationary]. The slicer must produce exactly three
        // payloads with point counts matching the source segments.
        var pts = new TrackPoint[30];
        for (int i = 0; i < 30; i++) pts[i] = Pt(TimeSpan.FromMinutes(i), 47.4 + i * 0.001, 8.5);

        var segs = new[]
        {
            Seg(pts[0].Timestamp,  pts[9].Timestamp,  stationary: true,  pts: 10),
            Seg(pts[10].Timestamp, pts[19].Timestamp, stationary: false, pts: 10),
            Seg(pts[20].Timestamp, pts[29].Timestamp, stationary: true,  pts: 10),
        };

        var payloads = HistorySegmentRender.BuildSegmentPayload(pts, segs);

        await Assert.That(payloads.Count).IsEqualTo(3);
        await Assert.That(payloads[0].Coords.Length).IsEqualTo(10);
        await Assert.That(payloads[1].Coords.Length).IsEqualTo(10);
        await Assert.That(payloads[2].Coords.Length).IsEqualTo(10);
        await Assert.That(payloads[0].IsStationary).IsTrue();
        await Assert.That(payloads[1].IsStationary).IsFalse();
        await Assert.That(payloads[2].IsStationary).IsTrue();
    }

    [Test]
    public async Task BuildSegmentPayload_DropsSinglePointSegments()
    {
        // Defensive: even though the segmenter merges single-point
        // segments, a downstream caller that hand-constructs segments
        // (e.g. a future stats-page feature) might pass a one-point
        // window. The slicer drops it because a one-coord polyline
        // doesn't render and confuses the JS bounds calc.
        var pts = new TrackPoint[]
        {
            Pt(TimeSpan.FromMinutes(0), 47.4, 8.5),
            Pt(TimeSpan.FromMinutes(1), 47.5, 8.5),
        };
        var segs = new[]
        {
            Seg(pts[0].Timestamp, pts[0].Timestamp, stationary: true, pts: 1),
            Seg(pts[1].Timestamp, pts[1].Timestamp, stationary: true, pts: 1),
        };

        var payloads = HistorySegmentRender.BuildSegmentPayload(pts, segs);

        await Assert.That(payloads.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BuildSegmentPayload_CoordsAreLatLonOrder()
    {
        // Pin the [lat, lon] order: Leaflet expects [lat, lon],
        // GeoJSON (which the SignalK API uses) is [lon, lat]. The
        // payload builder must hand Leaflet what Leaflet wants.
        var pts = new[]
        {
            Pt(TimeSpan.FromMinutes(0), 47.4, 8.5),
            Pt(TimeSpan.FromMinutes(1), 47.5, 8.6),
        };
        var segs = new[] {
            Seg(pts[0].Timestamp, pts[1].Timestamp, stationary: false, pts: 2)
        };

        var payloads = HistorySegmentRender.BuildSegmentPayload(pts, segs);

        await Assert.That(payloads[0].Coords[0][0]).IsEqualTo(47.4);   // lat
        await Assert.That(payloads[0].Coords[0][1]).IsEqualTo(8.5);    // lon
    }
}

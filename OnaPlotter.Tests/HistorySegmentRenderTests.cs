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
        // payloads with point counts matching the source segments,
        // AND the seam between adjacent segments must be clean (no
        // overlap that would render as a tiny "spike" in JS).
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
        // Seam: segment 0 ends at point 9, segment 1 starts at point
        // 10. The latitudes are unique (47.4 + i*0.001) so a one-coord
        // overlap would show up as the same value at both seam ends.
        await Assert.That(payloads[0].Coords[^1][0]).IsEqualTo(pts[9].Latitude);
        await Assert.That(payloads[1].Coords[0][0]).IsEqualTo(pts[10].Latitude);
        await Assert.That(payloads[1].Coords[^1][0]).IsEqualTo(pts[19].Latitude);
        await Assert.That(payloads[2].Coords[0][0]).IsEqualTo(pts[20].Latitude);
    }

    [Test]
    public async Task BuildTooltip_TimeZoneInjection_RendersInUtcWhenAsked()
    {
        // The tooltip's date row is local-time by default; tests
        // would otherwise drift across CI runners in different
        // timezones. With TimeZoneInfo.Utc the row format is fully
        // deterministic and we can pin the exact rendered string.
        var s = Seg(
            start: new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            end:   new DateTime(2024, 6, 1, 14, 15, 0, DateTimeKind.Utc),
            stationary: false,
            distanceM: 12 * 1852.0,
            sogAvg: 3.0, sogMax: 4.5, sogMin: 1.5);

        var tip = HistorySegmentRender.BuildTooltip(s, TimeZoneInfo.Utc);

        await Assert.That(tip).Contains("2024-06-01 12:00 → 14:15");
        // Sanity: with a non-UTC zone the rendered hour would shift.
        // Pick a fixed-offset zone the BCL recognises across platforms.
        var fixedZone = TimeZoneInfo.CreateCustomTimeZone(
            "TestZone+04:00", TimeSpan.FromHours(4),
            "Test +04:00", "Test +04:00");
        var tipPlus4 = HistorySegmentRender.BuildTooltip(s, fixedZone);
        await Assert.That(tipPlus4).Contains("16:00");
    }

    [Test]
    public async Task BuildSegmentPayload_TimeZoneInjection_PropagatesToTooltips()
    {
        var pts = new TrackPoint[]
        {
            Pt(TimeSpan.FromMinutes(0), 47.4, 8.5),
            Pt(TimeSpan.FromMinutes(1), 47.5, 8.5),
        };
        var segs = new[]
        {
            Seg(pts[0].Timestamp, pts[1].Timestamp, stationary: false, pts: 2)
        };

        var payloads = HistorySegmentRender.BuildSegmentPayload(pts, segs, TimeZoneInfo.Utc);

        await Assert.That(payloads.Count).IsEqualTo(1);
        // T0 is 2024-06-01 12:00 UTC; tooltip rendered in UTC is
        // deterministic.
        await Assert.That(payloads[0].Tooltip).Contains("2024-06-01 12:00");
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

    [Test]
    public async Task BuildSegmentPayload_JsonRoundTrip_KeysMatchHistoryJsContract()
    {
        // Regression test for a History-page mount crash: the JSON keys
        // emitted by JsonSerializer.Serialize default-cased to PascalCase
        // ("Coords", "IsStationary", "Tooltip"), but the eval block in
        // History.razor::LoadTrack read s.coords / s.isStationary /
        // s.tooltip (lowercase). Result was L.polyline(undefined, ...)
        // on every History page mount because s.coords was undefined.
        // The fix swaps the JS reads to PascalCase to match what the
        // serialiser emits; this test pins the JSON keys so a future
        // global JsonSerializerOptions change (e.g. opting into
        // JsonSerializerDefaults.Web) doesn't silently flip casing
        // and re-break the History page.
        var pts = new[]
        {
            Pt(TimeSpan.FromMinutes(0), 47.4, 8.5),
            Pt(TimeSpan.FromMinutes(1), 47.5, 8.6),
        };
        var segs = new[] {
            Seg(pts[0].Timestamp, pts[1].Timestamp, stationary: false, pts: 2)
        };

        var payloads = HistorySegmentRender.BuildSegmentPayload(pts, segs);
        var json = System.Text.Json.JsonSerializer.Serialize(payloads);

        // Pin the exact key names; History.razor's eval block reads
        // these verbatim. A casing flip surfaces here as a failed
        // contains assertion before the page crashes at runtime.
        await Assert.That(json).Contains("\"Coords\"");
        await Assert.That(json).Contains("\"IsStationary\"");
        await Assert.That(json).Contains("\"Tooltip\"");
    }
}

using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Layered coverage for <see cref="PointInPolygon.Contains"/>: white-
/// box edge crossings (point above / below / on the ring),
/// equivalence-class boundaries (null / sub-3 / closed-ring duplicate),
/// and realistic anchorage / no-go scenarios that drive the
/// hazard alarm.
/// </summary>
public class PointInPolygonTests
{
    // 10x10 axis-aligned square centred at (5, 5). [lat, lon] order.
    private static readonly double[][] UnitSquare =
    [
        [0.0, 0.0],
        [0.0, 10.0],
        [10.0, 10.0],
        [10.0, 0.0],
    ];

    // Same square but explicitly closed (last point == first), as
    // SignalK regions arrive after RegionApi.ParseRing.
    private static readonly double[][] ClosedSquare =
    [
        [0.0, 0.0],
        [0.0, 10.0],
        [10.0, 10.0],
        [10.0, 0.0],
        [0.0, 0.0],
    ];

    // Concave "L" shape so a horizontal ray through it crosses 4 edges
    // for some y - catches a refactor that breaks the inside/outside
    // alternation when the ray hits more than two edges.
    private static readonly double[][] ConcaveL =
    [
        [0, 0],
        [0, 10],
        [4, 10],
        [4, 4],
        [10, 4],
        [10, 0],
    ];

    // --- white box --------------------------------------------------

    [Test]
    public async Task Contains_PointInsideSquare_ReturnsTrue()
    {
        await Assert.That(PointInPolygon.Contains(UnitSquare, 5.0, 5.0)).IsTrue();
    }

    [Test]
    public async Task Contains_PointOutsideSquare_ReturnsFalse()
    {
        // Outside on each side.
        await Assert.That(PointInPolygon.Contains(UnitSquare, -1.0, 5.0)).IsFalse();
        await Assert.That(PointInPolygon.Contains(UnitSquare, 11.0, 5.0)).IsFalse();
        await Assert.That(PointInPolygon.Contains(UnitSquare, 5.0, -1.0)).IsFalse();
        await Assert.That(PointInPolygon.Contains(UnitSquare, 5.0, 11.0)).IsFalse();
    }

    [Test]
    public async Task Contains_ClosedRing_BehavesSameAsOpen()
    {
        // GeoJSON regions arrive with the first vertex repeated at the
        // end. The crossing-number walker mustn't double-count the
        // closing edge.
        await Assert.That(PointInPolygon.Contains(ClosedSquare, 5.0, 5.0)).IsTrue();
        await Assert.That(PointInPolygon.Contains(ClosedSquare, -1.0, 5.0)).IsFalse();
    }

    [Test]
    public async Task Contains_ConcaveShape_HandlesNotchInsideOutside()
    {
        // Inside the L's vertical arm.
        await Assert.That(PointInPolygon.Contains(ConcaveL, 8.0, 2.0)).IsTrue();
        // Inside the L's horizontal arm.
        await Assert.That(PointInPolygon.Contains(ConcaveL, 2.0, 6.0)).IsTrue();
        // The notch - visually inside the bounding box, but OUTSIDE
        // the L itself. Most algorithm bugs surface here.
        await Assert.That(PointInPolygon.Contains(ConcaveL, 8.0, 8.0)).IsFalse();
    }

    // --- equivalence classes ----------------------------------------

    [Test]
    public async Task Contains_NullRing_ReturnsFalse()
    {
        await Assert.That(PointInPolygon.Contains(null, 5.0, 5.0)).IsFalse();
    }

    [Test]
    public async Task Contains_SubTriangleRing_ReturnsFalse()
    {
        // Two-vertex "ring" is a line segment; not an enclosed area.
        double[][] line = [[0, 0], [10, 10]];
        await Assert.That(PointInPolygon.Contains(line, 5.0, 5.0)).IsFalse();

        // Empty ring.
        double[][] empty = [];
        await Assert.That(PointInPolygon.Contains(empty, 5.0, 5.0)).IsFalse();
    }

    [Test]
    public async Task Contains_VertexWithMissingCoord_DoesNotThrow()
    {
        // Defensive: a malformed vertex (single-element array) shouldn't
        // crash the alarm loop. The walker treats the bad edge as
        // contributing zero crossings and continues.
        double[][] ring =
        [
            [0, 0],
            [0, 10],
            [10],            // malformed
            [10, 0],
            [0, 0],
        ];
        // Result is implementation-defined for the malformed edge but
        // the call must not throw; assert on a clearly-outside point
        // so we still get a meaningful pass condition.
        var result = PointInPolygon.Contains(ring, -5.0, -5.0);
        await Assert.That(result).IsFalse();
    }

    // --- realistic-scenario coverage --------------------------------

    [Test]
    public async Task Contains_LakeOfConstance_NoGoZone_FiresInsideOnly()
    {
        // Approximate ferry-lane no-go area near Konstanz in Lake
        // Constance. Coordinates picked to bracket a real-ish region;
        // the boat is either on the ferry lane (fire) or just outside.
        // [lat, lon] in degrees.
        double[][] ferryLane =
        [
            [47.6580, 9.1700],
            [47.6580, 9.1820],
            [47.6620, 9.1820],
            [47.6620, 9.1700],
            [47.6580, 9.1700],
        ];

        // Boat in the middle of the lane - alarm should fire.
        await Assert.That(PointInPolygon.Contains(ferryLane, 47.6600, 9.1760)).IsTrue();
        // Boat ~150 m north of the lane - safe.
        await Assert.That(PointInPolygon.Contains(ferryLane, 47.6635, 9.1760)).IsFalse();
    }

    [Test]
    public async Task Contains_PointOnEdge_ReturnsTrue()
    {
        // "On the edge" should fire the hazard alarm rather than be
        // a one-pixel safe zone. Pin the documented behaviour so a
        // refactor that switches to strict-inside doesn't change
        // safety semantics by accident.
        // The crossing-number formulation treats this as inside via
        // the >= y-bound and strict < x-cross combination; pin one
        // case (point on the bottom edge of the square).
        await Assert.That(PointInPolygon.Contains(UnitSquare, 0.0, 5.0)).IsTrue();
    }
}

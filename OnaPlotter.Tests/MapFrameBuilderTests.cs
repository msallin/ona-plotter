using OnaPlotter.Models;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Covers the decision tree in MapFrameBuilder.Build. The builder was
/// extracted from Map.razor's HandleDataChanged so the per-tick
/// layering logic (what's in the frame, what's not, throttle counters,
/// clear-course latching) can be asserted directly instead of relying
/// on Playwright to notice a regression.
/// </summary>
public class MapFrameBuilderTests
{
    private static NavigationData NavAt(double? lat, double? lon,
        double? sogMs = null, double? cogRad = null, double? headingRad = null)
    {
        var nav = new NavigationData();
        if (lat is not null && lon is not null)
            nav.ApplyPosition(lat.Value, lon.Value);
        if (sogMs is not null) nav.Apply("navigation.speedOverGround", sogMs.Value);
        if (cogRad is not null) nav.Apply("navigation.courseOverGroundTrue", cogRad.Value);
        if (headingRad is not null) nav.Apply("navigation.headingTrue", headingRad.Value);
        return nav;
    }

    [Test]
    public async Task NoPosition_Builds_EmptyFrame()
    {
        var b = new MapFrameBuilder();
        var frame = b.Build(new NavigationData());
        await Assert.That(frame.Pos).IsNull();
        await Assert.That(frame.Track).IsNull();
        await Assert.That(frame.Course).IsNull();
        await Assert.That(frame.ClearCourse).IsFalse();
        await Assert.That(frame.Current).IsNull();
        await Assert.That(frame.Laylines).IsNull();
    }

    [Test]
    public async Task FirstFix_Emits_Position_But_No_TrackSegment()
    {
        // Track segments need BOTH endpoints -- the first tick after
        // boot has no previous point to draw from, so only Pos fires.
        var b = new MapFrameBuilder();
        var frame = b.Build(NavAt(47.4, 8.5, sogMs: 3.0));
        await Assert.That(frame.Pos).IsNotNull();
        await Assert.That(frame.Track).IsNull();
        // Previous position now latched for the next tick.
        await Assert.That(b.PrevLat).IsEqualTo(47.4);
        await Assert.That(b.PrevLon).IsEqualTo(8.5);
    }

    [Test]
    public async Task SecondFix_Emits_Track_From_Previous_To_Current()
    {
        var b = new MapFrameBuilder();
        b.Build(NavAt(47.4, 8.5));
        var frame = b.Build(NavAt(47.5, 8.6, sogMs: 4.0));
        await Assert.That(frame.Track).IsNotNull();
        // Shape: [lat, lon, sog, prevLat, prevLon]
        await Assert.That(frame.Track![0]).IsEqualTo(47.5);
        await Assert.That(frame.Track![1]).IsEqualTo(8.6);
        await Assert.That(frame.Track![2]).IsEqualTo(4.0);
        await Assert.That(frame.Track![3]).IsEqualTo(47.4);
        await Assert.That(frame.Track![4]).IsEqualTo(8.5);
    }

    [Test]
    public async Task CourseLine_Fires_When_NextPoint_Present()
    {
        var b = new MapFrameBuilder();
        var nav = NavAt(47.4, 8.5);
        nav.ApplyCourseNextPointPosition(47.6, 8.7);
        var frame = b.Build(nav);
        await Assert.That(frame.Course).IsNotNull();
        await Assert.That(frame.Course!.Value.WpLat).IsEqualTo(47.6);
        await Assert.That(frame.Course!.Value.WpLon).IsEqualTo(8.7);
        await Assert.That(frame.ClearCourse).IsFalse();
        await Assert.That(b.CourseLineDrawn).IsTrue();
    }

    [Test]
    public async Task CourseLine_Cleared_On_Transition_To_NoCourse()
    {
        // Course-line-drawn latches so a teardown produces a single
        // clear-course frame, not a sequence of them.
        var b = new MapFrameBuilder();
        var withCourse = NavAt(47.4, 8.5);
        withCourse.ApplyCourseNextPointPosition(47.6, 8.7);
        b.Build(withCourse);
        await Assert.That(b.CourseLineDrawn).IsTrue();

        var noCourse = NavAt(47.4, 8.5);
        var clearFrame = b.Build(noCourse);
        await Assert.That(clearFrame.ClearCourse).IsTrue();
        await Assert.That(clearFrame.Course).IsNull();
        await Assert.That(b.CourseLineDrawn).IsFalse();

        // Subsequent tick with still-no-course should NOT re-emit clear.
        var nextFrame = b.Build(noCourse);
        await Assert.That(nextFrame.ClearCourse).IsFalse();
    }

    [Test]
    public async Task Current_Arrow_Suppressed_Below_Drift_Cutoff()
    {
        var b = new MapFrameBuilder { MinCurrentDriftMs = 0.05 };
        var nav = NavAt(47.4, 8.5);
        nav.Apply("environment.current.setTrue", 1.0);
        nav.Apply("environment.current.drift", 0.04);           // below cutoff
        var frame = b.Build(nav);
        await Assert.That(frame.Current).IsNull();
    }

    [Test]
    public async Task Current_Arrow_Included_When_Drift_Above_Cutoff()
    {
        var b = new MapFrameBuilder { MinCurrentDriftMs = 0.05 };
        var nav = NavAt(47.4, 8.5);
        nav.Apply("environment.current.setTrue", 1.57);
        nav.Apply("environment.current.drift", 0.5);
        var frame = b.Build(nav);
        await Assert.That(frame.Current).IsNotNull();
        await Assert.That(frame.Current!.Value.SetRad).IsEqualTo(1.57);
        await Assert.That(frame.Current!.Value.DriftMs).IsEqualTo(0.5);
    }

    [Test]
    public async Task Laylines_Suppressed_When_Invisible()
    {
        var b = new MapFrameBuilder { LaylinesVisible = false };
        var nav = NavAt(47.4, 8.5);
        nav.Apply("environment.wind.directionTrue", 3.14);
        for (int i = 0; i < 10; i++)
        {
            var f = b.Build(nav);
            await Assert.That(f.Laylines).IsNull();
        }
    }

    [Test]
    public async Task Laylines_Throttled_Every_Nth_Fix()
    {
        // Default throttle: every 5th fix. Fix 5 is the first push.
        var b = new MapFrameBuilder { LaylinesVisible = true, LaylinePushEveryNFixes = 5 };
        var nav = NavAt(47.4, 8.5);
        nav.Apply("environment.wind.directionTrue", 3.14);
        for (int i = 1; i <= 4; i++)
        {
            var f = b.Build(nav);
            await Assert.That(f.Laylines).IsNull();
        }
        var push = b.Build(nav);
        await Assert.That(push.Laylines).IsNotNull();
        // Counter resets; next 4 silent again.
        for (int i = 1; i <= 4; i++)
        {
            var f = b.Build(nav);
            await Assert.That(f.Laylines).IsNull();
        }
        var push2 = b.Build(nav);
        await Assert.That(push2.Laylines).IsNotNull();
    }

    [Test]
    public async Task Laylines_WpCoords_Only_When_Active_Course()
    {
        // With active course, WP coords pass through so JS can draw
        // layline intercepts with the destination. Without, WP coords
        // are null and JS draws bare laylines from own-boat outward.
        var b = new MapFrameBuilder { LaylinesVisible = true, LaylinePushEveryNFixes = 1 };
        var nav = NavAt(47.4, 8.5);
        nav.Apply("environment.wind.directionTrue", 3.14);
        nav.Apply("environment.wind.angleTrueWater", 0.8);

        var noCourse = b.Build(nav);
        await Assert.That(noCourse.Laylines!.Value.WpLat).IsNull();

        nav.ApplyCourseNextPointPosition(47.6, 8.7);
        var withCourse = b.Build(nav);
        await Assert.That(withCourse.Laylines!.Value.WpLat).IsEqualTo(47.6);
        await Assert.That(withCourse.Laylines!.Value.WpLon).IsEqualTo(8.7);
    }

    [Test]
    public async Task SeededPrevPosition_Fires_Track_Segment_On_First_Tick()
    {
        // Map.razor seeds PrevLat/Lon from the server-side track on
        // page init so the first tick's movement is captured too.
        // Pin that behaviour.
        var b = new MapFrameBuilder { PrevLat = 47.4, PrevLon = 8.5 };
        var frame = b.Build(NavAt(47.41, 8.51, sogMs: 2.0));
        await Assert.That(frame.Track).IsNotNull();
        await Assert.That(frame.Track![3]).IsEqualTo(47.4);
    }

    [Test]
    public async Task SuppressLocalTrack_Drops_TrackSegment_Even_With_BothEndpoints()
    {
        // Map.razor flips SuppressLocalTrack on the route-active /
        // server-track-on transition so the long server polyline can
        // be the trail-of-record without the local trail overlapping
        // it. Pin that the gate kills the segment even when prev +
        // current are both present and the cadence has elapsed.
        var b = new MapFrameBuilder
        {
            PrevLat = 47.4,
            PrevLon = 8.5,
            SuppressLocalTrack = true,
        };
        var frame = b.Build(NavAt(47.5, 8.6, sogMs: 4.0));
        await Assert.That(frame.Track).IsNull();
        // Pos still fires -- we want the boat icon to keep moving;
        // it's only the trail polyline that's suppressed.
        await Assert.That(frame.Pos).IsNotNull();
    }

    [Test]
    public async Task SuppressLocalTrack_Off_Resumes_TrackEmission()
    {
        // After the route ends and SuppressLocalTrack is dropped, the
        // trail should resume. PrevLat / PrevLon must be re-seeded by
        // the parent first (Map.razor does this on the deactivation
        // transition); without a seed there's no pair to draw from.
        var b = new MapFrameBuilder { SuppressLocalTrack = true };
        b.Build(NavAt(47.4, 8.5));    // first fix; suppression on, no segment yet
        b.SuppressLocalTrack = false;
        var frame = b.Build(NavAt(47.5, 8.6, sogMs: 4.0));
        await Assert.That(frame.Track).IsNotNull();
        await Assert.That(frame.Track![3]).IsEqualTo(47.4);
    }
}

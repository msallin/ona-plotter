using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the tier-registry contract used by SignalkClient to build the
/// subscription set. Each path belongs to exactly one tier; the
/// ReceiveLoop issues one subscribe per tier with the matching
/// context + period.
///
/// Previous design had overlapping arrays and ran set algebra at
/// subscribe time; the tests here now verify the cleaner partition
/// model. If a future edit re-introduces duplication OR puts a
/// high-dynamic field into SelfSlow, one of these tests surfaces it
/// before the change lands on CI.
/// </summary>
public class SignalkClientSubscriptionPathsTests
{
    [Test]
    public async Task AisPaths_Contains_Shared_Nav_Fields()
    {
        // Shared navigation fields live in the Ais tier -- vessels.*
        // matches self + others, so own-boat still receives them
        // without a duplicate self-subscription. If one of these ever
        // leaves Ais, AIS vessels go dark on the map.
        await Assert.That(SignalkClient.AisPaths).Contains("navigation.position");
        await Assert.That(SignalkClient.AisPaths).Contains("navigation.speedOverGround");
        await Assert.That(SignalkClient.AisPaths).Contains("navigation.courseOverGroundTrue");
        await Assert.That(SignalkClient.AisPaths).Contains("navigation.headingTrue");
    }

    [Test]
    public async Task Tiers_Partition_The_Path_Registry()
    {
        // Each Paths entry has exactly one tier -- no entry duplication,
        // no path is both SelfFast and SelfSlow. Trivially true given
        // PathTier is an enum and each PathSubscription is immutable,
        // but the test catches the "accidentally listed same path
        // twice with different tiers" edit.
        var byPath = SignalkClient.Paths
            .GroupBy(p => p.Path)
            .ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var (path, entries) in byPath)
        {
            await Assert.That(entries.Length).IsEqualTo(1);
        }
    }

    [Test]
    public async Task SelfPaths_And_AisPaths_Are_Disjoint()
    {
        // No overlap in the new model -- Ais-tier paths don't appear
        // in SelfPaths (which is SelfFast + SelfSlow). Previously the
        // two sets overlapped and the receive loop had to strip.
        var overlap = SignalkClient.SelfPaths.Intersect(SignalkClient.AisPaths).ToArray();
        await Assert.That(overlap.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SelfOnly_Tier_Keeps_Wind_Depth_Route_Autopilot()
    {
        // Spot-check that the key self-only fields stayed in their
        // expected tier. If this test fails the field moved to Ais
        // or got dropped entirely.
        await Assert.That(SignalkClient.SelfPaths).Contains("environment.depth.belowTransducer");
        await Assert.That(SignalkClient.SelfPaths).Contains("environment.wind.speedApparent");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.anchor.position");
        await Assert.That(SignalkClient.SelfPaths).Contains("environment.sun");
        await Assert.That(SignalkClient.SelfPaths).Contains("steering.autopilot.state");
    }

    [Test]
    public async Task V2_Course_Paths_Subscribed()
    {
        // The v2 navigation.course API (+ course-provider-plugin's
        // calcValues subtree) is what real Signal K v2 servers
        // publish today. If these drop off, the HUD next-WP /
        // distance / bearing / TTG / VMG starves against any
        // properly-configured server -- the user-visible symptom
        // was empty route info despite an active route.
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.activeRoute.href");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.activeRoute.name");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.activeRoute.pointIndex");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.activeRoute.pointTotal");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.nextPoint.position");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.previousPoint.position");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.calcValues.distance");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.calcValues.bearingTrue");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.calcValues.timeToGo");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.calcValues.velocityMadeGood");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.calcValues.crossTrackError");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.calcValues.route.distance");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.calcValues.route.timeToGo");
    }

    [Test]
    public async Task Course_LegAdvance_Notifications_Subscribed()
    {
        // Two trigger paths for auto-advance:
        //   - notifications.navigation.course.{flag} -- emitted by the
        //     standard SignalK course-provider plugin via its
        //     Notification class (prepends "notifications." in
        //     src/lib/alarms.ts).
        //   - navigation.course.calcValues.{flag} -- bare-boolean
        //     fallback for stock signalk-server builds and forks that
        //     publish the flag without going through the notifications
        //     subsystem.
        // Both shapes route to the same NavigationData flag; subscribing
        // to both is belt-and-braces against future plugin churn.
        await Assert.That(SignalkClient.SelfPaths).Contains("notifications.navigation.course.perpendicularPassed");
        await Assert.That(SignalkClient.SelfPaths).Contains("notifications.navigation.course.arrivalCircleEntered");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.calcValues.perpendicularPassed");
        await Assert.That(SignalkClient.SelfPaths).Contains("navigation.course.calcValues.arrivalCircleEntered");
    }

    [Test]
    public async Task DeadCourse_Subscriptions_NotResurrected()
    {
        // velocityMadeGoodToCourse was a hopeful-but-wrong fallback
        // subscription -- the course-provider plugin only emits
        // velocityMadeGood. Pinning the absence so a future "let's
        // subscribe to everything" sweep doesn't drag dead paths back
        // in.
        await Assert.That(SignalkClient.SelfPaths).DoesNotContain("navigation.course.calcValues.velocityMadeGoodToCourse");
    }

    [Test]
    public async Task Legacy_V1_CoursePaths_NotSubscribed()
    {
        // The v1 navigation.courseGreatCircle.* and
        // navigation.courseRhumbline.* subtrees were dropped in the
        // tech-debt sweep (they duplicated the v2 surface above and
        // SK Node Server has shipped v2 as the default for years).
        // Pinning the absence means a future subscribe-to-all-paths
        // auto-fix doesn't accidentally re-add them without anyone
        // noticing.
        await Assert.That(SignalkClient.SelfPaths).DoesNotContain("navigation.courseGreatCircle.activeRoute.href");
        await Assert.That(SignalkClient.SelfPaths).DoesNotContain("navigation.courseGreatCircle.nextPoint.distance");
        await Assert.That(SignalkClient.SelfPaths).DoesNotContain("navigation.courseRhumbline.activeRoute.href");
        await Assert.That(SignalkClient.SelfPaths).DoesNotContain("navigation.courseRhumbline.nextPoint.distance");
    }

    [Test]
    public async Task SlowSelfPaths_Are_All_In_SelfPaths()
    {
        // SelfSlow is a subset of SelfPaths (which is SelfFast + SelfSlow).
        // Redundant check given the view implementation, but it keeps
        // the invariant explicit so a refactor can't silently
        // redefine the relationship.
        foreach (var slow in SignalkClient.SlowSelfPaths)
        {
            await Assert.That(SignalkClient.SelfPaths).Contains(slow);
        }
    }

    [Test]
    public async Task Fast_And_Slow_Tiers_Are_Disjoint()
    {
        // SelfFast and SelfSlow have to be disjoint or the same path
        // gets subscribed at two different periods -- the server
        // would deliver it twice.
        var fastPaths = SignalkClient.Paths
            .Where(p => p.Tier == SignalkClient.PathTier.SelfFast)
            .Select(p => p.Path).ToHashSet();
        foreach (var slow in SignalkClient.SlowSelfPaths)
        {
            await Assert.That(fastPaths).DoesNotContain(slow);
        }
    }

    [Test]
    public async Task SlowSelfPaths_Contains_The_Expected_Slow_Fields()
    {
        // Fields that change on minute-scale at best should be in the
        // slow tier. This nails the intent so a future edit that adds
        // another fast-moving field doesn't leak into SlowSelfPaths.
        await Assert.That(SignalkClient.SlowSelfPaths).Contains("navigation.anchor.position");
        await Assert.That(SignalkClient.SlowSelfPaths).Contains("navigation.anchor.maxRadius");
        await Assert.That(SignalkClient.SlowSelfPaths).Contains("environment.sun");
        await Assert.That(SignalkClient.SlowSelfPaths).Contains("environment.tide.heightNow");
        // Position / SOG / COG / heading must NEVER end up in the slow
        // tier -- they're the primary driver of the HUD and alarm eval.
        // (They live in the Ais tier now, so they're not in SlowSelfPaths
        // via any path.)
        await Assert.That(SignalkClient.SlowSelfPaths).DoesNotContain("navigation.position");
        await Assert.That(SignalkClient.SlowSelfPaths).DoesNotContain("navigation.speedOverGround");
    }

    [Test]
    public async Task Registry_Includes_Radar_Wildcard()
    {
        // Mayara radar ARPA targets arrive under vessels.self at
        // radars.<rid>.targets.<tid>.*; without this wildcard the
        // ProcessSelfDelta radar routing never fires. Specific
        // regression: the tier-refactor must not drop it.
        await Assert.That(SignalkClient.SelfPaths).Contains("radars.*.targets.*");
    }
}

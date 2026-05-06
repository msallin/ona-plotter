using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the tier-registry contract used by SignalkClient to build the
/// subscription set. Each path lives in exactly one
/// <c>SubscriptionTier</c>; the ReceiveLoop iterates Tiers and issues
/// one subscribe per tier with the matching context + period + policy.
///
/// If a future edit re-introduces duplication OR puts a high-dynamic
/// field into SelfSlow, one of these tests surfaces it before CI.
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
    public async Task Tiers_Have_NonEmpty_Names_And_Contexts()
    {
        // Every shipped tier needs a name (used by tests + diagnostics)
        // and a context (used as the subscribe message's "context"
        // field). An empty-string context would subscribe under the
        // server's default which is a wire bug we don't want quietly.
        foreach (var tier in SignalkClient.Tiers)
        {
            await Assert.That(string.IsNullOrEmpty(tier.Name)).IsFalse();
            await Assert.That(string.IsNullOrEmpty(tier.Context)).IsFalse();
        }
    }

    [Test]
    public async Task Each_Path_Lives_In_Exactly_One_Tier()
    {
        // Subscribing the same path under two tiers means the server
        // delivers it twice with potentially conflicting periods. The
        // partition model is the whole point of the refactor; this
        // test catches the "copy-pasted path into a second tier" edit.
        var byPath = SignalkClient.Tiers
            .SelectMany(t => t.Paths.Select(p => new { Tier = t.Name, Path = p }))
            .GroupBy(x => x.Path)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Tier).ToArray());

        foreach (var (path, tierNames) in byPath)
        {
            await Assert.That(tierNames.Length).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Notification_Tier_Uses_Instant_Policy()
    {
        // Edge-triggered notifications (perpendicularPassed,
        // arrivalCircleEntered) MUST ride policy=instant or the server
        // can coalesce the exact transition that drives auto-advance.
        var notif = SignalkClient.Tiers.Single(t => t.Name == "SelfFastNotifications");
        await Assert.That(notif.Policy).IsEqualTo("instant");
        await Assert.That(notif.Context).IsEqualTo("vessels.self");
    }

    [Test]
    public async Task Slow_Tier_Period_Is_The_Slow_Constant()
    {
        // Pin the slow-tier period so a future edit can't quietly
        // promote anchor / tide / draft to a 1 Hz subscription. Any
        // bump to SlowSubscriptionPeriodMs is intentional and the
        // test will track it via the constant.
        var slow = SignalkClient.Tiers.Single(t => t.Name == "SelfSlow");
        await Assert.That(slow.PeriodMs).IsEqualTo(SignalkClient.SlowSubscriptionPeriodMs);
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
    public async Task V2_Anchor_Paths_Subscribed()
    {
        // signalk-anchoralarm-plugin v2.0.0+ publishes four extra
        // paths beyond the v1 trio (position / maxRadius /
        // currentRadius). Without these subscribed, the HUD's
        // bow-corrected distance + bearing + rode length readouts
        // stay null on a properly-configured plugin -- silent
        // regression.
        await Assert.That(SignalkClient.SlowSelfPaths)
            .Contains("navigation.anchor.bearingTrue");
        await Assert.That(SignalkClient.SlowSelfPaths)
            .Contains("navigation.anchor.apparentBearing");
        await Assert.That(SignalkClient.SlowSelfPaths)
            .Contains("navigation.anchor.rodeLength");
        await Assert.That(SignalkClient.SlowSelfPaths)
            .Contains("navigation.anchor.distanceFromBow");
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
        var fastPaths = SignalkClient.Tiers
            .Where(t => t.Name == "SelfFast")
            .SelectMany(t => t.Paths)
            .ToHashSet();
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

    [Test]
    public async Task ServerNotifications_Tier_Subscribes_Wildcard()
    {
        // The "ServerNotifications" tier is what plumbs server-side
        // SignalK notifications (signalk-anchoralarm-plugin et al)
        // into the alarm banner. If this tier disappears, server-
        // decided alarms go silent on our end -- helm relies on this
        // to surface plugin-driven alerts.
        var tier = SignalkClient.Tiers.SingleOrDefault(t => t.Name == "ServerNotifications");
        await Assert.That(tier).IsNotNull();
        await Assert.That(tier!.Context).IsEqualTo("vessels.self");
        await Assert.That(tier.Paths).Contains("notifications.*");
    }
}

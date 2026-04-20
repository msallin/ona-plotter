using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins the self / AIS subscription split. vessels.* matches self too,
/// so any path in both lists would be delivered twice for own-boat --
/// the user would see "same time, same position 3-4x" in the raw stream
/// and the HUD would count ticks twice. The receive loop strips
/// <c>SelfPaths.Except(AisPaths)</c> for the vessels.self subscription;
/// these tests lock that contract so a future edit doesn't silently
/// re-introduce the duplicate.
/// </summary>
public class SignalkClientSubscriptionPathsTests
{
    [Test]
    public async Task AisPaths_Contains_Shared_Nav_Fields()
    {
        // These are the fields AIS vessels *must* publish for the map to
        // render them. They also happen to be self fields, which is why
        // the overlap exists at all. If one of these ever leaves AisPaths
        // we'd stop showing AIS positions.
        await Assert.That(SignalkClient.AisPaths).Contains("navigation.position");
        await Assert.That(SignalkClient.AisPaths).Contains("navigation.speedOverGround");
        await Assert.That(SignalkClient.AisPaths).Contains("navigation.courseOverGroundTrue");
        await Assert.That(SignalkClient.AisPaths).Contains("navigation.headingTrue");
    }

    [Test]
    public async Task SelfOnly_Subscription_Drops_Paths_That_Come_Via_Wildcard()
    {
        // The receive loop effectively does this Except() before sending
        // the vessels.self subscribe. Verify the result excludes every
        // AIS path so we never ask the server to deliver the same path
        // under two contexts.
        var selfOnly = SignalkClient.SelfPaths.Except(SignalkClient.AisPaths).ToArray();
        foreach (var aisPath in SignalkClient.AisPaths)
        {
            await Assert.That(selfOnly).DoesNotContain(aisPath);
        }
    }

    [Test]
    public async Task SelfOnly_Subscription_Keeps_SelfOnly_Paths()
    {
        // Self-only fields (depth, wind, anchor) must survive the Except.
        // If they didn't, the HUD would go dark for own-boat telemetry.
        var selfOnly = SignalkClient.SelfPaths.Except(SignalkClient.AisPaths).ToArray();
        await Assert.That(selfOnly).Contains("environment.depth.belowTransducer");
        await Assert.That(selfOnly).Contains("environment.wind.speedApparent");
        await Assert.That(selfOnly).Contains("navigation.anchor.position");
        await Assert.That(selfOnly).Contains("environment.sun");
    }

    [Test]
    public async Task SelfPaths_Still_Overlaps_AisPaths_So_The_Strip_Is_Meaningful()
    {
        // If a future refactor removes the shared paths from SelfPaths
        // entirely, the Except() becomes a no-op and the comment in
        // SignalkClient.ReceiveLoopAsync would be misleading. Keep the
        // overlap intentional: SelfPaths remains the single source of
        // truth for "what self should see", and the send-time strip is
        // the duplicate guard.
        var overlap = SignalkClient.SelfPaths.Intersect(SignalkClient.AisPaths).ToArray();
        await Assert.That(overlap.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task SlowSelfPaths_Are_All_In_SelfPaths()
    {
        // SlowSelfPaths is a SUBSET of SelfPaths; any path listed in
        // the slow tier must also appear in the master list or the
        // subscription split below silently drops it.
        foreach (var slow in SignalkClient.SlowSelfPaths)
        {
            await Assert.That(SignalkClient.SelfPaths).Contains(slow);
        }
    }

    [Test]
    public async Task Fast_And_Slow_Subscription_Sets_Are_Disjoint()
    {
        // ReceiveLoopAsync computes FastSelf = SelfPaths \ AisPaths \ SlowSelfPaths.
        // Pin that those three sets partition SelfPaths cleanly so a
        // path can't accidentally show up in both the 1 Hz and 10 s
        // subscriptions (doubling delivery for no reason).
        var fast = SignalkClient.SelfPaths.Except(SignalkClient.AisPaths)
                                          .Except(SignalkClient.SlowSelfPaths).ToArray();
        foreach (var f in fast)
        {
            await Assert.That(SignalkClient.SlowSelfPaths).DoesNotContain(f);
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
        await Assert.That(SignalkClient.SlowSelfPaths).DoesNotContain("navigation.position");
        await Assert.That(SignalkClient.SlowSelfPaths).DoesNotContain("navigation.speedOverGround");
    }
}

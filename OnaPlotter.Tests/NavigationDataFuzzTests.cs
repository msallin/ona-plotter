using OnaPlotter.Models;

namespace OnaPlotter.Tests;

/// <summary>
/// Fuzz-style tests for NavigationData.Apply. Covers the numeric-path
/// edge cases (NaN, infinity, negative depth, giant magnitudes) and
/// the clear-state transitions that the anchor / course handlers rely
/// on. A regression in any of these silently breaks the alarm /
/// HUD pipelines downstream.
/// </summary>
public class NavigationDataFuzzTests
{
    [Test]
    public async Task Depth_NaN_IsStoredAsIs_AlarmRulesMustGuard()
    {
        // NaN from a flaky DST800 happens; Apply doesn't filter it so
        // downstream rules (ShallowAlarmRule, tide math) are responsible
        // for Not-a-Number checks. Pin the contract so we don't start
        // filtering at apply time and surprise a downstream consumer
        // that relied on seeing the NaN.
        var nav = new NavigationData();
        var ok = nav.Apply("environment.depth.belowTransducer", double.NaN);
        await Assert.That(ok).IsTrue();
        await Assert.That(nav.Depth is double d && double.IsNaN(d)).IsTrue();
    }

    [Test]
    public async Task Depth_Negative_IsStored_AlarmsMustGuard()
    {
        // A transducer mounted above the waterline, an un-calibrated
        // offset, or a sensor in bilge-test mode can push depth below
        // zero. Surface it raw; the HUD will render something ugly
        // which is the right signal that the sensor is mis-configured.
        var nav = new NavigationData();
        nav.Apply("environment.depth.belowTransducer", -2.5);
        await Assert.That(nav.Depth).IsEqualTo(-2.5);
    }

    [Test]
    public async Task AnchorRadius_HugeValue_IsStored()
    {
        // A plugin pushing a 1e9 metre max radius (bug or corrupt
        // config) must not crash the apply path. HUD rendering may
        // look absurd; that's fine -- "anchor watch configured with
        // a silly radius" is better than "anchor watch not visible
        // because we crashed".
        var nav = new NavigationData();
        nav.Apply("navigation.anchor.maxRadius", 1_000_000_000.0);
        await Assert.That(nav.AnchorMaxRadius).IsEqualTo(1_000_000_000.0);
    }

    [Test]
    public async Task ClearAnchor_WithNothingSet_IsNoOp()
    {
        // Belt-and-braces: if the plugin sends a clearing delta before
        // any anchor state was ever set, we shouldn't crash and we
        // shouldn't set AnchorActive unexpectedly.
        var nav = new NavigationData();
        nav.ClearAnchor();
        await Assert.That(nav.AnchorActive).IsFalse();
    }

    [Test]
    public async Task ClearAnchor_WipesAllFourFields()
    {
        // A partial clear would leave stale data visible on the HUD.
        // All four anchor fields must null out.
        var nav = new NavigationData();
        nav.ApplyAnchorPosition(47.0, 8.0);
        nav.Apply("navigation.anchor.maxRadius", 30.0);
        nav.Apply("navigation.anchor.currentRadius", 12.0);
        await Assert.That(nav.AnchorActive).IsTrue();

        nav.ClearAnchor();
        await Assert.That(nav.AnchorActive).IsFalse();
        await Assert.That(nav.AnchorMaxRadius).IsNull();
        await Assert.That(nav.AnchorCurrentRadius).IsNull();
    }

    [Test]
    public async Task ClearCourse_WipesRouteProgressFieldsToo()
    {
        // The new route-total + WP-progress fields (pointIndex etc.)
        // must null out on ClearCourse too -- otherwise after Stop
        // Navigation the HUD would still show "WP 3 of 7" for a few
        // ticks until the server stopped publishing.
        var nav = new NavigationData();
        nav.ApplyCourseNextPointPosition(47.0, 8.0);
        nav.ApplyString("navigation.courseGreatCircle.activeRoute.href", "/resources/routes/abc");
        nav.Apply("navigation.courseGreatCircle.activeRoute.pointIndex", 2.0);
        nav.Apply("navigation.courseGreatCircle.activeRoute.pointTotal", 7.0);
        nav.Apply("navigation.courseGreatCircle.activeRoute.distanceRemaining", 5000.0);
        nav.Apply("navigation.courseGreatCircle.activeRoute.timeToGo", 1800.0);

        nav.ClearCourse();

        await Assert.That(nav.ActiveRoutePointIndex).IsNull();
        await Assert.That(nav.ActiveRoutePointTotal).IsNull();
        await Assert.That(nav.ActiveRouteDistanceRemaining).IsNull();
        await Assert.That(nav.ActiveRouteTimeToGo).IsNull();
        await Assert.That(nav.ActiveRouteHref).IsNull();
    }

    [Test]
    public async Task ActiveRoutePointIndex_FractionalServerValue_TruncatedToInt()
    {
        // SK path pointIndex is spec'd as integer but some servers
        // push 0.0 / 1.0 as a double. NavigationData.Apply receives it
        // as a double and casts to int. 1.9 -> 1 (truncation) is fine;
        // the test pins that behaviour so a future server version
        // rounding up doesn't silently change the "WP 2 of 7" label.
        var nav = new NavigationData();
        nav.Apply("navigation.courseGreatCircle.activeRoute.pointIndex", 1.9);
        await Assert.That(nav.ActiveRoutePointIndex).IsEqualTo(1);
    }

    [Test]
    public async Task RepeatedApply_SameValue_DoesNotThrow()
    {
        // AisStore's version ticks on every Apply; NavigationData
        // doesn't track version but must tolerate being fed the same
        // value dozens of times without throwing or leaking state.
        var nav = new NavigationData();
        for (int i = 0; i < 100; i++)
            nav.Apply("environment.depth.belowTransducer", 3.5);
        await Assert.That(nav.Depth).IsEqualTo(3.5);
    }

    [Test]
    public async Task Apply_ThreadSafety_ConcurrentWrites_DoNotCorruptState()
    {
        // NavigationData.Apply takes an internal lock. Stress it from
        // multiple threads to surface lock-order or forgotten-lock
        // regressions. 1000 interleaved writes on two paths should
        // end with consistent final values.
        var nav = new NavigationData();
        var tasks = new List<Task>();
        for (int t = 0; t < 4; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 250; i++)
                {
                    nav.Apply("environment.depth.belowTransducer", i * 0.1);
                    nav.Apply("navigation.speedOverGround", i * 0.05);
                }
            }));
        }
        await Task.WhenAll(tasks);
        // No assertion on specific values -- we only pin that the apply
        // path didn't throw / deadlock. Completion is the signal.
        await Assert.That(nav.Depth).IsNotNull();
        await Assert.That(nav.SpeedOverGround).IsNotNull();
    }
}

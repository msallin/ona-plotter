using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

/// <summary>
/// Rule-level tests for <see cref="HazardousRegionAlarmRule"/>. The
/// underlying point-in-polygon math is pinned by
/// <see cref="PointInPolygonTests"/>; here we cover the rule
/// orchestration:
/// <list type="bullet">
///   <item>own-ship inside a hazardous region fires Danger</item>
///   <item>own-ship inside a NON-hazardous region is silent (decorative
///       regions don't suddenly start alarming)</item>
///   <item>own-ship outside every region is silent</item>
///   <item>missing GPS is silent (no throw)</item>
///   <item>multiple overlapping hazardous regions emit one alarm per
///       region (CheckMany), with distinct TargetKeys so the AlarmManager's
///       per-key dismiss + cooldown state stays separate</item>
///   <item>region with empty name + non-empty Id falls back to Id;
///       both empty falls back to a stable label so the alarm key is
///       deterministic</item>
/// </list>
/// </summary>
public class HazardousRegionAlarmRuleTests
{
    private sealed class StubRegionStore : IRegionStore
    {
        public IReadOnlyList<SignalkRegion> Regions { get; private set; } = Array.Empty<SignalkRegion>();
        public void SetRegions(IReadOnlyList<SignalkRegion>? regions) =>
            Regions = regions ?? Array.Empty<SignalkRegion>();
    }

    /// <summary>5x5 square centred at (5, 5) in degrees - mirrors the
    /// PointInPolygon fixture so the in/out boundary is obvious.</summary>
    private static SignalkRegion SquareRegion(string id, string? name, bool isHazard,
        double minLat = 0, double maxLat = 10, double minLon = 0, double maxLon = 10)
    {
        return new SignalkRegion
        {
            Id = id,
            Name = name,
            IsHazard = isHazard,
            OuterRings = new[]
            {
                new[]
                {
                    new[] { minLat, minLon },
                    new[] { minLat, maxLon },
                    new[] { maxLat, maxLon },
                    new[] { maxLat, minLon },
                    new[] { minLat, minLon },
                },
            },
        };
    }

    private static AlarmEvaluationContext Ctx(NavigationData data, IAppSettings? settings = null) =>
        new(data,
            Array.Empty<AisVessel>(),
            settings ?? new FakeSettings(),
            DateTime.UtcNow,
            _ => false);

    private static NavigationData NavAt(double? lat, double? lon)
    {
        var nav = new NavigationData();
        if (lat is double a && lon is double b) nav.ApplyPosition(a, b);
        return nav;
    }

    [Test]
    public async Task NoGps_ReturnsNothing()
    {
        // GPS hasn't published yet; the rule must not throw and must
        // not emit a phantom alarm just because a hazardous region
        // exists.
        var store = new StubRegionStore();
        store.SetRegions(new[] { SquareRegion("r1", "Reefs", isHazard: true) });
        var rule = new HazardousRegionAlarmRule(store);

        var alarms = rule.CheckMany(Ctx(NavAt(null, null))).ToList();
        await Assert.That(alarms.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InsideHazardousRegion_FiresDanger()
    {
        var store = new StubRegionStore();
        store.SetRegions(new[] { SquareRegion("r1", "Reefs", isHazard: true) });
        var rule = new HazardousRegionAlarmRule(store);

        // Own-ship inside the 0..10 / 0..10 square.
        var alarms = rule.CheckMany(Ctx(NavAt(5.0, 5.0))).ToList();
        await Assert.That(alarms.Count).IsEqualTo(1);
        await Assert.That(alarms[0].Title).IsEqualTo("HAZARD");
        await Assert.That(alarms[0].Severity).IsEqualTo(AlarmSeverity.Danger);
        await Assert.That(alarms[0].TargetKey).IsEqualTo("r1");
        await Assert.That(alarms[0].TargetLabel).IsEqualTo("Reefs");
        await Assert.That(alarms[0].Message).Contains("Reefs");
    }

    [Test]
    public async Task OutsideRegion_Silent()
    {
        var store = new StubRegionStore();
        store.SetRegions(new[] { SquareRegion("r1", "Reefs", isHazard: true) });
        var rule = new HazardousRegionAlarmRule(store);

        var alarms = rule.CheckMany(Ctx(NavAt(20.0, 20.0))).ToList();
        await Assert.That(alarms.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InsideNonHazardousRegion_Silent()
    {
        // Decorative regions (the existing common case) must NOT
        // start firing an alarm just because they're now hazard-aware.
        // Pin so a refactor that drops the IsHazard gate surfaces.
        var store = new StubRegionStore();
        store.SetRegions(new[] { SquareRegion("r1", "Pretty Bay", isHazard: false) });
        var rule = new HazardousRegionAlarmRule(store);

        var alarms = rule.CheckMany(Ctx(NavAt(5.0, 5.0))).ToList();
        await Assert.That(alarms.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MultipleOverlappingHazards_FireOnePerRegion()
    {
        // Helm draws "shipping lane" + "no-anchor zone" overlapping at
        // a busy harbour entrance; both apply when the boat sits in the
        // intersection. CheckMany should yield two AlarmInfos with
        // distinct TargetKeys so the AlarmManager keys them
        // independently (dismissing one shouldn't silence the other).
        var store = new StubRegionStore();
        store.SetRegions(new[]
        {
            SquareRegion("r1", "Shipping lane", isHazard: true),
            SquareRegion("r2", "No anchor",      isHazard: true),
        });
        var rule = new HazardousRegionAlarmRule(store);

        var alarms = rule.CheckMany(Ctx(NavAt(5.0, 5.0))).ToList();
        await Assert.That(alarms.Count).IsEqualTo(2);
        var keys = alarms.Select(a => a.TargetKey).ToHashSet();
        await Assert.That(keys.Contains("r1")).IsTrue();
        await Assert.That(keys.Contains("r2")).IsTrue();
    }

    [Test]
    public async Task MixedHazardousAndDecorative_FiresOnlyHazardous()
    {
        // Three regions overlap own-ship; only the hazardous one fires.
        var store = new StubRegionStore();
        store.SetRegions(new[]
        {
            SquareRegion("decorative", "Anchorage", isHazard: false),
            SquareRegion("danger",     "Reefs",     isHazard: true),
            SquareRegion("decorative2","Lighthouse",isHazard: false),
        });
        var rule = new HazardousRegionAlarmRule(store);

        var alarms = rule.CheckMany(Ctx(NavAt(5.0, 5.0))).ToList();
        await Assert.That(alarms.Count).IsEqualTo(1);
        await Assert.That(alarms[0].TargetKey).IsEqualTo("danger");
    }

    [Test]
    public async Task EmptyName_FallsBackToId()
    {
        // Regions imported from a third-party tool may lack a name.
        // The TargetLabel must still be informative so the helm sees
        // SOMETHING in the banner.
        var store = new StubRegionStore();
        store.SetRegions(new[] { SquareRegion("region-abc", name: null, isHazard: true) });
        var rule = new HazardousRegionAlarmRule(store);

        var alarms = rule.CheckMany(Ctx(NavAt(5.0, 5.0))).ToList();
        await Assert.That(alarms.Count).IsEqualTo(1);
        await Assert.That(alarms[0].TargetLabel).IsEqualTo("region-abc");
        await Assert.That(alarms[0].TargetKey).IsEqualTo("region-abc");
    }

    [Test]
    public async Task EmptyNameAndId_FallsBackToStableLabel()
    {
        // Pathological case but defends the alarm-key invariant: even
        // with both Name and Id missing, TargetKey must be non-empty so
        // the AlarmManager's (Title, TargetKey) keying doesn't collapse
        // every nameless hazard into one slot.
        var store = new StubRegionStore();
        store.SetRegions(new[] { SquareRegion(id: "", name: "", isHazard: true) });
        var rule = new HazardousRegionAlarmRule(store);

        var alarms = rule.CheckMany(Ctx(NavAt(5.0, 5.0))).ToList();
        await Assert.That(alarms.Count).IsEqualTo(1);
        await Assert.That(string.IsNullOrEmpty(alarms[0].TargetKey)).IsFalse();
    }

    [Test]
    public async Task NoRegions_Silent()
    {
        var store = new StubRegionStore();
        var rule = new HazardousRegionAlarmRule(store);

        var alarms = rule.CheckMany(Ctx(NavAt(5.0, 5.0))).ToList();
        await Assert.That(alarms.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RuleIdentity_IsStable()
    {
        // Pinning the priority + autoclear so a refactor that flips
        // them is intentional, not silent.
        var rule = new HazardousRegionAlarmRule(new StubRegionStore());
        await Assert.That(rule.Title).IsEqualTo("HAZARD");
        await Assert.That(rule.Priority).IsEqualTo(150);
        await Assert.That(rule.AutoClear).IsTrue();
    }

    [Test]
    public async Task GetPublishPath_SanitisesTargetKey()
    {
        // Per-target notification path must strip path-illegal chars
        // so a region id like "urn:mrn:..." can't smuggle hierarchy
        // past the notifications.security.hazard. namespace.
        var rule = new HazardousRegionAlarmRule(new StubRegionStore());
        var alarm = new AlarmInfo("HAZARD", "Inside X", AlarmSeverity.Danger,
            TargetKey: "urn:mrn:abc.def/123");
        var path = rule.GetPublishPath(alarm);
        await Assert.That(path).IsNotNull();
        await Assert.That(path!).StartsWith("notifications.security.hazard.");
        // Colons / slashes / dots in the key get replaced with '_'.
        await Assert.That(path).Contains("urn_mrn_abc_def_123");
    }

    [Test]
    public async Task GetPublishPath_NullForEmptyTargetKey()
    {
        // Defensive: an alarm without a target key shouldn't get
        // published at notifications.security.hazard. (no suffix).
        var rule = new HazardousRegionAlarmRule(new StubRegionStore());
        var alarm = new AlarmInfo("HAZARD", "Inside", AlarmSeverity.Danger);
        await Assert.That(rule.GetPublishPath(alarm)).IsNull();
    }
}

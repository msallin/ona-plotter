using System.Diagnostics;
using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

/// <summary>
/// Stress test for <see cref="AlarmManager.Evaluate"/> with a busy
/// harbour AIS feed. The CPA rule iterates every vessel each tick;
/// 200 targets is realistic for a Solent / SF Bay scenario. The
/// invariants:
///   1. Evaluate completes well under one second on a typical dev box
///      (the 1 Hz tick budget would otherwise be exceeded);
///   2. Threats inside the guard zone are detected and surfaced as
///      Danger alarms with their target context preserved;
///   3. The harbour-mode bundle (HarborMode = true) suppresses CPA
///      regardless of how many vessels are inside the zone.
///
/// <para>Wall-clock budgets are intentionally generous (10x what local
/// runs need) to absorb CI runner variance without flaking.</para>
/// </summary>
public class AlarmManagerStressTests
{
    [Test]
    public async Task Evaluate_WithTwoHundredVessels_CompletesUnderBudget()
    {
        // 200 AIS targets scattered across a 4 NM box around own boat,
        // each with a random course / speed. A handful are deliberately
        // placed on collision tracks so the CPA branch fires; the rest
        // are background traffic.
        var rules = BuildRules();
        var settings = new FakeSettings();
        var clock = new MutableClock();
        var mgr = NewManager(rules, clock);

        var ownNav = OwnNav(0.0, 0.0, cogRad: 0.0, sogMs: 3.0);     // heading north @ ~6 kn
        var vessels = BuildHarbourFleet(200, includeColliders: 5, seed: 0xCA_FE_42);

        var sw = Stopwatch.StartNew();
        mgr.Evaluate(ownNav, vessels, settings);
        sw.Stop();

        await Assert.That(sw.Elapsed.TotalMilliseconds)
            .IsLessThan(200)
            .Because($"200-vessel Evaluate should complete <200ms; took {sw.ElapsedMilliseconds}ms");

        // The 5 colliders, by construction, are inside the guard zone.
        // At least one should produce an active CPA alarm.
        await Assert.That(mgr.ActiveAlarm).IsNotNull();
        await Assert.That(mgr.ActiveAlarm!.Title).IsEqualTo("CPA");
    }

    [Test]
    public async Task Evaluate_HarborMode_SuppressesCpaAcrossManyVessels()
    {
        // Harbor mode bundle silences CPA regardless of vessel count.
        // Pin the contract under a 200-vessel load so a partial
        // refactor that misses the early return still hits this.
        var rules = BuildRules();
        var settings = new FakeSettings();
        await settings.SetHarborModeAsync(true);
        var clock = new MutableClock();
        var mgr = NewManager(rules, clock);

        var ownNav = OwnNav(0.0, 0.0, cogRad: 0.0, sogMs: 3.0);
        var vessels = BuildHarbourFleet(200, includeColliders: 10, seed: 0xCA_FE_43);

        mgr.Evaluate(ownNav, vessels, settings);
        await Assert.That(mgr.ActiveAlarm).IsNull();
    }

    [Test]
    public async Task Evaluate_RepeatedTicks_StaysFastAndDoesNotLeak()
    {
        // 60 ticks at 1 Hz on a busy harbour. Total budget is generous
        // (5s for 60 evaluations = 83ms per tick on average; if any
        // single tick blows past 100ms in steady state we'd see the
        // sum spike). Catches a leak where state grows per-tick (e.g.
        // a stale-vessel bookkeeping dict that doesn't get cleaned up).
        var rules = BuildRules();
        var settings = new FakeSettings();
        var clock = new MutableClock { Now = DateTime.UtcNow };
        var mgr = NewManager(rules, clock);

        var ownNav = OwnNav(0.0, 0.0, cogRad: 0.0, sogMs: 3.0);
        var vessels = BuildHarbourFleet(200, includeColliders: 0, seed: 0xCA_FE_44);

        var sw = Stopwatch.StartNew();
        for (int tick = 0; tick < 60; tick++)
        {
            // Manager's internal 1 Hz cadence guard means we need to
            // advance the clock between calls or it short-circuits.
            clock.Now = clock.Now.AddSeconds(2);
            mgr.Evaluate(ownNav, vessels, settings);
        }
        sw.Stop();

        // 60 ticks at ~10 ms each in steady state = ~600 ms; budget
        // 2 s gives ~3x slack -- catches a per-tick regression that
        // would push us above the 30 ms / tick the alarm pipeline
        // budgets within the 1 Hz cadence.
        await Assert.That(sw.Elapsed.TotalSeconds)
            .IsLessThan(2.0)
            .Because($"60 ticks of 200-vessel Evaluate should stay <2s; took {sw.ElapsedMilliseconds}ms");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static IAlarmRule[] BuildRules() =>
    [
        new ShallowAlarmRule(),
        new CpaAlarmRule(new MooredVesselTracker()),
        new WindShiftAlarmRule(),
    ];

    private static AlarmManager NewManager(IAlarmRule[] rules, MutableClock clock) =>
        (AlarmManager)Activator.CreateInstance(
            typeof(AlarmManager),
            bindingAttr: System.Reflection.BindingFlags.Instance
                       | System.Reflection.BindingFlags.NonPublic
                       | System.Reflection.BindingFlags.Public,
            binder: null,
            args: [(IEnumerable<IAlarmRule>)rules, (Func<DateTime>)(() => clock.Now)],
            culture: null)!;

    private static NavigationData OwnNav(double lat, double lon, double cogRad, double sogMs)
    {
        var n = new NavigationData();
        n.ApplyPosition(lat, lon);
        n.Apply("navigation.courseOverGroundTrue", cogRad);
        n.Apply("navigation.speedOverGround", sogMs);
        return n;
    }

    private static List<AisVessel> BuildHarbourFleet(int count, int includeColliders, int seed)
    {
        var rng = new Random(seed);
        var fleet = new List<AisVessel>(count);

        // Background traffic: random positions in a 4-NM box around
        // own boat, random courses, mostly slow.
        for (int i = 0; i < count - includeColliders; i++)
        {
            var v = new AisVessel($"vessels.urn:mrn:imo:mmsi:21000{i:D4}");
            // ~4 NM per degree at the equator so 0.06 deg ~= 4 NM.
            v.Latitude = (rng.NextDouble() - 0.5) * 0.06;
            v.Longitude = (rng.NextDouble() - 0.5) * 0.06;
            v.CourseOverGround = rng.NextDouble() * Math.PI * 2.0;
            v.SpeedOverGround = 0.5 + rng.NextDouble() * 5.0;
            fleet.Add(v);
        }

        // Colliders: positioned dead ahead at varying distances within
        // the 30-min lookahead window, heading reciprocal at 5 m/s.
        // Closing speed = 8 m/s; from 0.005 deg (~0.3 NM) the CPA is
        // well inside the default guard zone.
        for (int i = 0; i < includeColliders; i++)
        {
            var v = new AisVessel($"vessels.urn:mrn:imo:mmsi:99000{i:D4}");
            v.Latitude = 0.005 + i * 0.001;        // ahead, north of own
            v.Longitude = 0.0;
            v.CourseOverGround = Math.PI;          // heading south
            v.SpeedOverGround = 5.0;
            fleet.Add(v);
        }
        return fleet;
    }

    private sealed class MutableClock { public DateTime Now { get; set; } = DateTime.UtcNow; }
}

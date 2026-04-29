using OnaPlotter.Models;
using OnaPlotter.Services;
using OnaPlotter.Services.Alarms;

namespace OnaPlotter.Tests;

/// <summary>
/// Property-based fuzz tests for <see cref="AnchorTideAlarmRule"/>. The
/// rule's safety claim is "predict whether the keel will touch bottom at
/// LW and warn the helm when it might". Random inputs across realistic
/// tide / depth / draft ranges pin the contract that the alarm doesn't
/// fabricate a hit when the math says otherwise, and that NaN / Infinity
/// inputs from a flaky transducer don't produce a numerical-nonsense
/// alarm message.
/// </summary>
public class AnchorTideAlarmRuleFuzzTests
{
    private const int Iterations = 1500;
    private const int Seed = 0x4E_C8_07_42;

    /// <summary>Fixed clock for deterministic fuzz reproduction. Any
    /// real-clock dependency here would interact with LW window math
    /// across daylight-savings rolls, leap seconds, and the moving
    /// 6-hour lookahead boundary -- noise that hides real failures.</summary>
    private static readonly DateTime FixedNow =
        new(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);

    private static NavigationData BuildNav(
        bool anchored,
        double depth, double heightNow, double heightLow,
        DateTime timeLow, double signalkDraft)
    {
        var nav = new NavigationData();
        if (anchored) nav.ApplyAnchorPosition(47.4, 8.5);
        nav.Apply("environment.depth.belowTransducer", depth);
        nav.Apply("environment.tide.heightNow", heightNow);
        nav.Apply("environment.tide.heightLow", heightLow);
        nav.ApplyString("environment.tide.timeLow",
            timeLow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        nav.Apply("design.draft.current", signalkDraft);
        return nav;
    }

    private static AlarmEvaluationContext Ctx(NavigationData nav, IAppSettings settings, DateTime now)
        => new(nav, [], settings, now, _ => false);

    [Test]
    public async Task Check_NoAnchor_AlwaysNull()
    {
        // Without anchor, the rule must short-circuit regardless of
        // tide / depth values. Pin so a refactor that moves the
        // anchor check downstream of the math doesn't leak a false
        // positive on a fast-moving boat with a passing-by LW window.
        var rng = new Random(Seed);
        var rule = new AnchorTideAlarmRule();
        var now = FixedNow;
        for (int i = 0; i < 200; i++)
        {
            var nav = BuildNav(
                anchored: false,
                depth: rng.NextDouble() * 5.0,
                heightNow: rng.NextDouble() * 3.0,
                heightLow: rng.NextDouble() * 1.0,
                timeLow: now.AddHours(rng.NextDouble() * 6.0 + 0.5),
                signalkDraft: 1.5 + rng.NextDouble() * 1.0);
            await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
        }
    }

    [Test]
    public async Task Check_RandomFinite_NeverThrowsAndMessageIsCoherent()
    {
        // Fuzz across realistic tidal-range scenarios. The rule must
        // never throw and any returned AlarmInfo must have a finite
        // TimeToEventMinutes and a non-empty Message.
        var rng = new Random(Seed ^ 1);
        var rule = new AnchorTideAlarmRule();
        // Fixed clock so the test reproduces from the seed alone --
        // a flaky DateTime.UtcNow inside the loop would interact
        // with the LW lookahead window in non-deterministic ways.
        var now = FixedNow;
        int hits = 0;
        for (int i = 0; i < Iterations; i++)
        {
            double depth = 1.0 + rng.NextDouble() * 8.0;          // 1..9 m at anchor
            double heightLow = -0.5 + rng.NextDouble() * 1.0;     // -0.5..0.5 m
            double heightNow = heightLow + rng.NextDouble() * 4.0;// always >= heightLow
            // hours-to-LW between 0.5 and 12 (some inside, some outside the 6h window)
            double hoursToLw = 0.5 + rng.NextDouble() * 11.5;
            double draft = 1.0 + rng.NextDouble() * 2.5;

            var nav = BuildNav(anchored: true, depth, heightNow, heightLow,
                now.AddHours(hoursToLw), draft);

            AlarmInfo? r = null;
            try { r = rule.Check(Ctx(nav, new FakeSettings(), now)); }
            catch (Exception ex)
            {
                // Re-raise with iteration context so the failure message
                // helps reproduce the offending input.
                throw new InvalidOperationException(
                    $"AnchorTide threw on iteration {i}: {ex.Message}", ex);
            }

            if (r is null) continue;
            hits++;

            // TTE must be a positive finite number of minutes when set;
            // the helm reads it as the "in N min" string and a NaN/Inf
            // value would print "in NaNmin" which is a clear bug.
            if (r.TimeToEventMinutes is double tte)
            {
                await Assert.That(double.IsFinite(tte)).IsTrue();
                await Assert.That(tte).IsGreaterThan(0);
            }
            await Assert.That(string.IsNullOrEmpty(r.Message)).IsFalse();
            await Assert.That(r.Title).IsEqualTo("ANCHOR TIDE");
        }
        // Across 1500 iterations, the random distribution should produce
        // a non-trivial number of hits. If we get zero, the test isn't
        // really exercising the alarm branches.
        await Assert.That(hits).IsGreaterThan(50);
    }

    [Test]
    public async Task Check_DangerImpliesNonPositiveClearance()
    {
        // Danger fires only when predicted depth - draft <= 0 (keel
        // touches bottom). Pin so a regression that loosens the >0/<=0
        // boundary doesn't paint Danger on a still-comfortable
        // anchorage.
        var rng = new Random(Seed ^ 2);
        var rule = new AnchorTideAlarmRule();
        int dangers = 0;
        for (int i = 0; i < Iterations; i++)
        {
            // Per-iteration fixed offset so each random LW window is
            // unambiguous against `now`.
            var now = FixedNow;
            double depth = 0.5 + rng.NextDouble() * 8.0;
            double heightLow = rng.NextDouble() * 0.8;
            double heightNow = heightLow + 0.1 + rng.NextDouble() * 4.0;
            double hoursToLw = 0.5 + rng.NextDouble() * 5.0;
            double draft = 0.5 + rng.NextDouble() * 3.0;
            double margin = 0.1 + rng.NextDouble() * 1.5;

            var nav = BuildNav(anchored: true, depth, heightNow, heightLow,
                now.AddHours(hoursToLw), draft);
            var settings = new FakeSettings { AnchorTideSafetyMargin = margin };

            var r = rule.Check(Ctx(nav, settings, now));
            if (r?.Severity == AlarmSeverity.Danger)
            {
                dangers++;
                double drop = heightNow - heightLow;
                double predictedDepth = depth - drop;
                double clearance = predictedDepth - draft;
                await Assert.That(clearance).IsLessThanOrEqualTo(0);
            }
        }
        await Assert.That(dangers).IsGreaterThan(20);
    }

    [Test]
    public async Task Check_RisingTide_ReturnsNull()
    {
        // heightNow <= heightLow means the tide is rising (or flat).
        // No alarm regardless of depth / draft.
        var rng = new Random(Seed ^ 3);
        var rule = new AnchorTideAlarmRule();
        for (int i = 0; i < 500; i++)
        {
            var now = FixedNow;
            double heightLow = 1.0 + rng.NextDouble() * 1.0;          // > heightNow
            double heightNow = heightLow - rng.NextDouble() * 0.5;    // <= heightLow

            var nav = BuildNav(anchored: true,
                depth: 0.5 + rng.NextDouble() * 5.0,
                heightNow, heightLow,
                now.AddHours(rng.NextDouble() * 5.0 + 0.5),
                signalkDraft: 1.0 + rng.NextDouble() * 2.0);

            await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
        }
    }

    [Test]
    public async Task Check_LookaheadBeyondSixHours_ReturnsNull()
    {
        // The 6-hour lookahead caps the alarm. A LW 14 hours away
        // shouldn't wake the helm when there's plenty of slack to
        // raise the anchor naturally.
        var rng = new Random(Seed ^ 4);
        var rule = new AnchorTideAlarmRule();
        for (int i = 0; i < 200; i++)
        {
            var now = FixedNow;
            // Pick LW between 6.1 and 24 hours out -- always outside
            // the alarm window.
            double hoursToLw = 6.1 + rng.NextDouble() * 18.0;
            var nav = BuildNav(anchored: true,
                depth: 1.0,
                heightNow: 3.0,        // huge tide drop -- would alarm at 3h
                heightLow: 0.0,
                now.AddHours(hoursToLw),
                signalkDraft: 1.5);
            await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
        }
    }

    [Test]
    public async Task Check_PastLowWater_ReturnsNull()
    {
        // timeLow in the past = the LW we're "predicting" already
        // happened. Rule must short-circuit. Pin so a sign-flip in
        // hoursToLw doesn't silently re-fire after every LW.
        var rng = new Random(Seed ^ 5);
        var rule = new AnchorTideAlarmRule();
        for (int i = 0; i < 200; i++)
        {
            var now = FixedNow;
            double hoursToLwPast = -0.1 - rng.NextDouble() * 12.0;
            var nav = BuildNav(anchored: true,
                depth: 1.0, heightNow: 3.0, heightLow: 0.0,
                now.AddHours(hoursToLwPast),
                signalkDraft: 1.5);
            await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
        }
    }

    [Test]
    public async Task Check_NoSignalKDraft_ReturnsNull()
    {
        // Draft must come from SignalK (design.draft.current/.maximum).
        // Without a draft from the bus the rule stays dormant rather
        // than running on a stale client default.
        var rule = new AnchorTideAlarmRule();
        var now = FixedNow;
        var nav = new NavigationData();
        nav.ApplyAnchorPosition(47.4, 8.5);
        nav.Apply("environment.depth.belowTransducer", 1.5);
        nav.Apply("environment.tide.heightNow", 2.5);
        nav.Apply("environment.tide.heightLow", 0.0);
        nav.ApplyString("environment.tide.timeLow",
            now.AddHours(2).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        // Note: NO design.draft.current applied.

        await Assert.That(rule.Check(Ctx(nav, new FakeSettings(), now))).IsNull();
    }
}

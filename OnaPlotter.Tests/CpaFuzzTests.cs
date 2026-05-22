using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Property-based fuzz tests for <see cref="Cpa"/>. Random inputs across
/// realistic ranges feed Compute and ClassifyThreat; the invariants below
/// pin behaviour the alarm pipeline depends on. Hand-picked geometries
/// live in <see cref="CpaTests"/>; this file is the random-input
/// belt-and-braces.
///
/// <para>Fuzz iterations are deterministic-seeded so a regression failure
/// is reproducible. Bumping iterations costs CPU but never changes the
/// invariants, so a future tightening (e.g. enabling NaN guards on every
/// math.* call) keeps passing.</para>
/// </summary>
public class CpaFuzzTests
{
    private const int Iterations = 2000;
    private const int Seed = unchecked((int)0xC0_FF_EE_42);

    /// <summary>Realistic vessel SOG range: 0..50 kn (0..25 m/s).</summary>
    private const double MaxSogMs = 25.0;

    [Test]
    public async Task Compute_RandomInputs_AlwaysReturnNullOrFiniteNonNegative()
    {
        // Most basic invariant: Compute never returns NaN/Infinity, never
        // negative CPA distance, never negative TCPA. A regression here
        // (e.g. dropping the IsFinite guards) would silently feed garbage
        // into CpaAlarmRule which then alarms or mis-classifies.
        var rng = new Random(Seed);
        int returned = 0;
        for (int i = 0; i < Iterations; i++)
        {
            // Realistic lat / lon (avoid poles where the equirectangular
            // patch degenerates), random COG, SOG up to 25 m/s.
            double lat1 = rng.NextDouble() * 160.0 - 80.0;
            double lon1 = rng.NextDouble() * 360.0 - 180.0;
            double lat2 = rng.NextDouble() * 160.0 - 80.0;
            double lon2 = rng.NextDouble() * 360.0 - 180.0;
            double cog1 = rng.NextDouble() * Math.PI * 2.0;
            double cog2 = rng.NextDouble() * Math.PI * 2.0;
            double sog1 = rng.NextDouble() * MaxSogMs;
            double sog2 = rng.NextDouble() * MaxSogMs;

            var r = Cpa.Compute(lat1, lon1, cog1, sog1, lat2, lon2, cog2, sog2);
            if (r is null) continue;
            returned++;

            await Assert.That(double.IsFinite(r.Value.CpaNm)).IsTrue();
            await Assert.That(double.IsFinite(r.Value.TcpaMin)).IsTrue();
            await Assert.That(r.Value.CpaNm).IsGreaterThanOrEqualTo(0.0);
            await Assert.That(r.Value.TcpaMin).IsGreaterThanOrEqualTo(0.0);
        }
        // Sanity: across 2000 random pairs we should hit the converging
        // branch at least sometimes; if every iteration returned null
        // the test isn't actually exercising the math.
        await Assert.That(returned).IsGreaterThan(50)
            .Because("random inputs should yield a non-trivial number of CPA hits");
    }

    [Test]
    public async Task Compute_NaNInputs_AlwaysReturnNull()
    {
        // NaN propagation through Math.Sin / Cos / Sqrt would yield a
        // NaN CpaNm that compares false against any threshold and
        // silently disables the alarm. The IsFinite guards in Compute
        // exist precisely to short-circuit before that happens.
        var rng = new Random(Seed ^ 1);
        for (int i = 0; i < 500; i++)
        {
            // Pick one position to be NaN.
            int pick = rng.Next(8);
            double[] args = [
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, rng.NextDouble() * MaxSogMs,
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, rng.NextDouble() * MaxSogMs,
            ];
            args[pick] = double.NaN;
            var r = Cpa.Compute(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]);
            await Assert.That(r).IsNull();
        }
    }

    [Test]
    public async Task Compute_InfiniteInputs_AlwaysReturnNull()
    {
        // Symmetric guard against Infinity inputs (an unbounded SOG
        // coming from a buggy AIS-decoder bridge, or a cog of +Inf
        // from a divide-by-zero somewhere upstream).
        var rng = new Random(Seed ^ 2);
        for (int i = 0; i < 500; i++)
        {
            int pick = rng.Next(8);
            double sign = rng.Next(2) == 0 ? -1.0 : 1.0;
            double[] args = [
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, rng.NextDouble() * MaxSogMs,
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, rng.NextDouble() * MaxSogMs,
            ];
            args[pick] = sign * double.PositiveInfinity;
            var r = Cpa.Compute(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]);
            await Assert.That(r).IsNull();
        }
    }

    [Test]
    public async Task Compute_Symmetric_SwappingVesselsGivesSameCpaNm()
    {
        // CPA distance is a property of the geometry, not the labelling.
        // Swapping (own, target) must yield the same CpaNm to within
        // floating-point noise. TCPA is also symmetric under swap (both
        // observers see CPA at the same wall-clock time). This test
        // catches regressions where one vessel's projection is computed
        // differently from the other.
        var rng = new Random(Seed ^ 3);
        const double Tolerance = 1e-6;
        int compared = 0;
        for (int i = 0; i < Iterations; i++)
        {
            double lat1 = rng.NextDouble() * 60.0 - 30.0;
            double lon1 = rng.NextDouble() * 60.0 - 30.0;
            double lat2 = rng.NextDouble() * 60.0 - 30.0;
            double lon2 = rng.NextDouble() * 60.0 - 30.0;
            double cog1 = rng.NextDouble() * Math.PI * 2.0;
            double cog2 = rng.NextDouble() * Math.PI * 2.0;
            double sog1 = rng.NextDouble() * MaxSogMs;
            double sog2 = rng.NextDouble() * MaxSogMs;

            var r1 = Cpa.Compute(lat1, lon1, cog1, sog1, lat2, lon2, cog2, sog2);
            var r2 = Cpa.Compute(lat2, lon2, cog2, sog2, lat1, lon1, cog1, sog1);

            if (r1 is null || r2 is null)
            {
                // Both must agree on null vs non-null (the stationary
                // pair short-circuit doesn't depend on argument order).
                await Assert.That(r1.HasValue).IsEqualTo(r2.HasValue);
                continue;
            }

            compared++;
            await Assert.That(Math.Abs(r1.Value.CpaNm - r2.Value.CpaNm))
                .IsLessThan(Tolerance);
            await Assert.That(Math.Abs(r1.Value.TcpaMin - r2.Value.TcpaMin))
                .IsLessThan(Tolerance);
        }
        await Assert.That(compared).IsGreaterThan(50);
    }

    [Test]
    public async Task Compute_AntimeridianShift_DoesNotChangeCpaNm()
    {
        // Shifting both lons by +/- 360 degrees is a pure relabelling
        // of the same point on the sphere. The wraparound logic in
        // Compute (the "((lon2 - lon1 + 540) % 360) - 180" trick) must
        // make CpaNm invariant under this shift. A regression here is
        // exactly the "alarm never fires near the dateline" bug the
        // antimeridian comment calls out.
        var rng = new Random(Seed ^ 4);
        int compared = 0;
        for (int i = 0; i < Iterations; i++)
        {
            double lat1 = rng.NextDouble() * 60.0 - 30.0;
            double lon1 = rng.NextDouble() * 360.0 - 180.0;
            double lat2 = rng.NextDouble() * 60.0 - 30.0;
            double lon2 = rng.NextDouble() * 360.0 - 180.0;
            double cog1 = rng.NextDouble() * Math.PI * 2.0;
            double cog2 = rng.NextDouble() * Math.PI * 2.0;
            double sog1 = rng.NextDouble() * MaxSogMs;
            double sog2 = rng.NextDouble() * MaxSogMs;

            var r1 = Cpa.Compute(lat1, lon1, cog1, sog1, lat2, lon2, cog2, sog2);
            // Shift both lons by +360 - still the same physical points.
            var r2 = Cpa.Compute(lat1, lon1 + 360.0, cog1, sog1, lat2, lon2 + 360.0, cog2, sog2);

            if (r1 is null || r2 is null)
            {
                await Assert.That(r1.HasValue).IsEqualTo(r2.HasValue);
                continue;
            }

            compared++;
            // 1e-3 nm tolerance (~1.85 m): the equirectangular projection
            // introduces a few ppm drift across the ((..)+540)%360 path,
            // which on a few-tens-of-nm separation is sub-metre. Allow
            // up to ~2 m of drift for the worst-case antimeridian shift
            // before flagging.
            await Assert.That(Math.Abs(r1.Value.CpaNm - r2.Value.CpaNm))
                .IsLessThan(1e-3);
        }
        await Assert.That(compared).IsGreaterThan(50);
    }

    // === ClassifyThreat ===

    [Test]
    public async Task Compute_Monotone_FartherInitialSeparationNeverYieldsCloserCpa()
    {
        // For the same headings + speeds, moving the target farther
        // along the bearing line at t=0 must NOT decrease CpaNm.
        // (CPA distance is a perpendicular projection that scales with
        // the orthogonal component of separation; the parallel
        // component pushes TCPA later but doesn't change perpendicular
        // distance. So CpaNm is non-decreasing in initial separation
        // along the closing axis.) A regression in the dx/dy projection
        // axes (a typo lat<->lon swap) would break this immediately.
        var rng = new Random(Seed ^ 8);
        int compared = 0;
        for (int i = 0; i < Iterations; i++)
        {
            // Set up a converging encounter: own at origin going north,
            // target a north-of-origin pin going south (head-on).
            double sog = 3.0 + rng.NextDouble() * 5.0;
            double dLat = 0.005 + rng.NextDouble() * 0.05;
            double dLon = (rng.NextDouble() - 0.5) * 0.005;       // small lateral offset

            var rNear = Cpa.Compute(0, 0, 0, sog, dLat, dLon, Math.PI, sog);
            // Push target 2x farther along the closing axis.
            var rFar = Cpa.Compute(0, 0, 0, sog, dLat * 2, dLon * 2, Math.PI, sog);

            if (rNear is null || rFar is null) continue;
            compared++;

            // Both should be valid finite CpaNm values, and the farther
            // initial geometry must yield CpaNm >= the near geometry's
            // (modulo float jitter). Pin with a relaxed 1e-6 tolerance.
            await Assert.That(rFar.Value.CpaNm + 1e-6)
                .IsGreaterThanOrEqualTo(rNear.Value.CpaNm)
                .Because("doubling lateral offset should not produce a smaller CPA");
        }
        await Assert.That(compared).IsGreaterThan(50);
    }

    [Test]
    public async Task ClassifyThreat_BuddyAlwaysNone()
    {
        // The buddy-list opt-out must hold regardless of CPA / TCPA
        // values. A regression that ignored the buddy flag would paint
        // a friend's boat red on the chart - guaranteed UX bug.
        var rng = new Random(Seed ^ 5);
        for (int i = 0; i < 500; i++)
        {
            double cpa = rng.NextDouble() * 5.0;
            double tcpa = rng.NextDouble() * 30.0;
            double alarmCpa = rng.NextDouble() * 2.0 + 0.1;
            double alarmTcpa = rng.NextDouble() * 30.0 + 1.0;
            double awarenessCpa = alarmCpa + rng.NextDouble() * 2.0;
            double awarenessTcpa = alarmTcpa + rng.NextDouble() * 30.0;

            var t = Cpa.ClassifyThreat(cpa, tcpa,
                alarmCpa, alarmTcpa, awarenessCpa, awarenessTcpa, isBuddy: true);
            await Assert.That(t).IsEqualTo(Cpa.Threat.None);
        }
    }

    [Test]
    public async Task ClassifyThreat_AlarmImpliesInsideAlarmTier()
    {
        // If the result is Alarm, the inputs must satisfy: the
        // projected CPA is within the alarm-tier CPA limit AND the
        // TCPA is inside the alarm-tier window. Catches a regression
        // where someone widens the band accidentally (e.g. replacing
        // <= with <, or using the awareness threshold for the alarm
        // gate).
        var rng = new Random(Seed ^ 6);
        int sawAlarm = 0;
        for (int i = 0; i < 1000; i++)
        {
            double cpa = rng.NextDouble() * 5.0;
            double tcpa = rng.NextDouble() * 30.0 + 0.01;       // > 0 to avoid the early-exit
            double alarmCpa = rng.NextDouble() * 2.0 + 0.1;
            double alarmTcpa = rng.NextDouble() * 30.0 + 1.0;
            double awarenessCpa = alarmCpa + rng.NextDouble() * 2.0;
            double awarenessTcpa = alarmTcpa + rng.NextDouble() * 30.0;

            var t = Cpa.ClassifyThreat(cpa, tcpa,
                alarmCpa, alarmTcpa, awarenessCpa, awarenessTcpa, isBuddy: false);
            if (t == Cpa.Threat.Alarm)
            {
                sawAlarm++;
                await Assert.That(cpa).IsLessThanOrEqualTo(alarmCpa);
                await Assert.That(tcpa).IsLessThanOrEqualTo(alarmTcpa);
            }
        }
        await Assert.That(sawAlarm).IsGreaterThan(20);
    }

    [Test]
    public async Task ClassifyThreat_AwarenessImpliesInsideAwarenessTier()
    {
        // Awareness fires when the projected CPA is inside the
        // awareness-tier CPA limit, the TCPA is inside the awareness
        // window, AND the alarm-tier criteria aren't simultaneously
        // satisfied (otherwise it would be Alarm). Pin both halves so
        // the band is provably narrower than "anything that doesn't
        // alarm".
        var rng = new Random(Seed ^ 7);
        int sawAwareness = 0;
        for (int i = 0; i < 1000; i++)
        {
            double cpa = rng.NextDouble() * 5.0;
            double tcpa = rng.NextDouble() * 30.0 + 0.01;
            double alarmCpa = rng.NextDouble() * 2.0 + 0.1;
            double alarmTcpa = rng.NextDouble() * 30.0 + 1.0;
            double awarenessCpa = alarmCpa + rng.NextDouble() * 2.0;
            double awarenessTcpa = alarmTcpa + rng.NextDouble() * 30.0;

            var t = Cpa.ClassifyThreat(cpa, tcpa,
                alarmCpa, alarmTcpa, awarenessCpa, awarenessTcpa, isBuddy: false);
            if (t == Cpa.Threat.Awareness)
            {
                sawAwareness++;
                await Assert.That(cpa).IsLessThanOrEqualTo(awarenessCpa);
                await Assert.That(tcpa).IsLessThanOrEqualTo(awarenessTcpa);
                // Not simultaneously inside the alarm tier (else it
                // would have classified Alarm).
                bool insideAlarm = cpa <= alarmCpa && tcpa <= alarmTcpa;
                await Assert.That(insideAlarm).IsFalse();
            }
        }
        await Assert.That(sawAwareness).IsGreaterThan(20);
    }

    [Test]
    public async Task ClassifyThreat_NoneOutsideAwarenessCpa()
    {
        // Symmetric: if projected CPA exceeds the awareness-tier limit,
        // classification is always None regardless of TCPA, because the
        // awareness band is the outer envelope. This is the anti-
        // clutter contract.
        var rng = new Random(Seed ^ 8);
        int checks = 0;
        for (int i = 0; i < 1000; i++)
        {
            double alarmCpa = rng.NextDouble() * 1.0 + 0.1;
            double alarmTcpa = rng.NextDouble() * 20.0 + 5.0;
            double awarenessCpa = alarmCpa + rng.NextDouble() * 1.0;
            double awarenessTcpa = alarmTcpa + rng.NextDouble() * 20.0;
            // Force cpa outside the awareness band.
            double cpa = awarenessCpa + 0.1 + rng.NextDouble() * 5.0;
            double tcpa = rng.NextDouble() * 5.0 + 0.5;        // otherwise eligible

            var t = Cpa.ClassifyThreat(cpa, tcpa,
                alarmCpa, alarmTcpa, awarenessCpa, awarenessTcpa, isBuddy: false);
            await Assert.That(t).IsEqualTo(Cpa.Threat.None);
            checks++;
        }
        await Assert.That(checks).IsGreaterThan(900);
    }

    [Test]
    public async Task ClassifyThreat_NullOrZeroTcpaIsNone()
    {
        // tcpa <= 0 means "CPA is in the past" - the rule must say
        // None, not Awareness, regardless of cpa value. Mirror of the
        // null-cpa case.
        await Assert.That(Cpa.ClassifyThreat(0.1, null,
            0.5, 10.0, 1.0, 10.0, isBuddy: false))
            .IsEqualTo(Cpa.Threat.None);
        await Assert.That(Cpa.ClassifyThreat(null, 5.0,
            0.5, 10.0, 1.0, 10.0, isBuddy: false))
            .IsEqualTo(Cpa.Threat.None);
        await Assert.That(Cpa.ClassifyThreat(0.1, 0.0,
            0.5, 10.0, 1.0, 10.0, isBuddy: false))
            .IsEqualTo(Cpa.Threat.None);
        await Assert.That(Cpa.ClassifyThreat(0.1, -5.0,
            0.5, 10.0, 1.0, 10.0, isBuddy: false))
            .IsEqualTo(Cpa.Threat.None);
    }
}

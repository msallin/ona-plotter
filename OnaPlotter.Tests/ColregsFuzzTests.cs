using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>
/// Property-based fuzz tests for <see cref="Colregs"/>. Hand-picked cases
/// live in <see cref="ColregsTests"/>; this file pins invariants under
/// random encounter geometries (latitude / longitude / COG / SOG sweeps).
///
/// <para>The high-value invariants the popup label depends on:
/// reciprocity (swapping own/target maps Overtaking <-> BeingOvertaken),
/// NaN/Infinity short-circuits to Indeterminate, and the give-way role
/// is always consistent with the category (no Crossing-FromStarboard
/// labelled StandOn, etc.).</para>
/// </summary>
public class ColregsFuzzTests
{
    private const int Iterations = 2000;
    private const int Seed = unchecked((int)0x80_C0_18_42);
    private const double MaxSogMs = 25.0;
    private const double Stationary = 0.05;     // < 0.1 -> Indeterminate

    [Test]
    public async Task Classify_RandomInputs_ReturnsConsistentRoleForCategory()
    {
        // Pin the per-category role: every category has exactly one role
        // mapping. A regression that flipped (e.g. Overtaking is now
        // StandOn) would silently mis-label the helm's duty in a way
        // the watch would only catch when comparing against the
        // chartplotter on another boat.
        var rng = new Random(Seed);
        for (int i = 0; i < Iterations; i++)
        {
            var r = Colregs.Classify(
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, Stationary + rng.NextDouble() * MaxSogMs,
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, Stationary + rng.NextDouble() * MaxSogMs);

            // Role/Category contract:
            //   HeadOn -> GiveWay (Rule 14: each turns starboard,
            //     but UI labels both as GiveWay since neither stands on)
            //   Overtaking -> GiveWay (Rule 13)
            //   BeingOvertaken -> StandOn (Rule 13 from the other side)
            //   CrossingFromStarboard -> GiveWay (Rule 15)
            //   CrossingFromPort -> StandOn (Rule 17)
            //   Indeterminate -> None
            switch (r.Category)
            {
                case Colregs.Category.HeadOn:
                case Colregs.Category.Overtaking:
                case Colregs.Category.CrossingFromStarboard:
                    await Assert.That(r.Role).IsEqualTo(Colregs.Role.GiveWay);
                    break;
                case Colregs.Category.BeingOvertaken:
                case Colregs.Category.CrossingFromPort:
                    await Assert.That(r.Role).IsEqualTo(Colregs.Role.StandOn);
                    break;
                case Colregs.Category.Indeterminate:
                    await Assert.That(r.Role).IsEqualTo(Colregs.Role.None);
                    break;
            }
        }
    }

    [Test]
    public async Task Classify_NaNInputs_AlwaysIndeterminate()
    {
        var rng = new Random(Seed ^ 1);
        for (int i = 0; i < 500; i++)
        {
            int pick = rng.Next(8);
            double[] args = [
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, Stationary + rng.NextDouble() * MaxSogMs,
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, Stationary + rng.NextDouble() * MaxSogMs,
            ];
            args[pick] = double.NaN;
            var r = Colregs.Classify(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]);
            await Assert.That(r.Category).IsEqualTo(Colregs.Category.Indeterminate);
            await Assert.That(r.Role).IsEqualTo(Colregs.Role.None);
        }
    }

    [Test]
    public async Task Classify_InfiniteInputs_AlwaysIndeterminate()
    {
        var rng = new Random(Seed ^ 2);
        for (int i = 0; i < 500; i++)
        {
            int pick = rng.Next(8);
            double sign = rng.Next(2) == 0 ? -1.0 : 1.0;
            double[] args = [
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, Stationary + rng.NextDouble() * MaxSogMs,
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, Stationary + rng.NextDouble() * MaxSogMs,
            ];
            args[pick] = sign * double.PositiveInfinity;
            var r = Colregs.Classify(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]);
            await Assert.That(r.Category).IsEqualTo(Colregs.Category.Indeterminate);
        }
    }

    [Test]
    public async Task Classify_OwnStationary_AlwaysIndeterminate()
    {
        // The COLREGS rules assume both vessels are under way with
        // steerage. A drifting own-boat must NOT be labelled "Stand-on"
        // by being-overtaken logic, and a stationary target must NOT
        // pin its own duty as "Give-way". Pin the Indeterminate exit.
        var rng = new Random(Seed ^ 3);
        for (int i = 0; i < 500; i++)
        {
            // Make own stationary; target moving fast.
            var r = Colregs.Classify(
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, 0.05,    // < 0.1
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, 5.0 + rng.NextDouble() * MaxSogMs);
            await Assert.That(r.Category).IsEqualTo(Colregs.Category.Indeterminate);
        }
    }

    [Test]
    public async Task Classify_TargetStationary_AlwaysIndeterminate()
    {
        // Symmetric to the previous test.
        var rng = new Random(Seed ^ 4);
        for (int i = 0; i < 500; i++)
        {
            var r = Colregs.Classify(
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, 5.0 + rng.NextDouble() * MaxSogMs,
                rng.NextDouble() * 60.0 - 30.0, rng.NextDouble() * 60.0 - 30.0,
                rng.NextDouble() * Math.PI * 2.0, 0.05);
            await Assert.That(r.Category).IsEqualTo(Colregs.Category.Indeterminate);
        }
    }

    [Test]
    public async Task Classify_OvertakingAndBeingOvertakenAreReciprocal()
    {
        // For aligned-heading encounters where one boat is faster, the
        // classification from "own's" point of view should mirror the
        // reverse from "target's". I.e. Classify(own, target) =
        // Overtaking implies Classify(target, own) = BeingOvertaken
        // (and vice versa). Walk the same encounter twice with swapped
        // roles to pin this.
        var rng = new Random(Seed ^ 5);
        int matched = 0;
        for (int i = 0; i < Iterations; i++)
        {
            // Same heading +/- small jitter so we land in the
            // headingDiff < 22.5 branch.
            double cog = rng.NextDouble() * Math.PI * 2.0;
            double cogJitter = (rng.NextDouble() - 0.5) * 0.1;       // ~6 degrees max
            double sogA = 5.0 + rng.NextDouble() * 10.0;
            double sogB = 5.0 + rng.NextDouble() * 10.0;
            double latA = rng.NextDouble() * 1.0 - 0.5;
            double lonA = rng.NextDouble() * 1.0 - 0.5;
            // Place B somewhere along A's heading axis (0.01 deg ~ 1 km)
            double dist = (rng.NextDouble() - 0.5) * 0.05;            // can be ahead or astern
            double latB = latA + Math.Cos(cog) * dist;
            double lonB = lonA + Math.Sin(cog) * dist;

            var r1 = Colregs.Classify(latA, lonA, cog, sogA, latB, lonB, cog + cogJitter, sogB);
            var r2 = Colregs.Classify(latB, lonB, cog + cogJitter, sogB, latA, lonA, cog, sogA);

            if (r1.Category == Colregs.Category.Overtaking)
            {
                matched++;
                await Assert.That(r2.Category)
                    .IsEqualTo(Colregs.Category.BeingOvertaken)
                    .Because("if A is overtaking B, B sees A as catching up from astern");
                await Assert.That(r1.Role).IsEqualTo(Colregs.Role.GiveWay);
                await Assert.That(r2.Role).IsEqualTo(Colregs.Role.StandOn);
            }
            else if (r1.Category == Colregs.Category.BeingOvertaken)
            {
                matched++;
                await Assert.That(r2.Category)
                    .IsEqualTo(Colregs.Category.Overtaking);
            }
        }
        await Assert.That(matched).IsGreaterThan(20)
            .Because("the random walk should hit the overtaking branch sometimes");
    }

    [Test]
    public async Task Classify_CrossingFromStarboardAndPortAreReciprocal()
    {
        // When A sees B coming from starboard (give-way for A), B sees
        // A coming from port (stand-on for B). Pin this; a regression
        // that swapped the port/stbd labels would silently flip every
        // crossing duty across the chartplotter.
        var rng = new Random(Seed ^ 6);
        int matched = 0;
        for (int i = 0; i < Iterations; i++)
        {
            double cogA = rng.NextDouble() * Math.PI * 2.0;
            // Force perpendicular-ish encounter so we hit a crossing.
            double cogB = cogA + Math.PI * 0.5 + (rng.NextDouble() - 0.5) * 0.5;
            double sogA = 5.0 + rng.NextDouble() * 10.0;
            double sogB = 5.0 + rng.NextDouble() * 10.0;
            double latA = rng.NextDouble() * 1.0 - 0.5;
            double lonA = rng.NextDouble() * 1.0 - 0.5;
            double offset = 0.005 + rng.NextDouble() * 0.02;
            // Place B somewhere off A's beam.
            double bearingFromA = cogA + (rng.Next(2) == 0 ? Math.PI / 2 : -Math.PI / 2);
            double latB = latA + Math.Cos(bearingFromA) * offset;
            double lonB = lonA + Math.Sin(bearingFromA) * offset;

            var r1 = Colregs.Classify(latA, lonA, cogA, sogA, latB, lonB, cogB, sogB);
            var r2 = Colregs.Classify(latB, lonB, cogB, sogB, latA, lonA, cogA, sogA);

            if (r1.Category == Colregs.Category.CrossingFromStarboard)
            {
                matched++;
                // From B's perspective, A approaches from B's port side
                // (assuming non-degenerate geometry). Allow either
                // CrossingFromPort or HeadOn (when the perpendicular
                // assumption decays into a near-reciprocal).
                await Assert.That(r2.Category != Colregs.Category.CrossingFromStarboard)
                    .IsTrue()
                    .Because("a single encounter cannot be 'starboard crossing' for both vessels");
            }
            else if (r1.Category == Colregs.Category.CrossingFromPort)
            {
                matched++;
                await Assert.That(r2.Category != Colregs.Category.CrossingFromPort).IsTrue();
            }
        }
        await Assert.That(matched).IsGreaterThan(20);
    }

    [Test]
    public async Task Classify_HeadOnIsSymmetric()
    {
        // Head-on is the one category where both vessels see the same
        // category from each side. (Rule 14 explicitly says BOTH turn
        // starboard.)
        var rng = new Random(Seed ^ 7);
        int matched = 0;
        for (int i = 0; i < Iterations; i++)
        {
            // Place B ahead of A; make B's heading reciprocal-ish.
            double cog = rng.NextDouble() * Math.PI * 2.0;
            double recip = Norm2Pi(cog + Math.PI + (rng.NextDouble() - 0.5) * 0.1);
            double sog = 5.0 + rng.NextDouble() * 10.0;
            double latA = rng.NextDouble() * 1.0 - 0.5;
            double lonA = rng.NextDouble() * 1.0 - 0.5;
            double offset = 0.005 + rng.NextDouble() * 0.02;
            double latB = latA + Math.Cos(cog) * offset;
            double lonB = lonA + Math.Sin(cog) * offset;

            var r1 = Colregs.Classify(latA, lonA, cog, sog, latB, lonB, recip, sog);
            var r2 = Colregs.Classify(latB, lonB, recip, sog, latA, lonA, cog, sog);

            if (r1.Category == Colregs.Category.HeadOn)
            {
                matched++;
                await Assert.That(r2.Category).IsEqualTo(Colregs.Category.HeadOn);
            }
        }
        await Assert.That(matched).IsGreaterThan(20);
    }

    private static double Norm2Pi(double r)
    {
        r %= Math.PI * 2.0;
        return r < 0 ? r + Math.PI * 2.0 : r;
    }
}

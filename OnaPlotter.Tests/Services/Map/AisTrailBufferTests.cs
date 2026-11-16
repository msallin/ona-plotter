using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the semantics of <see cref="AisTrailBuffer"/>: position dedup
/// on push, age-based trim, and the append/trim delta that keeps the
/// renderer's copy in step without resending the whole window.
/// </summary>
public sealed class AisTrailBufferTests
{
    private static readonly DateTime T0 = new(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Push_FirstFix_AddsPoint_AndReportsChange()
    {
        var buf = new AisTrailBuffer();
        bool changed = buf.Push("v.A", 54.0, 11.0, T0);

        await Assert.That(changed).IsTrue();
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
        await Assert.That(buf.ConsumeDelta("v.A").Append).IsEquivalentTo(new[] { 54.0, 11.0 });
    }

    [Test]
    public async Task Push_SamePosition_IsDedupedAndReportsNoChange()
    {
        // A moored vessel pinging the same coords every 3 s shouldn't
        // grow its trail.
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        _ = buf.ConsumeDelta("v.A");                     // renderer caught up

        bool changed = buf.Push("v.A", 54.0, 11.0, T0.AddSeconds(3));

        await Assert.That(changed).IsFalse();
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
        await Assert.That(buf.ConsumeDelta("v.A").IsEmpty).IsTrue();
    }

    [Test]
    public async Task Push_DifferentPosition_AppendsOnlyTheNewPoint()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        _ = buf.ConsumeDelta("v.A");

        bool changed = buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(10));

        await Assert.That(changed).IsTrue();
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(2);
        var delta = buf.ConsumeDelta("v.A");
        await Assert.That(delta.Append).IsEquivalentTo(new[] { 54.1, 11.1 });
        await Assert.That(delta.Trim).IsEqualTo(0);
    }

    [Test]
    public async Task Push_AgeTrim_DropsExpiredFronts()
    {
        // Default window is 5 minutes.
        //   T0+0:00 -> dropped (6m30s old)
        //   T0+0:30 -> dropped (6m old)
        //   T0+6:30 -> kept (now)
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(30));
        _ = buf.ConsumeDelta("v.A");

        bool changed = buf.Push("v.A", 54.2, 11.2, T0.AddMinutes(6).AddSeconds(30));

        await Assert.That(changed).IsTrue();
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
        var delta = buf.ConsumeDelta("v.A");
        await Assert.That(delta.Trim).IsEqualTo(2);
        await Assert.That(delta.Append).IsEquivalentTo(new[] { 54.2, 11.2 });
    }

    [Test]
    public async Task Push_AgeTrim_KeepsPointsAtTheBoundary()
    {
        // A point exactly at the cutoff age stays: the trim condition
        // is strict less-than.
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddMinutes(5));

        await Assert.That(buf.CountFor("v.A")).IsEqualTo(2);
    }

    [Test]
    public async Task GetCoords_ReturnsFlatLatLonPairsInInsertionOrder()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(10));
        buf.Push("v.A", 54.2, 11.2, T0.AddSeconds(20));

        var coords = buf.GetCoords("v.A");

        await Assert.That(coords).IsEquivalentTo(new[] { 54.0, 11.0, 54.1, 11.1, 54.2, 11.2 });
    }

    [Test]
    public async Task GetCoords_UnknownContext_ReturnsNull()
    {
        var buf = new AisTrailBuffer();
        await Assert.That(buf.GetCoords("v.never-seen")).IsNull();
    }

    [Test]
    public async Task GetCoords_SinglePoint_IsReturned()
    {
        // A full sync carries a lone point too, so the renderer's copy
        // grows into a drawable two-point trail on the next append.
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        await Assert.That(buf.GetCoords("v.A")).IsEquivalentTo(new[] { 54.0, 11.0 });
    }

    [Test]
    public async Task ConsumeDelta_UnknownContext_IsEmpty()
    {
        var buf = new AisTrailBuffer();
        await Assert.That(buf.ConsumeDelta("v.never-seen").IsEmpty).IsTrue();
    }

    [Test]
    public async Task ConsumeDelta_ResetsAfterRead()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(3));

        var first = buf.ConsumeDelta("v.A");
        await Assert.That(first.Append).IsEquivalentTo(new[] { 54.0, 11.0, 54.1, 11.1 });
        await Assert.That(buf.ConsumeDelta("v.A").IsEmpty).IsTrue();
    }

    [Test]
    public async Task MarkSynced_StartsDeltaFromFullSync()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(3));
        _ = buf.GetCoords("v.A");
        buf.MarkSynced("v.A");

        buf.Push("v.A", 54.2, 11.2, T0.AddSeconds(6));

        var delta = buf.ConsumeDelta("v.A");
        await Assert.That(delta.Append).IsEquivalentTo(new[] { 54.2, 11.2 });
        await Assert.That(delta.Trim).IsEqualTo(0);
    }

    [Test]
    public async Task ConsumeDelta_PointsExpiredBeforeEmission_AreNotAppended()
    {
        // Renderer holds [p1, p2]. p3 arrives, then a long gap expires
        // p1..p3 and p4 arrives. Trim covers p1, p2 and the never-sent
        // p3; the renderer clamps the trim to what it holds and appends
        // only p4.
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(3));
        _ = buf.ConsumeDelta("v.A");
        buf.Push("v.A", 54.2, 11.2, T0.AddSeconds(6));
        buf.Push("v.A", 54.3, 11.3, T0.AddMinutes(10));

        var delta = buf.ConsumeDelta("v.A");
        await Assert.That(delta.Trim).IsEqualTo(3);
        await Assert.That(delta.Append).IsEquivalentTo(new[] { 54.3, 11.3 });
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
    }

    [Test]
    public async Task ConsumeDelta_ReplayedOnRendererCopy_MatchesBuffer()
    {
        // Property: applying every delta to a renderer-side list (drop
        // min(Trim, held) points from the head, then append) always
        // reproduces GetCoords. Seeded walk with gaps that force trims
        // and skipped ticks that batch several changes into one delta.
        var rng = new Random(0x5EED);
        var buf = new AisTrailBuffer(TimeSpan.FromSeconds(60));
        var renderer = new List<double>();
        var t = T0;
        for (int i = 0; i < 400; i++)
        {
            t = t.AddSeconds(rng.Next(0, 4) == 0 ? rng.Next(20, 90) : 3);
            double lat = 54 + rng.Next(0, 5) * 0.001;
            buf.Push("v.A", lat, 11.0, t);
            if (rng.Next(0, 3) == 0) continue;
            var d = buf.ConsumeDelta("v.A");
            int drop = Math.Min(d.Trim, renderer.Count / 2);
            renderer.RemoveRange(0, drop * 2);
            if (d.Append is not null) renderer.AddRange(d.Append);
            await Assert.That(renderer).IsEquivalentTo(buf.GetCoords("v.A") ?? []);
        }
    }

    [Test]
    public async Task Forget_DropsTrailAndDeltaState()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(10));

        buf.Forget("v.A");

        await Assert.That(buf.CountFor("v.A")).IsEqualTo(0);
        await Assert.That(buf.GetCoords("v.A")).IsNull();
        await Assert.That(buf.ConsumeDelta("v.A").IsEmpty).IsTrue();
    }

    [Test]
    public async Task EndSweep_DropsContextsNotPushedSinceLastSweep()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.B", 55.0, 12.0, T0);
        buf.Push("v.C", 56.0, 13.0, T0);
        buf.EndSweep();

        buf.Push("v.A", 54.0, 11.0, T0.AddSeconds(3));
        buf.Push("v.C", 56.0, 13.0, T0.AddSeconds(3));
        buf.EndSweep();

        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
        await Assert.That(buf.CountFor("v.B")).IsEqualTo(0);
        await Assert.That(buf.CountFor("v.C")).IsEqualTo(1);
    }

    [Test]
    public async Task EndSweep_KeepsContextsRePushedAtSamePosition()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.EndSweep();

        bool changed = buf.Push("v.A", 54.0, 11.0, T0.AddSeconds(3));
        buf.EndSweep();

        await Assert.That(changed).IsFalse();
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
    }

    [Test]
    public async Task EndSweep_AllRefreshed_KeepsEverything()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.B", 55.0, 12.0, T0);
        buf.EndSweep();

        buf.Push("v.A", 54.0, 11.0, T0.AddSeconds(3));
        buf.Push("v.B", 55.0, 12.0, T0.AddSeconds(3));
        buf.EndSweep();

        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
        await Assert.That(buf.CountFor("v.B")).IsEqualTo(1);
    }

    [Test]
    public async Task Clear_ResetsEverything()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.B", 55.0, 12.0, T0);

        buf.Clear();

        await Assert.That(buf.CountFor("v.A")).IsEqualTo(0);
        await Assert.That(buf.CountFor("v.B")).IsEqualTo(0);
        await Assert.That(buf.ConsumeDelta("v.A").IsEmpty).IsTrue();
    }

    [Test]
    public async Task Push_ReacquiredContextAfterForget_StartsFreshTrail()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(10));
        buf.Forget("v.A");

        bool changed = buf.Push("v.A", 60.0, 20.0, T0.AddMinutes(10));

        await Assert.That(changed).IsTrue();
        await Assert.That(buf.GetCoords("v.A")).IsEquivalentTo(new[] { 60.0, 20.0 });
    }
}

using OnaPlotter.Services.Map;

namespace OnaPlotter.Tests.Services.Map;

/// <summary>
/// Pins the semantics of <see cref="AisTrailBuffer"/> - the C# home
/// of the AIS trail sliding-window that used to live JS-side in
/// <c>aisLayer.aisTrailHistory</c>. The original JS behaviour the
/// port must preserve: position-dedup on push, age-based trim,
/// degenerate-trail suppression (&lt; 2 points), and the "only emit
/// fresh coords when the trail changed" gate that drives the
/// per-vessel wire payload.
/// </summary>
public sealed class AisTrailBufferTests
{
    private static readonly DateTime T0 = new(2026, 5, 12, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Push_FirstFix_AddsPoint_AndMarksDirty()
    {
        var buf = new AisTrailBuffer();
        bool changed = buf.Push("v.A", 54.0, 11.0, T0);

        await Assert.That(changed).IsTrue();
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
        await Assert.That(buf.ConsumeDirty("v.A")).IsTrue();
    }

    [Test]
    public async Task Push_SamePosition_IsDedupedAndStillReportsNoChange()
    {
        // A moored vessel pinging the same coords every 3s shouldn't
        // grow its trail. Matches the JS dedup at
        // `if (!last || last.lat !== lat || last.lon !== lon) hist.push`.
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        _ = buf.ConsumeDirty("v.A");                     // drain initial dirty

        bool changed = buf.Push("v.A", 54.0, 11.0, T0.AddSeconds(3));

        await Assert.That(changed).IsFalse();
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
        await Assert.That(buf.ConsumeDirty("v.A")).IsFalse();
    }

    [Test]
    public async Task Push_DifferentPosition_AppendsAndMarksDirty()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        _ = buf.ConsumeDirty("v.A");

        bool changed = buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(10));

        await Assert.That(changed).IsTrue();
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(2);
        await Assert.That(buf.ConsumeDirty("v.A")).IsTrue();
    }

    [Test]
    public async Task Push_AgeTrim_DropsExpiredFronts()
    {
        // Default window is 5 minutes. Two points clearly older than
        // 5 minutes ago plus the current one should leave just the
        // current point after trim.
        //   T0+0:00     -> dropped (6m30s old)
        //   T0+0:30     -> dropped (6m old)
        //   T0+6:30     -> kept (now)
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(30));
        _ = buf.ConsumeDirty("v.A");

        bool changed = buf.Push(
            "v.A", 54.2, 11.2, T0.AddMinutes(6).AddSeconds(30));

        await Assert.That(changed).IsTrue();
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
        // GetCoords returns null because <2 points after the trim.
        await Assert.That(buf.GetCoords("v.A")).IsNull();
    }

    [Test]
    public async Task Push_AgeTrim_KeepsPointsAtTheBoundary()
    {
        // A point exactly at the cutoff age stays - the trim
        // condition is strict less-than (TicksUtc < cutoff). Pins
        // the boundary so a future tweak of the trim arithmetic
        // can't silently start dropping points that were JUST
        // inside the window.
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);

        // T0+5min: the first point is now exactly 5 minutes old.
        // It must NOT be dropped at the boundary.
        buf.Push("v.A", 54.1, 11.1, T0.AddMinutes(5));

        await Assert.That(buf.CountFor("v.A")).IsEqualTo(2);
    }

    [Test]
    public async Task GetCoords_LessThanTwoPoints_ReturnsNull()
    {
        // Mirrors the JS guard `if (hist.length < 2) return;` - a
        // single-coord polyline is degenerate, the renderer skipped it.
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        await Assert.That(buf.GetCoords("v.A")).IsNull();
    }

    [Test]
    public async Task GetCoords_ReturnsLatLonPairsInInsertionOrder()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(10));
        buf.Push("v.A", 54.2, 11.2, T0.AddSeconds(20));

        var coords = buf.GetCoords("v.A");

        await Assert.That(coords).IsNotNull();
        await Assert.That(coords!.Length).IsEqualTo(3);
        await Assert.That(coords[0][0]).IsEqualTo(54.0);
        await Assert.That(coords[2][1]).IsEqualTo(11.2);
    }

    [Test]
    public async Task ConsumeDirty_OnlyTrueOnceUntilNextChange()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);

        await Assert.That(buf.ConsumeDirty("v.A")).IsTrue();
        await Assert.That(buf.ConsumeDirty("v.A")).IsFalse();
        await Assert.That(buf.ConsumeDirty("v.A")).IsFalse();

        // After another change-causing push, dirty flips on again.
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(10));
        await Assert.That(buf.ConsumeDirty("v.A")).IsTrue();
        await Assert.That(buf.ConsumeDirty("v.A")).IsFalse();
    }

    [Test]
    public async Task ConsumeDirty_UnknownContext_IsFalse()
    {
        var buf = new AisTrailBuffer();
        await Assert.That(buf.ConsumeDirty("v.never-seen")).IsFalse();
    }

    [Test]
    public async Task Forget_DropsTrailAndDirtyState()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(10));

        buf.Forget("v.A");

        await Assert.That(buf.CountFor("v.A")).IsEqualTo(0);
        await Assert.That(buf.GetCoords("v.A")).IsNull();
        await Assert.That(buf.ConsumeDirty("v.A")).IsFalse();
    }

    [Test]
    public async Task RetainOnly_DropsAbsentContexts_KeepsLiveOnes()
    {
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.B", 55.0, 12.0, T0);
        buf.Push("v.C", 56.0, 13.0, T0);

        buf.RetainOnly(new[] { "v.A", "v.C" });

        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
        await Assert.That(buf.CountFor("v.B")).IsEqualTo(0);  // gone
        await Assert.That(buf.CountFor("v.C")).IsEqualTo(1);
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
        await Assert.That(buf.ConsumeDirty("v.A")).IsFalse();
    }

    [Test]
    public async Task Push_ReacquiredContextAfterForget_StartsFreshTrail()
    {
        // Common case: a vessel ages out of the AisStore, gets forgotten
        // by RetainOnly, then comes back. The new trail must NOT carry
        // points from the pre-forget life.
        var buf = new AisTrailBuffer();
        buf.Push("v.A", 54.0, 11.0, T0);
        buf.Push("v.A", 54.1, 11.1, T0.AddSeconds(10));
        buf.Forget("v.A");

        bool changed = buf.Push("v.A", 60.0, 20.0, T0.AddMinutes(10));

        await Assert.That(changed).IsTrue();
        await Assert.That(buf.CountFor("v.A")).IsEqualTo(1);
        await Assert.That(buf.GetCoords("v.A")).IsNull();
    }
}

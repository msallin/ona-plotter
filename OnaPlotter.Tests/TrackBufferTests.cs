using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class TrackBufferTests
{
    private static TrackPoint MakePoint(double lat, double lon, double? sog = null) =>
        new(DateTime.UtcNow, lat, lon, sog, null, null, null, null, null, null);

    [Test]
    public async Task Empty_CountIsZero()
    {
        var buf = new TrackBuffer();
        await Assert.That(buf.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_SnapshotIsEmpty()
    {
        var buf = new TrackBuffer();
        await Assert.That(buf.GetSnapshot()).IsEmpty();
    }

    [Test]
    public async Task Add_IncrementsCount()
    {
        var buf = new TrackBuffer();
        buf.Add(MakePoint(47, 8));
        await Assert.That(buf.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Add_MultiplePoints_AllRetrievable()
    {
        var buf = new TrackBuffer(100);
        buf.Add(MakePoint(1, 10));
        buf.Add(MakePoint(2, 20));
        buf.Add(MakePoint(3, 30));

        var snap = buf.GetSnapshot();
        await Assert.That(snap.Length).IsEqualTo(3);
        await Assert.That(snap[0].Latitude).IsEqualTo(1);
        await Assert.That(snap[1].Latitude).IsEqualTo(2);
        await Assert.That(snap[2].Latitude).IsEqualTo(3);
    }

    [Test]
    public async Task RingBuffer_WrapsAround_OldestFirst()
    {
        var buf = new TrackBuffer(capacity: 3);
        buf.Add(MakePoint(1, 0));
        buf.Add(MakePoint(2, 0));
        buf.Add(MakePoint(3, 0));
        buf.Add(MakePoint(4, 0)); // overwrites point 1

        var snap = buf.GetSnapshot();
        await Assert.That(snap.Length).IsEqualTo(3);
        await Assert.That(snap[0].Latitude).IsEqualTo(2); // oldest surviving
        await Assert.That(snap[1].Latitude).IsEqualTo(3);
        await Assert.That(snap[2].Latitude).IsEqualTo(4); // newest
    }

    [Test]
    public async Task RingBuffer_CountCapsAtCapacity()
    {
        var buf = new TrackBuffer(capacity: 5);
        for (int i = 0; i < 20; i++)
            buf.Add(MakePoint(i, 0));

        await Assert.That(buf.Count).IsEqualTo(5);
    }

    [Test]
    public async Task RingBuffer_DoubleWrap_CorrectOrder()
    {
        var buf = new TrackBuffer(capacity: 3);
        for (int i = 1; i <= 7; i++)
            buf.Add(MakePoint(i, 0));

        var snap = buf.GetSnapshot();
        await Assert.That(snap.Length).IsEqualTo(3);
        await Assert.That(snap[0].Latitude).IsEqualTo(5);
        await Assert.That(snap[1].Latitude).IsEqualTo(6);
        await Assert.That(snap[2].Latitude).IsEqualTo(7);
    }

    [Test]
    public async Task Add_RaisesOnTrackUpdated()
    {
        var buf = new TrackBuffer();
        int raised = 0;
        buf.OnTrackUpdated += () => raised++;

        buf.Add(MakePoint(1, 1));
        buf.Add(MakePoint(2, 2));

        await Assert.That(raised).IsEqualTo(2);
    }

    [Test]
    public async Task Snapshot_IsIndependentCopy()
    {
        var buf = new TrackBuffer(10);
        buf.Add(MakePoint(1, 0));

        var snap1 = buf.GetSnapshot();
        buf.Add(MakePoint(2, 0));
        var snap2 = buf.GetSnapshot();

        await Assert.That(snap1.Length).IsEqualTo(1);
        await Assert.That(snap2.Length).IsEqualTo(2);
    }

    // --- Depth trend ----------------------------------------------------

    private static TrackPoint DepthSample(DateTime t, double depth) =>
        new(t, 0, 0, null, null, null, null, null, null, null, depth);

    [Test]
    public async Task DepthTrend_NullWithoutEnoughSamples()
    {
        var buf = new TrackBuffer(10);
        await Assert.That(buf.GetDepthTrendMetersPerMinute(TimeSpan.FromMinutes(1))).IsNull();

        buf.Add(DepthSample(DateTime.UtcNow, 5.0));
        await Assert.That(buf.GetDepthTrendMetersPerMinute(TimeSpan.FromMinutes(1))).IsNull();
    }

    [Test]
    public async Task DepthTrend_Ignores_Samples_Without_Depth()
    {
        // Track points flow in whenever ANY nav value changes, not just
        // depth. The trend calc must ignore null-depth points so the
        // result reflects actual depth change, not sample frequency.
        // Use a 5-min window so clock-slip between the test's UtcNow
        // snapshot and the implementation's doesn't push the oldest
        // sample just outside the window.
        var now = DateTime.UtcNow;
        var buf = new TrackBuffer(10);
        buf.Add(DepthSample(now.AddMinutes(-1), 5.0));
        buf.Add(new TrackPoint(now.AddSeconds(-30), 0, 0, null, null, null, null, null, null, null)); // no depth
        buf.Add(DepthSample(now, 4.0));

        var mpm = buf.GetDepthTrendMetersPerMinute(TimeSpan.FromMinutes(5));
        await Assert.That(mpm).IsNotNull();
        await Assert.That(mpm!.Value).IsEqualTo(-1.0).Within(0.05);
    }

    [Test]
    public async Task DepthTrend_Positive_When_Getting_Deeper()
    {
        var now = DateTime.UtcNow;
        var buf = new TrackBuffer(10);
        buf.Add(DepthSample(now.AddMinutes(-1), 4.0));
        buf.Add(DepthSample(now, 6.0));

        var mpm = buf.GetDepthTrendMetersPerMinute(TimeSpan.FromMinutes(5));
        await Assert.That(mpm).IsNotNull();
        await Assert.That(mpm!.Value).IsEqualTo(2.0).Within(0.1);
    }

    [Test]
    public async Task DepthTrend_Negative_When_Getting_Shallower()
    {
        var now = DateTime.UtcNow;
        var buf = new TrackBuffer(10);
        buf.Add(DepthSample(now.AddMinutes(-1), 8.0));
        buf.Add(DepthSample(now, 5.0));

        var mpm = buf.GetDepthTrendMetersPerMinute(TimeSpan.FromMinutes(5));
        await Assert.That(mpm).IsNotNull();
        await Assert.That(mpm!.Value).IsEqualTo(-3.0).Within(0.2);
    }

    [Test]
    public async Task DepthTrend_ZeroSpread_ReturnsNull()
    {
        // Two samples at the same instant shouldn't divide-by-zero.
        var now = DateTime.UtcNow;
        var buf = new TrackBuffer(10);
        buf.Add(DepthSample(now, 5.0));
        buf.Add(DepthSample(now, 4.0));

        await Assert.That(buf.GetDepthTrendMetersPerMinute(TimeSpan.FromMinutes(1))).IsNull();
    }

    [Test]
    public async Task DepthTrend_Skips_Points_Older_Than_Window()
    {
        // An old sample from before the lookback window shouldn't anchor
        // the trend; the next-oldest IN the window should.
        var now = DateTime.UtcNow;
        var buf = new TrackBuffer(10);
        buf.Add(DepthSample(now.AddMinutes(-10), 2.0));        // outside window
        buf.Add(DepthSample(now.AddSeconds(-30), 5.0));        // inside window (older)
        buf.Add(DepthSample(now, 5.5));                         // inside window (newest)

        var mpm = buf.GetDepthTrendMetersPerMinute(TimeSpan.FromMinutes(1));
        // (5.5 - 5.0) / 0.5 min = 1.0 m/min, NOT based on the -10 min sample.
        await Assert.That(mpm).IsNotNull();
        await Assert.That(mpm!.Value).IsEqualTo(1.0).Within(0.1);
    }
}

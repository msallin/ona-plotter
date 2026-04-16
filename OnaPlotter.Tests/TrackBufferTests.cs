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
}

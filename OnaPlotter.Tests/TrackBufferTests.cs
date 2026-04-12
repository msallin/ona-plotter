using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

public class TrackBufferTests
{
    private static TrackPoint MakePoint(double lat, double lon, double? sog = null) =>
        new(DateTime.UtcNow, lat, lon, sog, null, null, null, null);

    [Fact]
    public void Empty_CountIsZero()
    {
        var buf = new TrackBuffer();
        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void Empty_SnapshotIsEmpty()
    {
        var buf = new TrackBuffer();
        Assert.Empty(buf.GetSnapshot());
    }

    [Fact]
    public void Add_IncrementsCount()
    {
        var buf = new TrackBuffer();
        buf.Add(MakePoint(47, 8));
        Assert.Equal(1, buf.Count);
    }

    [Fact]
    public void Add_MultiplePoints_AllRetrievable()
    {
        var buf = new TrackBuffer(100);
        buf.Add(MakePoint(1, 10));
        buf.Add(MakePoint(2, 20));
        buf.Add(MakePoint(3, 30));

        var snap = buf.GetSnapshot();
        Assert.Equal(3, snap.Length);
        Assert.Equal(1, snap[0].Latitude);
        Assert.Equal(2, snap[1].Latitude);
        Assert.Equal(3, snap[2].Latitude);
    }

    [Fact]
    public void RingBuffer_WrapsAround_OldestFirst()
    {
        var buf = new TrackBuffer(capacity: 3);
        buf.Add(MakePoint(1, 0));
        buf.Add(MakePoint(2, 0));
        buf.Add(MakePoint(3, 0));
        buf.Add(MakePoint(4, 0)); // overwrites point 1

        var snap = buf.GetSnapshot();
        Assert.Equal(3, snap.Length);
        Assert.Equal(2, snap[0].Latitude); // oldest surviving
        Assert.Equal(3, snap[1].Latitude);
        Assert.Equal(4, snap[2].Latitude); // newest
    }

    [Fact]
    public void RingBuffer_CountCapsAtCapacity()
    {
        var buf = new TrackBuffer(capacity: 5);
        for (int i = 0; i < 20; i++)
            buf.Add(MakePoint(i, 0));

        Assert.Equal(5, buf.Count);
    }

    [Fact]
    public void RingBuffer_DoubleWrap_CorrectOrder()
    {
        var buf = new TrackBuffer(capacity: 3);
        for (int i = 1; i <= 7; i++)
            buf.Add(MakePoint(i, 0));

        var snap = buf.GetSnapshot();
        Assert.Equal(3, snap.Length);
        Assert.Equal(5, snap[0].Latitude);
        Assert.Equal(6, snap[1].Latitude);
        Assert.Equal(7, snap[2].Latitude);
    }

    [Fact]
    public void Add_RaisesOnTrackUpdated()
    {
        var buf = new TrackBuffer();
        int raised = 0;
        buf.OnTrackUpdated += () => raised++;

        buf.Add(MakePoint(1, 1));
        buf.Add(MakePoint(2, 2));

        Assert.Equal(2, raised);
    }

    [Fact]
    public void Snapshot_IsIndependentCopy()
    {
        var buf = new TrackBuffer(10);
        buf.Add(MakePoint(1, 0));

        var snap1 = buf.GetSnapshot();
        buf.Add(MakePoint(2, 0));
        var snap2 = buf.GetSnapshot();

        Assert.Single(snap1);
        Assert.Equal(2, snap2.Length);
    }
}

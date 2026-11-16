using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class RenderThrottleTests
{
    /// <summary>Fake clock + manually released trailing delay.</summary>
    private sealed class Harness
    {
        public long Now = 1_000;
        public int Fires;
        public readonly List<int> Delays = [];
        private TaskCompletionSource? _gate;
        public RenderThrottle Throttle { get; }

        public Harness(int intervalMs = 300)
        {
            Throttle = new RenderThrottle(TimeSpan.FromMilliseconds(intervalMs), () => Fires++,
                () => Now,
                ms => { Delays.Add(ms); DueAt = Now + ms; _gate = new TaskCompletionSource(); return _gate.Task; });
        }

        public long? DueAt;

        public void ReleaseTrailing() { DueAt = null; _gate!.SetResult(); }
    }

    [Test]
    public async Task FirstRequest_FiresImmediately()
    {
        var h = new Harness();

        h.Throttle.Request();

        await Assert.That(h.Fires).IsEqualTo(1);
        await Assert.That(h.Delays).IsEmpty();
    }

    [Test]
    public async Task FirstRequest_FiresEvenWhenClockIsNearZero()
    {
        // TickCount64 starts near zero on a freshly booted kiosk.
        var h = new Harness { Now = 0 };

        h.Throttle.Request();

        await Assert.That(h.Fires).IsEqualTo(1);
    }

    [Test]
    public async Task BurstInsideInterval_CoalescesIntoOneTrailingFire()
    {
        var h = new Harness();
        h.Throttle.Request();
        h.Now += 40;
        h.Throttle.Request();
        h.Now += 40;
        h.Throttle.Request();
        h.Throttle.Request();

        await Assert.That(h.Fires).IsEqualTo(1);
        await Assert.That(h.Delays).IsEquivalentTo(new[] { 260 });

        h.ReleaseTrailing();

        await Assert.That(h.Fires).IsEqualTo(2);
    }

    [Test]
    public async Task RequestAfterInterval_FiresImmediately()
    {
        var h = new Harness();
        h.Throttle.Request();
        h.Now += 300;

        h.Throttle.Request();

        await Assert.That(h.Fires).IsEqualTo(2);
        await Assert.That(h.Delays).IsEmpty();
    }

    [Test]
    public async Task TrailingFire_RestartsInterval()
    {
        var h = new Harness();
        h.Throttle.Request();
        h.Now += 100;
        h.Throttle.Request();
        h.Now += 200;
        h.ReleaseTrailing();           // trailing fire at t=300

        h.Now += 50;
        h.Throttle.Request();          // 50 ms after the trailing fire

        await Assert.That(h.Fires).IsEqualTo(2);
        await Assert.That(h.Delays).IsEquivalentTo(new[] { 200, 250 });
    }

    [Test]
    public async Task Dispose_DropsParkedTrailingFireAndLaterRequests()
    {
        var h = new Harness();
        h.Throttle.Request();
        h.Now += 10;
        h.Throttle.Request();

        h.Throttle.Dispose();
        h.ReleaseTrailing();
        h.Now += 1_000;
        h.Throttle.Request();

        await Assert.That(h.Fires).IsEqualTo(1);
    }

    [Test]
    public async Task SelfDeltaStorm_CapsFireRate()
    {
        // 30 deltas/s for 3 s against a 300 ms throttle: about 3-4 renders
        // per second instead of 30.
        var h = new Harness();
        for (int i = 0; i < 90; i++)
        {
            if (h.DueAt is long due && h.Now >= due) h.ReleaseTrailing();
            h.Throttle.Request();
            h.Now += 33;
        }

        await Assert.That(h.Fires).IsLessThanOrEqualTo(12);
        await Assert.That(h.Fires).IsGreaterThanOrEqualTo(9);
    }

    [Test]
    public async Task NonPositiveInterval_Throws()
    {
        await Assert.That(() => new RenderThrottle(TimeSpan.Zero, () => { }))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => new RenderThrottle(TimeSpan.FromMilliseconds(-1), () => { }))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task RealDelay_DeliversTrailingFire()
    {
        int fires = 0;
        var done = new TaskCompletionSource();
        using var t = new RenderThrottle(TimeSpan.FromMilliseconds(20), () =>
        {
            if (Interlocked.Increment(ref fires) == 2) done.TrySetResult();
        });

        t.Request();
        t.Request();

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(fires).IsEqualTo(2);
    }
}

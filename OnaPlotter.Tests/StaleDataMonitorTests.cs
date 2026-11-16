using Microsoft.Extensions.Time.Testing;
using OnaPlotter.Services.Signalk;

namespace OnaPlotter.Tests;

public class StaleDataMonitorTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(StaleDataMonitor.CheckIntervalSeconds);

    private static (StaleDataMonitor Sut, FakeSignalkClient Client, FakeTimeProvider Time, List<int> Raised) Create()
    {
        var client = new FakeSignalkClient();
        var time = new FakeTimeProvider();
        var sut = new StaleDataMonitor(client, time);
        var raised = new List<int>();
        sut.OnChanged += () => raised.Add(raised.Count + 1);
        return (sut, client, time, raised);
    }

    [Test]
    public async Task Does_Nothing_Before_StartAsync()
    {
        var (_, client, time, raised) = Create();

        client.IsDataStale = true;
        time.Advance(Tick * 5);
        client.RaiseDataChanged();

        await Assert.That(raised.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Raises_When_Stale_State_Flips_At_The_Check_Interval()
    {
        var (sut, client, time, raised) = Create();
        await sut.StartAsync(CancellationToken.None);

        client.IsDataStale = true;
        time.Advance(Tick - TimeSpan.FromMilliseconds(1));
        await Assert.That(raised.Count).IsEqualTo(0);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(raised.Count).IsEqualTo(1);

        client.IsDataStale = false;
        time.Advance(Tick);
        await Assert.That(raised.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Does_Not_Raise_Repeatedly_While_State_Is_Unchanged()
    {
        var (sut, client, time, raised) = Create();
        await sut.StartAsync(CancellationToken.None);

        client.IsDataStale = true;
        time.Advance(Tick * 5);

        await Assert.That(raised.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Data_Arrival_While_Stale_Raises_Immediately_Without_Waiting_For_The_Tick()
    {
        var (sut, client, time, raised) = Create();
        await sut.StartAsync(CancellationToken.None);
        client.IsDataStale = true;
        time.Advance(Tick);
        await Assert.That(raised.Count).IsEqualTo(1);

        client.IsDataStale = false;
        client.RaiseDataChanged();

        await Assert.That(raised.Count).IsEqualTo(2);
        // The next tick sees not-stale on both sides: no duplicate.
        time.Advance(Tick);
        await Assert.That(raised.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Data_Arrival_While_Not_Stale_Raises_Nothing()
    {
        var (sut, client, _, raised) = Create();
        await sut.StartAsync(CancellationToken.None);

        client.RaiseDataChanged();

        await Assert.That(raised.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StartAsync_Twice_Runs_A_Single_Timer()
    {
        var (sut, client, time, raised) = Create();
        await sut.StartAsync(CancellationToken.None);
        await sut.StartAsync(CancellationToken.None);

        client.IsDataStale = true;
        time.Advance(Tick);
        client.IsDataStale = false;
        client.RaiseDataChanged();

        // One raise from the tick, one from the data frame; a duplicated
        // subscription would double the data-frame raise.
        await Assert.That(raised.Count).IsEqualTo(2);
    }

    [Test]
    public async Task StartAsync_After_StopAsync_Is_A_No_Op()
    {
        var (sut, client, time, raised) = Create();
        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        await sut.StartAsync(CancellationToken.None);
        client.IsDataStale = true;
        time.Advance(Tick * 3);

        await Assert.That(raised.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StopAsync_Stops_The_Timer_And_Data_Subscription()
    {
        var (sut, client, time, raised) = Create();
        await sut.StartAsync(CancellationToken.None);
        client.IsDataStale = true;
        time.Advance(Tick);
        await Assert.That(raised.Count).IsEqualTo(1);

        await sut.StopAsync(CancellationToken.None);
        client.IsDataStale = false;
        client.RaiseDataChanged();
        time.Advance(Tick * 5);

        await Assert.That(raised.Count).IsEqualTo(1);
    }
}

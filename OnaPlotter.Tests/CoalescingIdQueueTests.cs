using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

public class CoalescingIdQueueTests
{
    [Test]
    public async Task SameTickEvents_FoldIntoOneBatch()
    {
        var batches = new List<string[]>();
        var q = new CoalescingIdQueue(b => { batches.Add(b); return Task.CompletedTask; });

        q.Enqueue("a");
        q.Enqueue("b");
        q.Enqueue("c");
        await q.WhenIdleAsync();

        await Assert.That(batches.Count).IsEqualTo(1);
        await Assert.That(batches[0].OrderBy(x => x).ToArray()).IsEquivalentTo(new[] { "a", "b", "c" });
    }

    [Test]
    public async Task DuplicateIds_WithinADrain_AreCoalesced()
    {
        var batches = new List<string[]>();
        var q = new CoalescingIdQueue(b => { batches.Add(b); return Task.CompletedTask; });

        q.Enqueue("a");
        q.Enqueue("a");
        q.Enqueue("b");
        q.Enqueue("a");
        await q.WhenIdleAsync();

        await Assert.That(batches.Count).IsEqualTo(1);
        await Assert.That(batches[0].Length).IsEqualTo(2);
    }

    [Test]
    public async Task NothingRuns_BeforeTheYield()
    {
        int calls = 0;
        var q = new CoalescingIdQueue(_ => { calls++; return Task.CompletedTask; });

        q.Enqueue("a");

        await Assert.That(calls).IsEqualTo(0);
        await q.WhenIdleAsync();
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task IdArrivingDuringABatch_IsPickedUpByTheSameDrain()
    {
        var batches = new List<string[]>();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var q = new CoalescingIdQueue(async b =>
        {
            batches.Add(b);
            if (batches.Count == 1)
            {
                entered.SetResult();
                await gate.Task;
            }
        });

        q.Enqueue("a");
        await entered.Task;
        q.Enqueue("b");
        q.Enqueue("a");
        gate.SetResult();
        await q.WhenIdleAsync();

        await Assert.That(batches.Count).IsEqualTo(2);
        await Assert.That(batches[0]).IsEquivalentTo(new[] { "a" });
        await Assert.That(batches[1].OrderBy(x => x).ToArray()).IsEquivalentTo(new[] { "a", "b" });
    }

    [Test]
    public async Task OnDrained_RunsOncePerDrain_AfterAllBatches()
    {
        var log = new List<string>();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var q = new CoalescingIdQueue(async b =>
        {
            log.Add("batch:" + string.Join(",", b.OrderBy(x => x)));
            if (log.Count == 1)
            {
                entered.SetResult();
                await gate.Task;
            }
        }, () => log.Add("drained"));

        q.Enqueue("a");
        await entered.Task;
        q.Enqueue("b");
        gate.SetResult();
        await q.WhenIdleAsync();

        await Assert.That(log).IsEquivalentTo(new[] { "batch:a", "batch:b", "drained" });
    }

    [Test]
    public async Task EventsAfterADrain_StartANewDrain()
    {
        int drained = 0;
        var batches = new List<string[]>();
        var q = new CoalescingIdQueue(b => { batches.Add(b); return Task.CompletedTask; }, () => drained++);

        q.Enqueue("a");
        await q.WhenIdleAsync();
        q.Enqueue("a");
        await q.WhenIdleAsync();

        await Assert.That(batches.Count).IsEqualTo(2);
        await Assert.That(drained).IsEqualTo(2);
    }

    [Test]
    public async Task ExceptionInDrain_SkipsOnDrained_AndDoesNotWedgeTheQueue()
    {
        int calls = 0;
        int drained = 0;
        var q = new CoalescingIdQueue(_ =>
        {
            if (++calls == 1) throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }, () => drained++);

        q.Enqueue("a");
        await Assert.That(async () => await q.WhenIdleAsync()).Throws<InvalidOperationException>();
        await Assert.That(drained).IsEqualTo(0);

        q.Enqueue("b");
        await q.WhenIdleAsync();

        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(drained).IsEqualTo(1);
    }

    [Test]
    public async Task IdsPendingWhenDrainThrows_AreNotLost_ForTheNextDrain()
    {
        var seen = new List<string>();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var q = new CoalescingIdQueue(async b =>
        {
            seen.AddRange(b);
            if (seen.Count == 1)
            {
                entered.SetResult();
                await gate.Task;
                throw new InvalidOperationException("boom");
            }
        });

        q.Enqueue("a");
        await entered.Task;
        q.Enqueue("late");
        gate.SetResult();
        await Assert.That(async () => await q.WhenIdleAsync()).Throws<InvalidOperationException>();

        q.Enqueue("next");
        await q.WhenIdleAsync();

        await Assert.That(seen.Contains("late")).IsTrue();
        await Assert.That(seen.Contains("next")).IsTrue();
    }

    [Test]
    public async Task IdEnqueuedFromOnDrained_IsNotStranded()
    {
        var batches = new List<string[]>();
        int drained = 0;
        CoalescingIdQueue q = null!;
        q = new CoalescingIdQueue(b => { batches.Add(b); return Task.CompletedTask; }, () =>
        {
            if (++drained == 1) q.Enqueue("late");
        });

        q.Enqueue("a");
        await q.WhenIdleAsync();

        await Assert.That(batches.Count).IsEqualTo(2);
        await Assert.That(batches[1]).IsEquivalentTo(new[] { "late" });
        await Assert.That(drained).IsEqualTo(2);
    }
}

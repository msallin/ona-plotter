namespace OnaPlotter.Utilities;

/// <summary>
/// Coalesces id-keyed change events into batched, serialised drains. A
/// reconnect-edge reconcile fires one event per cached resource back to
/// back in a single dispatcher tick (90+ routes, 200+ waypoints); handling
/// each in its own fire-and-forget task raced them through the JS-interop
/// bridge and overflowed the WASM stack. The first <see cref="Enqueue"/>
/// schedules one drain; the drain yields once so same-tick events fold into
/// the pending set, then hands the distinct ids to the callback as one
/// batch. Ids that arrive while a batch is processing are picked up by the
/// next pass of the same drain, so only one batch is ever in flight.
/// <para>
/// Not thread-safe: Blazor WASM raises the events on one thread.
/// </para>
/// </summary>
public sealed class CoalescingIdQueue
{
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Func<string[], Task> _drain;
    private readonly Action? _onDrained;
    private Task _current = Task.CompletedTask;
    private bool _scheduled;

    /// <param name="drain">Processes one batch of distinct ids. It owns
    /// per-id error handling; an exception it lets escape ends the drain
    /// (skipping <paramref name="onDrained"/>) but not the queue.</param>
    /// <param name="onDrained">Runs once at the end of a drain that
    /// emptied the queue, however many batches it took.</param>
    public CoalescingIdQueue(Func<string[], Task> drain, Action? onDrained = null)
    {
        _drain = drain ?? throw new ArgumentNullException(nameof(drain));
        _onDrained = onDrained;
    }

    public void Enqueue(string id)
    {
        _pending.Add(id);
        if (_scheduled) return;
        _scheduled = true;
        _current = DrainAsync();
    }

    /// <summary>Completes when the scheduled drain, if any, has finished;
    /// faults with the exception that ended it.</summary>
    public Task WhenIdleAsync() => _current;

    private async Task DrainAsync()
    {
        try
        {
            await Task.Yield();
            // An id enqueued from inside onDrained is still pending after
            // it returns, so loop until a full pass leaves nothing behind.
            do
            {
                while (_pending.Count > 0)
                {
                    // Snapshot + clear before awaiting so an event arriving
                    // mid-batch queues onto the next pass instead of mutating
                    // the set under the iteration.
                    var batch = _pending.ToArray();
                    _pending.Clear();
                    await _drain(batch);
                }
                _onDrained?.Invoke();
            }
            while (_pending.Count > 0);
        }
        finally
        {
            _scheduled = false;
        }
    }
}

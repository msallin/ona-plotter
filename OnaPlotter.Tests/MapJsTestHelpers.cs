using Microsoft.JSInterop;

namespace OnaPlotter.Tests;

/// <summary>
/// Shared recording fake for the IMap*Js wrapper tests. Captures the
/// (identifier, args) tuple of every InvokeAsync call so tests can
/// assert the wrapper forwarded the right JS function name + arguments.
/// Optional flags fire one of the three relevant exceptions on the
/// next call so the wrapper's swallow / propagate behaviour can be
/// pinned independently.
///
/// <para>Same shape as the private fake in
/// <see cref="MapAnchorJsTests"/>; promoted to a shared helper now
/// that six wrapper test classes need it.</para>
/// </summary>
internal sealed class RecordingJsRef : IJSObjectReference
{
    public List<(string id, object?[] args)> Calls { get; } = new();
    public bool ThrowDisconnectedNext { get; set; }
    public bool ThrowDisposedNext { get; set; }
    public bool ThrowJsExceptionNext { get; set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        Calls.Add((identifier, args ?? Array.Empty<object?>()));
        if (ThrowDisconnectedNext)
        {
            ThrowDisconnectedNext = false;
            throw new JSDisconnectedException("test disconnect");
        }
        if (ThrowDisposedNext)
        {
            ThrowDisposedNext = false;
            throw new ObjectDisposedException("test");
        }
        if (ThrowJsExceptionNext)
        {
            ThrowJsExceptionNext = false;
            throw new JSException("test js error");
        }
        return new ValueTask<TValue>(default(TValue)!);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => InvokeAsync<TValue>(identifier, args);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

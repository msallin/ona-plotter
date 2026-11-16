using Microsoft.JSInterop;

namespace OnaPlotter.Tests;

/// <summary>
/// Shared recording fake for the IMap*Js wrapper tests. Captures the
/// (identifier, args) tuple of every InvokeAsync call so tests can
/// assert the wrapper forwarded the right JS function name + arguments.
/// Optional flags fire one of the three relevant exceptions on the
/// next call so the wrapper's swallow / propagate behaviour can be
/// pinned independently.
/// </summary>
internal sealed class RecordingJsRef : IJSObjectReference
{
    // The recording logic lives on a separate non-interop type: the
    // Blazor BL0016 analyzer treats every call on an IJSObjectReference
    // implementation as a JS interop call, including the fake's own
    // helper methods.
    private readonly JsCallRecorder _recorder = new();

    public List<(string id, object?[] args)> Calls => _recorder.Calls;
    public bool ThrowDisconnectedNext { get => _recorder.ThrowDisconnectedNext; set => _recorder.ThrowDisconnectedNext = value; }
    public bool ThrowDisposedNext { get => _recorder.ThrowDisposedNext; set => _recorder.ThrowDisposedNext = value; }
    public bool ThrowJsExceptionNext { get => _recorder.ThrowJsExceptionNext; set => _recorder.ThrowJsExceptionNext = value; }

    /// <summary>Optional per-identifier return value override. Used by
    /// the Get* wrapper tests to pin that the wrapper hands the JS-side
    /// payload through unchanged. Keyed on the JS function id so a
    /// single fake can serve multiple Invoke calls in a sequence.</summary>
    public Dictionary<string, object?> Returns => _recorder.Returns;

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => new(_recorder.Record<TValue>(identifier, args));

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => new(_recorder.Record<TValue>(identifier, args));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class JsCallRecorder
    {
        public List<(string id, object?[] args)> Calls { get; } = new();
        public bool ThrowDisconnectedNext { get; set; }
        public bool ThrowDisposedNext { get; set; }
        public bool ThrowJsExceptionNext { get; set; }
        public Dictionary<string, object?> Returns { get; } = new();

        public TValue Record<TValue>(string identifier, object?[]? args)
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
            if (Returns.TryGetValue(identifier, out var v))
                return (TValue)v!;
            return default!;
        }
    }
}

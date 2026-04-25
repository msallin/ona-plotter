namespace OnaPlotter.Services.Api;

/// <summary>
/// Outcome of a SignalK API mutation (Delete, Update, Set*). Wraps
/// a success flag with an optional server-provided error message so
/// every caller can render a specific failure reason rather than a
/// generic "operation failed" toast. One shape across every mutation
/// (deletes, course / autopilot state, radar control) so call-sites
/// don't have to switch between <c>if (!ok)</c>, <c>if (id == null)</c>,
/// and <c>if (!r.Success)</c>.
/// </summary>
/// <param name="Success">True iff the server returned a 2xx.</param>
/// <param name="Error">Server-provided error string when
/// available (e.g. "Control range value 12000 is not a legal
/// value" from the Radar API) or a short transport hint on
/// HttpRequestException. Null on success; may also be null on
/// failure if the response body had nothing parseable -- callers
/// still know the operation failed from <see cref="Success"/>.</param>
public sealed record ApiResult(bool Success, string? Error = null)
{
    /// <summary>Singleton success. Allocation-free happy path.</summary>
    public static ApiResult Ok { get; } = new(true);

    /// <summary>Builds a failure result. Null or empty error becomes
    /// null on the record (normalised so consumers can write
    /// <c>?? "default message"</c>).</summary>
    public static ApiResult Fail(string? error = null) =>
        new(false, string.IsNullOrWhiteSpace(error) ? null : error);
}

/// <summary>
/// Outcome of an API operation that returns a value on success
/// (typically a freshly-created resource id, occasionally a full
/// DTO). Same success / error conventions as <see cref="ApiResult"/>
/// plus a <see cref="Value"/> payload present on success.
/// </summary>
/// <typeparam name="T">Payload type. For resource creates this is
/// usually <c>string</c> (the new id); for reads that want error
/// detail it might be a DTO.</typeparam>
public sealed record ApiResult<T>(bool Success, T? Value = default, string? Error = null)
{
    public static ApiResult<T> Ok(T value) => new(true, value);

    public static ApiResult<T> Fail(string? error = null) =>
        new(false, default, string.IsNullOrWhiteSpace(error) ? null : error);
}

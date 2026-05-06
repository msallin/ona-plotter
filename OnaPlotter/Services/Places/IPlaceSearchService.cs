namespace OnaPlotter.Services.Places;

/// <summary>
/// Provider-agnostic place lookup. The topbar SearchBox debounces
/// keystrokes and calls into this surface; the implementation is
/// the wired Photon client (default), with caching and own-data
/// merging layered on as decorators.
///
/// <para>Failure semantics: implementations return an empty list
/// on transient error (offline, rate-limited, malformed response)
/// rather than throwing. The caller surfaces "no results" in the
/// dropdown either way; an exception would force every call site
/// to decide between "abort the search" and "fall through to the
/// next provider", and the consensus across this codebase is that
/// search-time failures are recoverable per-tick events rather
/// than user-facing errors.</para>
/// </summary>
public interface IPlaceSearchService
{
    /// <summary>
    /// Look up places matching <paramref name="query"/>. Caller
    /// pre-trims; an empty / whitespace-only query returns an
    /// empty list. <paramref name="ct"/> aborts an in-flight HTTP
    /// call when the helm types another character (the debounce
    /// keeps the previous query's CTS alive long enough that the
    /// new keystroke can cancel the old request).
    /// </summary>
    Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default);
}

namespace OnaPlotter.Utilities;

/// <summary>
/// Picks a route name that doesn't collide with any existing route on
/// the SignalK server. Used by the "Add Route" flow to keep the
/// auto-suggested date-stamped default ("Route 20260427") unique when
/// the helm has already created routes that day -- without a suffix
/// the second one silently gets the same display name as the first
/// and the routes panel shows two identical rows.
/// </summary>
/// <remarks>
/// Pure function (no dependencies on the C# UI / SignalK client) so it
/// drops into the unit-test harness without bUnit. The disambiguation
/// strategy: if <paramref name="baseName"/> is already taken, append
/// " (2)", " (3)", etc. until a free slot is found. Mirrors the macOS
/// Finder / Windows Explorer convention helms recognise from desktop
/// file managers.
/// </remarks>
public static class UniqueRouteName
{
    /// <summary>
    /// Returns <paramref name="baseName"/> if no entry of
    /// <paramref name="existingNames"/> matches; otherwise returns
    /// <c>baseName (2)</c>, <c>baseName (3)</c>, ... whichever is the
    /// first that isn't already taken. Comparison is case-sensitive and
    /// ordinal -- SignalK route names are user-facing strings and
    /// "Crossing" vs "crossing" are legitimately different routes.
    /// </summary>
    public static string Suggest(string baseName, IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentNullException.ThrowIfNull(existingNames);

        // HashSet for O(1) collision checks. Filter null / empty out
        // since SignalK will occasionally return a route resource
        // with no name (deleted-mid-list, plugin bug); those can't
        // collide with anything anyway.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in existingNames)
        {
            if (!string.IsNullOrEmpty(name)) taken.Add(name);
        }

        if (!taken.Contains(baseName)) return baseName;

        // Bounded loop: 9999 candidates is far more than any helm will
        // ever hit, but the upper limit stops a runaway in pathological
        // tests / corrupted data. Falls back to the base name unchanged
        // -- the server will then surface the duplicate visually, which
        // is no worse than today.
        for (int i = 2; i < 10000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
        return baseName;
    }
}

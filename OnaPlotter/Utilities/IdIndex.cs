namespace OnaPlotter.Utilities;

/// <summary>
/// id -> position lookups for the page-local resource lists. A drained
/// batch of 200 ids builds the index once (<see cref="Build"/>) and each
/// apply does an O(1) <see cref="IndexOf"/> instead of an O(M) scan.
/// </summary>
public static class IdIndex
{
    public static Dictionary<string, int> Build<T>(IReadOnlyList<T> list, Func<T, string> getId)
    {
        var index = new Dictionary<string, int>(list.Count, StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++)
            index[getId(list[i])] = i;
        return index;
    }

    /// <summary>Position of <paramref name="id"/>, or -1. The hint is
    /// validated against the list: an earlier await may have shifted
    /// indices, in which case it falls back to a scan.</summary>
    public static int IndexOf<T>(IReadOnlyList<T> list, string id, Func<T, string> getId,
        IReadOnlyDictionary<string, int> hint)
    {
        if (hint.TryGetValue(id, out int hinted)
            && hinted >= 0 && hinted < list.Count
            && getId(list[hinted]) == id)
            return hinted;
        for (int i = 0; i < list.Count; i++)
            if (getId(list[i]) == id) return i;
        return -1;
    }

    /// <summary>Replaces the entry with <paramref name="id"/> or appends
    /// <paramref name="item"/>, keeping the hint current for appends.
    /// Returns true when the item was appended.</summary>
    public static bool Upsert<T>(List<T> list, string id, T item, Func<T, string> getId,
        Dictionary<string, int> hint)
    {
        int idx = IndexOf(list, id, getId, hint);
        if (idx >= 0)
        {
            list[idx] = item;
            return false;
        }
        list.Add(item);
        hint[id] = list.Count - 1;
        return true;
    }
}

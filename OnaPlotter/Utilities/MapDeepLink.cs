namespace OnaPlotter.Utilities;

/// <summary>
/// Parses the deep-link query string the Resources page (and external
/// callers) attach to <c>/map?focus=...</c> or <c>/map?edit=...</c>.
/// Pure helper so the parser is testable without the Razor page; the
/// page handles dispatch (resolving the kind+id to a route / waypoint
/// / note / region and panning or opening the editor).
///
/// <para>Value format: <c>&lt;kind&gt;:&lt;id&gt;</c>. Kinds today
/// are <c>route</c>, <c>waypoint</c>, <c>note</c>, <c>region</c>.</para>
///
/// <para>Examples:</para>
/// <list type="bullet">
/// <item><description><c>?focus=route:abc123</c> -> <c>(route, abc123, false)</c></description></item>
/// <item><description><c>?edit=region:xyz</c> -> <c>(region, xyz, true)</c></description></item>
/// <item><description><c>?garbage</c> -> null</description></item>
/// <item><description><c>?focus=route</c> (missing colon) -> null</description></item>
/// </list>
/// </summary>
public static class MapDeepLink
{
    /// <summary>Parsed deep-link target. <see cref="IsEdit"/> = true
    /// when the helm asked for the editor (came from <c>?edit=...</c>);
    /// false when the helm asked to focus + pan (<c>?focus=...</c>).</summary>
    public readonly record struct Target(string Kind, string Id, bool IsEdit);

    /// <summary>Parses the query portion of a URI (with or without the
    /// leading '?'). Returns null when no deep-link is present or when
    /// the value can't be split into <c>kind:id</c>. <c>edit</c> wins
    /// over <c>focus</c> if both are present so an editor link
    /// dominates a focus link in the same request.</summary>
    public static Target? Parse(string? query)
    {
        if (string.IsNullOrEmpty(query)) return null;

        string? raw = null;
        bool isEdit = false;
        // Single-pass scan: any 'edit=' wins immediately; 'focus='
        // is captured but loses to a later 'edit='. Avoids a second
        // pass over the segments to enforce precedence.
        foreach (var seg in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = seg.IndexOf('=');
            if (eq <= 0) continue;
            string key = seg[..eq];
            string val = Uri.UnescapeDataString(seg[(eq + 1)..]);
            if (key == "edit") { raw = val; isEdit = true; break; }
            if (key == "focus") { raw = val; isEdit = false; }
        }
        if (raw is null) return null;

        var parts = raw.Split(':', 2);
        if (parts.Length != 2) return null;
        if (parts[0].Length == 0 || parts[1].Length == 0) return null;
        return new Target(parts[0], parts[1], isEdit);
    }
}

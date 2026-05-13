using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.ServerNotifications;

namespace OnaPlotter.Services.Alarms;

/// <summary>
/// Surfaces server-side SignalK notifications (signalk-anchoralarm-plugin,
/// signalk-mob-notifier, depth alarms, etc.) into the alarm banner
/// stack. Reads the active set from <see cref="ServerNotificationStore"/>
/// on every Evaluate tick and emits one <see cref="AlarmInfo"/> per
/// active notification.
/// <para>
/// Multi-output: where typical rules return a single alarm via
/// <see cref="IAlarmRule.Check"/>, this rule overrides
/// <see cref="CheckMany"/> so multiple simultaneous server alerts
/// (e.g. depth + anchor at once) all reach the banner stack rather
/// than only the highest-severity one.
/// </para>
/// <para>
/// Title derivation tries to map well-known path prefixes to short
/// uppercase labels (DEPTH, ANCHOR, MOB, COLLISION, ...). Unknown
/// paths fall back to the leaf segment uppercased; the helm sees
/// the message body either way.
/// </para>
/// </summary>
public sealed class ServerNotificationsAlarmRule : IAlarmRule
{
    private readonly ServerNotificationStore _store;
    private readonly INotificationsApi? _api;
    private readonly IPublishedAlarmTracker? _publishedTracker;

    public ServerNotificationsAlarmRule(ServerNotificationStore store,
        INotificationsApi? api = null,
        IPublishedAlarmTracker? publishedTracker = null)
    {
        _store = store;
        _api = api;
        _publishedTracker = publishedTracker;
    }

    /// <summary>Empty title - this rule never goes through the
    /// single-output <see cref="Check"/> path. <see cref="CheckMany"/>
    /// builds a per-notification title via
    /// <see cref="DeriveTitleAndDefault"/>. Empty string is safer than
    /// a magic placeholder ("_SERVER_NOTIFICATIONS_") which a future
    /// debugger seeing it on the banner would chase across the codebase
    /// before noticing the doc.</summary>
    public string Title => string.Empty;

    /// <summary>Sits between collision (200) and wind shift (300). The
    /// per-AlarmInfo severity is what actually orders the stack; this
    /// only matters for deterministic ordering between two same-
    /// severity alarms.</summary>
    public int Priority => 250;

    /// <summary>Server clears notifications by setting state=normal or
    /// publishing null; the store removes the entry. Letting AlarmManager
    /// auto-drop the stack entry mirrors that lifecycle without needing
    /// a side-channel ClearByKey.</summary>
    public bool AutoClear => true;

    /// <summary>Single-output Check is unused for this rule - the
    /// store can have several notifications armed at once. Returning
    /// null here is correct: the manager calls
    /// <see cref="CheckMany"/> instead.</summary>
    public AlarmInfo? Check(AlarmEvaluationContext ctx) => null;

    public IEnumerable<AlarmInfo> CheckMany(AlarmEvaluationContext ctx)
    {
        foreach (var n in _store.Active)
        {
            // Honour the helm's snooze. The TargetKey is the path
            // (see BuildAlarmInfo), so snoozing
            // notifications.environment.depth.belowSurface silences
            // that one path while leaving sibling depth notifications
            // visible. Without this skip the rule re-asserts the
            // alarm on every Evaluate tick and the snooze is a no-op
            // for server-emitted alarms.
            if (ctx.IsSnoozed(n.Path)) continue;
            // Skip our own publication echoes. AlarmPublisher (Phase B)
            // POSTs locally-emitted alarms to the SK server so other
            // plotters see them; the same delta loops back to us via
            // the WS feed and lands here. Surfacing it would render
            // the same alarm twice in the banner stack - once from
            // the originating client rule, once from the bridge.
            if (_publishedTracker?.IsOwnedPath(n.Path) == true) continue;
            yield return BuildAlarmInfo(n, _api);
        }
    }

    /// <summary>Builds the banner-facing AlarmInfo. The path becomes
    /// the <see cref="AlarmInfo.TargetKey"/> so two notifications under
    /// the same Title (DEPTH at belowTransducer + DEPTH at belowSurface)
    /// dedup as separate entries rather than overwriting each other.
    /// V2 server-side ack flows through here too: when the server
    /// supplied an id and <c>status.canAcknowledge</c> is true, an
    /// <see cref="SignalKNotificationAcknowledger"/> is attached so
    /// <c>AlarmManager.DismissAsync</c> can trigger the cross-plotter
    /// ack POST. On pre-v2.21 servers (no id) or when the API client
    /// isn't wired (legacy test ctors) the <see cref="AlarmInfo.Acknowledger"/>
    /// field stays null and dismiss falls back to local-only.</summary>
    internal static AlarmInfo BuildAlarmInfo(ServerNotification n, INotificationsApi? api)
    {
        // Skip the HumaniseTail StringBuilder build when the upstream
        // notification already carried a message (the typical case for
        // OnaPlotter-published alarms and for plugins that populate
        // value.message). Only fall back to the humanised path tail
        // when there's no server-supplied text.
        string title;
        string message;
        if (!string.IsNullOrEmpty(n.Message))
        {
            title = DeriveTitle(n.Path);
            message = n.Message;
        }
        else
        {
            (title, message) = DeriveTitleAndDefault(n.Path);
        }
        // SignalK vessel contexts come through the path as
        // urn_mrn_imo_mmsi_<digits> (the v2 publisher's
        // SanitisePerTargetPath scrubs colons to underscores so the
        // suffix can't extend the path namespace). The humanised
        // default-message fallback or a server-published message
        // that re-uses the same id leaves that URN fragment in the
        // banner; rewrite to "MMSI <digits>" so the helm reads a
        // recognisable identifier instead of a 30-char garbage id.
        message = SanitiseUrnMmsi(message);
        // Build the acknowledger only when (a) we have an API client
        // wired (production DI; absent in some legacy test ctors) and
        // (b) the server actually gave us an id to address. The
        // CanAcknowledge field on the acknowledger then mirrors the
        // server's status.canAcknowledge - false for emergency-state
        // notifications the spec forbids silencing.
        IAlarmAcknowledger? ack = null;
        if (api is not null && n.Id is string id)
        {
            ack = new SignalKNotificationAcknowledger(
                api, id, canAcknowledge: n.Status?.CanAcknowledge ?? false);
        }
        return new AlarmInfo(
            Title: title,
            Message: message,
            Severity: n.Severity,
            TargetKey: n.Path,
            TargetLabel: title,
            // Server-decided alarms can be snoozed too - the snooze
            // suppresses the visible banner but doesn't talk back to
            // the server (we just stop surfacing). Useful when a noisy
            // plugin is firing on a path the helm has already
            // acknowledged via VHF / radio.
            Snoozeable: true,
            Acknowledger: ack);
    }

    /// <summary>Maps a SignalK notification path to a short banner
    /// title and a human-readable default message. Examples:
    /// <list type="bullet">
    /// <item>notifications.environment.depth.belowTransducer -> ("DEPTH", "below transducer")</item>
    /// <item>notifications.navigation.anchor.position -> ("ANCHOR", "position")</item>
    /// <item>notifications.environment.wind.shift -> ("WIND", "shift")</item>
    /// <item>notifications.mob -> ("MOB", "Man overboard")</item>
    /// <item>notifications.foo.bar.baz -> ("BAZ", "foo bar baz")</item>
    /// </list>
    /// Unknown prefixes degrade gracefully: leaf-segment uppercased
    /// becomes the title, the trailing path (with dots replaced and
    /// camelCase split) becomes the default message. The helm always
    /// gets SOMETHING informative even when a brand-new plugin shows
    /// up. Used as the FALLBACK when the upstream notification didn't
    /// include a <c>message</c> field; an OnaPlotter-published alarm
    /// almost always carries a richer message ("TWD shifted 60° in
    /// 30 min") that wins via the !string.IsNullOrEmpty check at the
    /// call site. See <see cref="DeriveTitle"/> for the title-only
    /// variant used on the typical (message-present) path.</summary>
    internal static (string title, string defaultMessage) DeriveTitleAndDefault(string path)
    {
        var tail = StripNotificationsPrefix(path);
        var (title, humanisePrefix, hardcodedDefault) = MatchKnownPath(tail);
        return (title, hardcodedDefault ?? HumaniseTail(tail, humanisePrefix));
    }

    /// <summary>Title-only variant for the hot path where the upstream
    /// notification already carries a <c>message</c>; skips the
    /// <see cref="HumaniseTail"/> StringBuilder build that the default-
    /// message branch needs. Same matching semantics as
    /// <see cref="DeriveTitleAndDefault"/>.</summary>
    internal static string DeriveTitle(string path)
    {
        var tail = StripNotificationsPrefix(path);
        return MatchKnownPath(tail).Title;
    }

    /// <summary>Strip the leading <c>notifications.</c> prefix when
    /// present so prefix matchers can work on the meaningful tail.</summary>
    private static string StripNotificationsPrefix(string path)
    {
        const string prefix = "notifications.";
        return path.StartsWith(prefix, StringComparison.Ordinal)
            ? path[prefix.Length..]
            : path;
    }

    /// <summary>Match a stripped notification tail against the known
    /// path-prefix table. Returns the banner title, the prefix to
    /// strip when humanising the default message, and a hardcoded
    /// default message (when one exists; otherwise <see cref="HumaniseTail"/>
    /// runs against the tail using <c>HumanisePrefix</c>).
    ///
    /// <para>Common plugins (anchor watch, MOB, depth, collision) get
    /// a recognised title so muscle memory carries from one chartplotter
    /// to the next. Unknown paths fall through to leaf-segment uppercased.</para>
    ///
    /// <para>The "APPROACH" title is load-bearing: MainLayout.razor
    /// checks <c>a.Title == "APPROACH"</c> to render the "Next WP"
    /// advance button on the banner. Course-provider bridged alarms
    /// use the same title so the layout doesn't need a per-rule case.</para>
    ///
    /// <para>Row shape: rows with a <c>HardcodedDefault</c> ignore
    /// <c>HumanisePrefix</c> (empty string sentinel); rows with a null
    /// <c>HardcodedDefault</c> use <c>HumanisePrefix</c> to strip the
    /// matched prefix from the tail before humanising the rest.</para></summary>
    private static (string Title, string HumanisePrefix, string? HardcodedDefault) MatchKnownPath(string tail)
    {
        if (tail.StartsWith("environment.depth.", StringComparison.Ordinal))
            return ("DEPTH", "environment.depth.", null);
        if (tail.StartsWith("navigation.anchor.", StringComparison.Ordinal))
            return ("ANCHOR", "navigation.anchor.", null);
        if (tail == "navigation.anchor")
            return ("ANCHOR", "", "anchor");
        if (tail.StartsWith("navigation.course.", StringComparison.Ordinal))
            return ("APPROACH", "navigation.course.", null);
        if (tail == "navigation.arrivalCircleEntered")
            return ("APPROACH", "", "arrival circle entered");
        if (tail == "navigation.perpendicularPassed")
            return ("APPROACH", "", "perpendicular passed");
        if (tail == "navigation.routeComplete")
            return ("APPROACH", "", "route complete");
        if (tail == "mob" || tail.StartsWith("mob.", StringComparison.Ordinal))
            return ("MOB", "", "Man overboard");
        if (tail.StartsWith("security.collision", StringComparison.Ordinal))
            return ("COLLISION", "security.", null);
        if (tail.StartsWith("environment.wind.", StringComparison.Ordinal))
            return ("WIND", "environment.wind.", null);
        if (tail.StartsWith("environment.fire.", StringComparison.Ordinal))
            return ("FIRE", "environment.fire.", null);
        if (tail.StartsWith("buddy.", StringComparison.Ordinal))
            return ("BUDDY", "buddy.", null);

        // Default: last path segment, uppercased. Reasonable banner
        // for any plugin we don't have a hard-coded mapping for.
        var lastDot = tail.LastIndexOf('.');
        var leaf = lastDot >= 0 ? tail[(lastDot + 1)..] : tail;
        return (leaf.ToUpperInvariant(), "", null);
    }

    /// <summary>Turn a SignalK path tail into a helm-readable phrase.
    /// Drops the supplied prefix, replaces dots with spaces, and
    /// inserts a space before each capital letter so camelCase reads
    /// as words. <c>environment.wind.shift</c> with prefix
    /// <c>environment.wind.</c> -&gt; <c>shift</c>.
    /// <c>environment.depth.belowTransducer</c> with prefix
    /// <c>environment.depth.</c> -&gt; <c>below transducer</c>.
    /// Empty result (the prefix consumed the whole tail) falls back
    /// to the prefix's own leaf so the banner never renders empty.</summary>
    private static string HumaniseTail(string tail, string knownPrefix)
    {
        var rest = !string.IsNullOrEmpty(knownPrefix)
                   && tail.StartsWith(knownPrefix, StringComparison.Ordinal)
            ? tail[knownPrefix.Length..]
            : tail;

        if (string.IsNullOrEmpty(rest))
        {
            // Whole tail was the known prefix (e.g. "navigation.anchor"
            // with prefix "navigation.anchor."). Fall back to the
            // last segment of the original tail.
            int dot = tail.LastIndexOf('.');
            rest = dot >= 0 ? tail[(dot + 1)..] : tail;
        }

        var sb = new System.Text.StringBuilder(rest.Length + 4);
        for (int i = 0; i < rest.Length; i++)
        {
            char c = rest[i];
            if (c == '.')
            {
                sb.Append(' ');
            }
            else if (i > 0 && char.IsUpper(c) && !char.IsUpper(rest[i - 1]))
            {
                // camelCase boundary: insert a space so "belowTransducer"
                // reads as "below transducer". Don't split runs of caps
                // (e.g. "MOB", "MMSI") so acronyms stay glued.
                sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Rewrite SignalK vessel-context URN fragments in a
    /// banner-bound message to a helm-readable "MMSI &lt;digits&gt;"
    /// form. The publisher path-sanitiser turns
    /// <c>vessels.urn:mrn:imo:mmsi:338546948</c> into
    /// <c>urn_mrn_imo_mmsi_338546948</c> so the suffix can't extend
    /// the notifications path namespace; that sanitisation also
    /// shows up in the humanised default message ("collision
    /// urn_mrn_imo_mmsi_338546948") and sometimes in upstream
    /// publisher messages too. The accepted shapes here cover both:
    ///   - <c>urn_mrn_imo_mmsi_NNN</c> (sanitised path tail)
    ///   - <c>urn:mrn:imo:mmsi:NNN</c> (raw context, in case a
    ///     publisher emits the message body with the original colons)
    /// In each case the URN fragment is replaced by <c>MMSI NNN</c>.
    /// Falls through unchanged when no MMSI fragment is present so
    /// non-vessel notifications (depth, anchor, MOB) don't get
    /// rewritten. No regex - a hand-rolled scan keeps the WASM
    /// startup cost down and the matching narrow.</summary>
    internal static string SanitiseUrnMmsi(string message)
    {
        if (string.IsNullOrEmpty(message)) return message;
        // Fast pre-check: bail without an allocation when the message
        // doesn't even contain "mmsi". The common-case banner (depth,
        // anchor, MOB) skips the rebuild entirely.
        if (message.IndexOf("mmsi", StringComparison.OrdinalIgnoreCase) < 0)
            return message;

        // Pattern length: "urn" + sep + "mrn" + sep + "imo" + sep
        // + "mmsi" + sep = 17. The MMSI digits follow.
        const int prefixLen = 17;

        var sb = new System.Text.StringBuilder(message.Length);
        int i = 0;
        while (i < message.Length)
        {
            int matchStart = FindUrnMmsiStart(message, i);
            if (matchStart < 0)
            {
                sb.Append(message, i, message.Length - i);
                break;
            }
            if (matchStart > i) sb.Append(message, i, matchStart - i);
            int digitsStart = matchStart + prefixLen;
            int digitsEnd = digitsStart;
            while (digitsEnd < message.Length && char.IsDigit(message[digitsEnd]))
                digitsEnd++;
            if (digitsEnd == digitsStart)
            {
                // "urn_mrn_imo_mmsi_" with no digits after - emit the
                // matched text verbatim so we don't lose data.
                sb.Append(message, matchStart, prefixLen);
            }
            else
            {
                sb.Append("MMSI ");
                sb.Append(message, digitsStart, digitsEnd - digitsStart);
            }
            i = digitsEnd;
        }
        return sb.ToString();
    }

    /// <summary>Find the start index of a urn[:_]mrn[:_]imo[:_]mmsi[:_]
    /// pattern at or after <paramref name="from"/>. Returns -1 when
    /// no match before end-of-string.</summary>
    private static int FindUrnMmsiStart(string s, int from)
    {
        const int prefixLen = 17;   // "urn?mrn?imo?mmsi?"
        int idx = from;
        while (true)
        {
            int u = s.IndexOf("urn", idx, StringComparison.OrdinalIgnoreCase);
            if (u < 0 || u + prefixLen > s.Length) return -1;
            // Require: urn[:_]mrn[:_]imo[:_]mmsi[:_]
            if (IsSep(s[u + 3])
                && s.AsSpan(u + 4, 3).Equals("mrn", StringComparison.OrdinalIgnoreCase) && IsSep(s[u + 7])
                && s.AsSpan(u + 8, 3).Equals("imo", StringComparison.OrdinalIgnoreCase) && IsSep(s[u + 11])
                && s.AsSpan(u + 12, 4).Equals("mmsi", StringComparison.OrdinalIgnoreCase) && IsSep(s[u + 16]))
            {
                return u;
            }
            idx = u + 1;
        }
    }

    private static bool IsSep(char c) => c == ':' || c == '_';
}

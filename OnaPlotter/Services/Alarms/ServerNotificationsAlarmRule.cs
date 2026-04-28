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

    /// <summary>Empty title -- this rule never goes through the
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

    /// <summary>Single-output Check is unused for this rule -- the
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
            // the same alarm twice in the banner stack -- once from
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
        var (title, defaultMsg) = DeriveTitleAndDefault(n.Path);
        var message = !string.IsNullOrEmpty(n.Message) ? n.Message : defaultMsg;
        // Build the acknowledger only when (a) we have an API client
        // wired (production DI; absent in some legacy test ctors) and
        // (b) the server actually gave us an id to address. The
        // CanAcknowledge field on the acknowledger then mirrors the
        // server's status.canAcknowledge -- false for emergency-state
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
            // Server-decided alarms can be snoozed too -- the snooze
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
    /// <item>notifications.environment.depth.belowTransducer -> ("DEPTH", "depth.belowTransducer")</item>
    /// <item>notifications.navigation.anchor.position -> ("ANCHOR", "anchor")</item>
    /// <item>notifications.mob -> ("MOB", "Man overboard")</item>
    /// <item>notifications.foo.bar.baz -> ("BAZ", "foo.bar.baz")</item>
    /// </list>
    /// Unknown prefixes degrade gracefully: leaf-segment uppercased
    /// becomes the title, the trailing path becomes the default
    /// message. The helm always gets SOMETHING informative even when
    /// a brand-new plugin shows up.</summary>
    internal static (string title, string defaultMessage) DeriveTitleAndDefault(string path)
    {
        // Strip the leading "notifications." prefix when present.
        const string prefix = "notifications.";
        var tail = path.StartsWith(prefix, StringComparison.Ordinal)
            ? path[prefix.Length..]
            : path;

        // Known path-prefix mappings. Keep the title list short and
        // helm-readable; commonly-shipped plugins (anchor watch, MOB,
        // depth, collision) get a recognised label so the helm's
        // muscle memory carries from one chartplotter to the next.
        if (tail.StartsWith("environment.depth.", StringComparison.Ordinal))
            return ("DEPTH", tail);
        if (tail.StartsWith("navigation.anchor", StringComparison.Ordinal))
            return ("ANCHOR", tail);
        if (tail == "mob" || tail.StartsWith("mob.", StringComparison.Ordinal))
            return ("MOB", "Man overboard");
        if (tail.StartsWith("security.collision", StringComparison.Ordinal))
            return ("COLLISION", tail);
        if (tail.StartsWith("environment.wind.", StringComparison.Ordinal))
            return ("WIND", tail);
        if (tail.StartsWith("environment.fire.", StringComparison.Ordinal))
            return ("FIRE", tail);
        if (tail.StartsWith("buddy.", StringComparison.Ordinal))
            return ("BUDDY", tail);

        // Default: last path segment, uppercased. Reasonable banner
        // for any plugin we don't have a hard-coded mapping for.
        var lastDot = tail.LastIndexOf('.');
        var leaf = lastDot >= 0 ? tail[(lastDot + 1)..] : tail;
        return (leaf.ToUpperInvariant(), tail);
    }
}

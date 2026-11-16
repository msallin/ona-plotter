namespace OnaPlotter.Components.Map;

/// <summary>Single-key chart shortcuts.</summary>
/// <remarks>When adding one: add the value here, a row in
/// <see cref="MapKeyboardShortcuts.All"/> (key + help-card label), an arm
/// in <see cref="MapKeyboardShortcuts.DispatchAsync"/>, and the handler
/// method on <see cref="IMapShortcutHandler"/>. The JS key gate and the
/// help card read the table, and MapKeyboardShortcutsTests fails for a
/// value without a dispatch arm.</remarks>
public enum MapShortcut
{
    Follow,
    Orientation,
    Night,
    Anchor,
    Mob,
    FitTrack,
    Laylines,
    Measure,
    RaceTimer,
    Help,
    Dismiss,
}

/// <summary>What a shortcut does on the Map page. Implemented
/// explicitly by <c>Map</c>.</summary>
public interface IMapShortcutHandler
{
    Task ToggleFollowAsync();
    Task CycleOrientationAsync();
    Task ToggleNightAsync();
    Task ToggleAnchorAsync();
    Task ToggleMobAsync();
    Task FitTrackAsync();
    Task ToggleLaylinesAsync();
    Task ToggleMeasureAsync();
    Task ToggleRaceTimerAsync();
    Task ToggleHelpAsync();
    Task DismissAsync();
}

/// <summary>
/// The shortcut table. One source for the keys <c>leafletInterop.js</c>
/// forwards to .NET (everything else stays with the browser), the help
/// card, and the dispatch.
/// </summary>
public static class MapKeyboardShortcuts
{
    /// <param name="Key">KeyboardEvent.key, lower-cased, as JS sends it.</param>
    /// <param name="Display">Key cap on the help card.</param>
    /// <param name="Label">What the key does, on the help card.</param>
    public sealed record Shortcut(string Key, string Display, string Label, MapShortcut Action);

    /// <summary>In help-card order.</summary>
    public static readonly IReadOnlyList<Shortcut> All =
    [
        new("f", "F", "Toggle follow", MapShortcut.Follow),
        new("o", "O", "Cycle orientation", MapShortcut.Orientation),
        new("n", "N", "Night mode", MapShortcut.Night),
        new("a", "A", "Anchor watch", MapShortcut.Anchor),
        new("m", "M", "Man overboard", MapShortcut.Mob),
        new("t", "T", "Fit track", MapShortcut.FitTrack),
        new("l", "L", "Show laylines", MapShortcut.Laylines),
        new("d", "D", "Measure (distance / bearing)", MapShortcut.Measure),
        new("r", "R", "Race timer", MapShortcut.RaceTimer),
        new("?", "?", "Show this card", MapShortcut.Help),
        new("escape", "Esc", "Dismiss / cancel current edit", MapShortcut.Dismiss),
    ];

    /// <summary>Keys the JS handler forwards.</summary>
    public static string[] Keys => All.Select(s => s.Key).ToArray();

    /// <summary>The shortcut bound to a forwarded key, if any.</summary>
    public static bool TryParse(string key, out MapShortcut action)
    {
        foreach (var s in All)
        {
            if (s.Key == key) { action = s.Action; return true; }
        }
        action = default;
        return false;
    }

    /// <summary>Runs the handler for <paramref name="action"/>.</summary>
    public static Task DispatchAsync(MapShortcut action, IMapShortcutHandler h) => action switch
    {
        MapShortcut.Follow => h.ToggleFollowAsync(),
        MapShortcut.Orientation => h.CycleOrientationAsync(),
        MapShortcut.Night => h.ToggleNightAsync(),
        MapShortcut.Anchor => h.ToggleAnchorAsync(),
        MapShortcut.Mob => h.ToggleMobAsync(),
        MapShortcut.FitTrack => h.FitTrackAsync(),
        MapShortcut.Laylines => h.ToggleLaylinesAsync(),
        MapShortcut.Measure => h.ToggleMeasureAsync(),
        MapShortcut.RaceTimer => h.ToggleRaceTimerAsync(),
        MapShortcut.Help => h.ToggleHelpAsync(),
        MapShortcut.Dismiss => h.DismissAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No handler for this shortcut."),
    };
}

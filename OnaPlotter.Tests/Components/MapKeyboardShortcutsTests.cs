using Bunit;
using OnaPlotter.Components.Map;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// The shortcut table is the single source for the keys JS forwards, the
/// help card and the dispatch. These tests pin that every bound action
/// dispatches to its own handler and every row is on the help card.
/// </summary>
public class MapKeyboardShortcutsTests
{
    private sealed class RecordingHandler : IMapShortcutHandler
    {
        public List<string> Calls { get; } = [];
        private Task Rec(string name) { Calls.Add(name); return Task.CompletedTask; }
        public Task ToggleFollowAsync() => Rec(nameof(ToggleFollowAsync));
        public Task CycleOrientationAsync() => Rec(nameof(CycleOrientationAsync));
        public Task ToggleNightAsync() => Rec(nameof(ToggleNightAsync));
        public Task ToggleAnchorAsync() => Rec(nameof(ToggleAnchorAsync));
        public Task ToggleMobAsync() => Rec(nameof(ToggleMobAsync));
        public Task FitTrackAsync() => Rec(nameof(FitTrackAsync));
        public Task ToggleLaylinesAsync() => Rec(nameof(ToggleLaylinesAsync));
        public Task ToggleMeasureAsync() => Rec(nameof(ToggleMeasureAsync));
        public Task ToggleRaceTimerAsync() => Rec(nameof(ToggleRaceTimerAsync));
        public Task ToggleHelpAsync() => Rec(nameof(ToggleHelpAsync));
        public Task DismissAsync() => Rec(nameof(DismissAsync));
    }

    [Test]
    public async Task EveryAction_IsBoundToExactlyOneKey()
    {
        foreach (var action in Enum.GetValues<MapShortcut>())
        {
            await Assert.That(MapKeyboardShortcuts.All.Count(s => s.Action == action)).IsEqualTo(1)
                .Because($"{action} needs one row in MapKeyboardShortcuts.All");
        }
        await Assert.That(MapKeyboardShortcuts.Keys.Distinct().Count()).IsEqualTo(MapKeyboardShortcuts.All.Count);
    }

    [Test]
    public async Task Keys_AreLowerCase_AsTheJsHandlerCompares()
    {
        foreach (var key in MapKeyboardShortcuts.Keys)
        {
            await Assert.That(key).IsEqualTo(key.ToLowerInvariant());
        }
    }

    [Test]
    public async Task EveryAction_DispatchesToItsOwnHandler()
    {
        // A new MapShortcut without a dispatch arm throws here.
        var handler = new RecordingHandler();
        foreach (var action in Enum.GetValues<MapShortcut>())
        {
            await MapKeyboardShortcuts.DispatchAsync(action, handler);
        }
        await Assert.That(handler.Calls.Count).IsEqualTo(Enum.GetValues<MapShortcut>().Length);
        await Assert.That(handler.Calls.Distinct().Count()).IsEqualTo(handler.Calls.Count);
    }

    [Test]
    [Arguments("d", MapShortcut.Measure)]
    [Arguments("?", MapShortcut.Help)]
    [Arguments("escape", MapShortcut.Dismiss)]
    public async Task TryParse_KnownKey(string key, MapShortcut expected)
    {
        await Assert.That(MapKeyboardShortcuts.TryParse(key, out var action)).IsTrue();
        await Assert.That(action).IsEqualTo(expected);
    }

    [Test]
    [Arguments("x")]
    [Arguments("F")]
    [Arguments("")]
    public async Task TryParse_UnboundKey_IsFalse(string key)
    {
        await Assert.That(MapKeyboardShortcuts.TryParse(key, out _)).IsFalse();
    }

    [Test]
    public async Task HelpCard_ListsEveryShortcut()
    {
        using var ctx = new Bunit.TestContext();
        var cut = ctx.RenderComponent<MapShortcutsOverlay>(p => p.Add(x => x.OnDismiss, () => { }));

        var caps = cut.FindAll(".shortcuts-grid > kbd").Select(e => e.TextContent).ToList();
        var labels = cut.Markup;
        foreach (var s in MapKeyboardShortcuts.All)
        {
            await Assert.That(caps).Contains(s.Display);
            await Assert.That(labels).Contains(s.Label);
        }
    }
}

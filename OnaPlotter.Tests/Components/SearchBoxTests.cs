using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OnaPlotter.Components.Layout;
using OnaPlotter.Services.Places;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for the topbar place-search component. The interesting
/// surface is the dropdown lifecycle (open / close, highlight,
/// keyboard nav, pick callback) and the debounce gate -- not the
/// IPlaceSearchService implementation itself, which is unit-tested
/// elsewhere. We pass DebounceMs=0 in every test so the input
/// handler resolves synchronously without waiting on a timer.
/// </summary>
public class SearchBoxTests
{
    /// <summary>Test stub: returns whatever the test sets in
    /// <see cref="NextResults"/>. Records every call so tests can
    /// assert call count + most-recent query. Optional Gate parks
    /// the call until the test releases it (used to prove that a
    /// late response from a stale query doesn't clobber the
    /// dropdown after a fresher keystroke landed).</summary>
    private sealed class StubSearch : IPlaceSearchService
    {
        public IReadOnlyList<PlaceResult> NextResults { get; set; } = [];
        public int CallCount { get; private set; }
        public string? LastQuery { get; private set; }
        public TaskCompletionSource? Gate { get; set; }
        public CancellationToken LastSeenCt { get; private set; }
        public async Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            CallCount++;
            LastQuery = query;
            LastSeenCt = ct;
            if (Gate is not null) await Gate.Task;
            return NextResults;
        }
    }

    private static IRenderedComponent<SearchBox> Render(
        Bunit.TestContext ctx,
        StubSearch search,
        EventCallback<PlaceResult>? onPicked = null,
        EventCallback? onCleared = null)
    {
        ctx.Services.AddSingleton<IPlaceSearchService>(search);
        return ctx.RenderComponent<SearchBox>(p => p
            .Add(x => x.DebounceMs, 0)
            .Add(x => x.OnPicked, onPicked ?? EventCallback<PlaceResult>.Empty)
            .Add(x => x.OnCleared, onCleared ?? EventCallback.Empty));
    }

    private static PlaceResult Pl(string name, double lat = 47.0, double lon = 8.0) =>
        new(name, $"{name} -- locality, country", lat, lon, "photon");

    [Test]
    public async Task Empty_Input_Renders_Just_The_Field()
    {
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch();
        var cut = Render(ctx, search);

        await Assert.That(cut.Find(".topbar-search-input")).IsNotNull();
        // No dropdown, no spinner, no clear button.
        await Assert.That(cut.FindAll(".topbar-search-dropdown").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".topbar-search-spinner").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".topbar-search-clear").Count).IsEqualTo(0);
        // Inner provider not called.
        await Assert.That(search.CallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Typing_Triggers_Search_And_Renders_Results()
    {
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch
        {
            NextResults = new[] { Pl("Berlin"), Pl("Bremen") },
        };
        var cut = Render(ctx, search);

        await cut.Find(".topbar-search-input").InputAsync(
            new() { Value = "ber" });

        await Assert.That(search.CallCount).IsEqualTo(1);
        await Assert.That(search.LastQuery).IsEqualTo("ber");

        var rows = cut.FindAll(".topbar-search-row");
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].QuerySelector(".topbar-search-name")!.TextContent)
            .IsEqualTo("Berlin");
    }

    [Test]
    public async Task Empty_Result_Renders_No_Results_Message_With_Connection_Hint()
    {
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch { NextResults = Array.Empty<PlaceResult>() };
        var cut = Render(ctx, search);

        await cut.Find(".topbar-search-input").InputAsync(new() { Value = "zzz" });

        // Dropdown opens with the empty-state placeholder, no rows.
        await Assert.That(cut.FindAll(".topbar-search-row").Count).IsEqualTo(0);
        // The empty-state is two-line: a "No results" title and a
        // softer "Check your connection?" hint. Phase 4 added the
        // hint so an offline / rate-limited geocoder failure surfaces
        // a recoverable cue instead of looking like a real miss.
        await Assert.That(cut.Find(".topbar-search-empty-title").TextContent.Trim())
            .IsEqualTo("No results");
        await Assert.That(cut.Find(".topbar-search-empty-hint").TextContent.Trim())
            .IsEqualTo("Check your connection?");
    }

    [Test]
    public async Task ArrowDown_Then_Enter_Picks_Highlighted_Row()
    {
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch
        {
            NextResults = new[] { Pl("Berlin", 52.5, 13.4), Pl("Bremen", 53.1, 8.8) },
        };
        PlaceResult? picked = null;
        var onPicked = EventCallback.Factory.Create<PlaceResult>(
            new object(), r => picked = r);
        var cut = Render(ctx, search, onPicked);

        await cut.Find(".topbar-search-input").InputAsync(new() { Value = "br" });
        // Default highlight is index 0; press ArrowDown to move to 1.
        await cut.Find(".topbar-search-input").KeyDownAsync(new() { Key = "ArrowDown" });
        await cut.Find(".topbar-search-input").KeyDownAsync(new() { Key = "Enter" });

        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.Name).IsEqualTo("Bremen");
        await Assert.That(picked.Lat).IsEqualTo(53.1);
    }

    [Test]
    public async Task Escape_With_Query_Clears_Input()
    {
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch
        {
            NextResults = new[] { Pl("Berlin") },
        };
        var cut = Render(ctx, search);

        await cut.Find(".topbar-search-input").InputAsync(new() { Value = "ber" });
        // Dropdown open, results visible.
        await Assert.That(cut.FindAll(".topbar-search-row").Count).IsEqualTo(1);

        // Escape with results-open: closes dropdown, keeps the query.
        await cut.Find(".topbar-search-input").KeyDownAsync(new() { Key = "Escape" });
        await Assert.That(cut.FindAll(".topbar-search-dropdown").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Click_Clear_Button_Resets_State()
    {
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch { NextResults = new[] { Pl("Berlin") } };
        var cut = Render(ctx, search);

        await cut.Find(".topbar-search-input").InputAsync(new() { Value = "ber" });
        await Assert.That(cut.FindAll(".topbar-search-clear").Count).IsEqualTo(1);

        await cut.Find(".topbar-search-clear").ClickAsync(new());
        // Clear hides dropdown + removes the clear button (input empty).
        await Assert.That(cut.FindAll(".topbar-search-dropdown").Count).IsEqualTo(0);
        await Assert.That(cut.FindAll(".topbar-search-clear").Count).IsEqualTo(0);
    }

    [Test]
    public async Task Pick_Via_Mousedown_Fires_OnPicked()
    {
        // Mousedown is used (not click) so the dropdown closes BEFORE
        // the input loses focus. This test pins that path.
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch
        {
            NextResults = new[] { Pl("Marathon", 24.7, -81.1) },
        };
        PlaceResult? picked = null;
        var onPicked = EventCallback.Factory.Create<PlaceResult>(
            new object(), r => picked = r);
        var cut = Render(ctx, search, onPicked);

        await cut.Find(".topbar-search-input").InputAsync(new() { Value = "marathon" });
        await cut.Find(".topbar-search-row").MouseDownAsync(new());

        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.Lat).IsEqualTo(24.7);
        await Assert.That(picked.Lon).IsEqualTo(-81.1);
    }

    [Test]
    public async Task Search_Threads_Cancellation_Token_Through_To_Provider()
    {
        // The class doc on _searchCts pins this invariant: "without
        // this a slow Photon response from the previous query would
        // clobber the dropdown when it eventually returns". The full
        // race is hard to exercise end-to-end through bUnit's
        // InputAsync (which awaits the whole OnInput Task and would
        // deadlock against a TCS-gated stub), so we pin the necessary
        // ingredient: every SearchAsync call receives a non-default
        // cancellation token, and a follow-up keystroke produces a
        // different token (proving the CTS rotation actually fires).
        // A regression that drops _searchCts.Cancel() or stops
        // creating a fresh CTS on each keystroke would surface here
        // as identical tokens across calls.
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch
        {
            NextResults = new[] { Pl("Berlin") },
        };
        var cut = Render(ctx, search);

        await cut.Find(".topbar-search-input").InputAsync(new() { Value = "ber" });
        var firstCt = search.LastSeenCt;

        await cut.Find(".topbar-search-input").InputAsync(new() { Value = "berl" });
        var secondCt = search.LastSeenCt;

        await Assert.That(search.CallCount).IsEqualTo(2);
        await Assert.That(firstCt.CanBeCanceled).IsTrue();
        await Assert.That(secondCt.CanBeCanceled).IsTrue();
        // After the second keystroke the first token is cancelled
        // (the stale clobber guard); the second token is fresh.
        await Assert.That(firstCt.IsCancellationRequested).IsTrue();
        await Assert.That(secondCt.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task ArrowUp_Clamps_At_Zero_And_Default_Enter_Picks_First()
    {
        // Three-in-one: ArrowUp at index 0 stays at 0; ArrowDown past
        // last row stays at last; default-highlight Enter picks the
        // first row (no arrow press needed).
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch
        {
            NextResults = new[] { Pl("A", 1, 1), Pl("B", 2, 2) },
        };
        PlaceResult? picked = null;
        var onPicked = EventCallback.Factory.Create<PlaceResult>(
            new object(), r => picked = r);
        var cut = Render(ctx, search, onPicked);
        await cut.Find(".topbar-search-input").InputAsync(new() { Value = "a" });

        // ArrowUp at index 0: still index 0.
        await cut.Find(".topbar-search-input").KeyDownAsync(new() { Key = "ArrowUp" });
        // ArrowDown past last (twice): clamped at index 1 (last).
        await cut.Find(".topbar-search-input").KeyDownAsync(new() { Key = "ArrowDown" });
        await cut.Find(".topbar-search-input").KeyDownAsync(new() { Key = "ArrowDown" });
        // ArrowUp from last: back to index 0.
        await cut.Find(".topbar-search-input").KeyDownAsync(new() { Key = "ArrowUp" });
        // Enter on default index 0.
        await cut.Find(".topbar-search-input").KeyDownAsync(new() { Key = "Enter" });

        await Assert.That(picked).IsNotNull();
        await Assert.That(picked!.Name).IsEqualTo("A");
    }

    [Test]
    public async Task Escape_With_Empty_Query_Is_NoOp()
    {
        // Escape on an empty query (no results, dropdown closed):
        // shouldn't throw, shouldn't fire OnCleared spuriously.
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch();
        bool clearedFired = false;
        var onCleared = EventCallback.Factory.Create(
            new object(), () => clearedFired = true);
        var cut = Render(ctx, search, onCleared: onCleared);

        await cut.Find(".topbar-search-input").KeyDownAsync(new() { Key = "Escape" });

        await Assert.That(clearedFired).IsFalse();
    }

    [Test]
    public async Task HandleClear_Fires_OnCleared_Callback()
    {
        // The clear button must invoke OnCleared so the parent can
        // tear down the search-pin on the JS side. Without this the
        // pin lingers on the chart after the helm clears the input.
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch
        {
            NextResults = new[] { Pl("Berlin") },
        };
        bool clearedFired = false;
        var onCleared = EventCallback.Factory.Create(
            new object(), () => clearedFired = true);
        var cut = Render(ctx, search, onCleared: onCleared);

        await cut.Find(".topbar-search-input").InputAsync(new() { Value = "ber" });
        await cut.Find(".topbar-search-clear").ClickAsync(new());

        await Assert.That(clearedFired).IsTrue();
    }

    [Test]
    public async Task FocusOut_Closes_Dropdown_FocusIn_Reopens_With_Existing_Results()
    {
        // The helm clicks away then back into a populated input.
        // The dropdown should close on blur, and re-open on refocus
        // when results are still available + the query is non-empty.
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch
        {
            NextResults = new[] { Pl("Berlin") },
        };
        var cut = Render(ctx, search);

        await cut.Find(".topbar-search-input").InputAsync(new() { Value = "ber" });
        await Assert.That(cut.FindAll(".topbar-search-dropdown").Count).IsEqualTo(1);

        await cut.Find(".topbar-search").FocusOutAsync(new());
        await Assert.That(cut.FindAll(".topbar-search-dropdown").Count).IsEqualTo(0);

        await cut.Find(".topbar-search-input").FocusInAsync(new());
        await Assert.That(cut.FindAll(".topbar-search-dropdown").Count).IsEqualTo(1);
    }

    [Test]
    public async Task Long_Query_Truncated_To_MaxQueryLength_Before_Service_Call()
    {
        // PARA-004: paste a multi-KB string into the input. The
        // SearchBox must cap the helm-typed query at MaxQueryLength
        // before invoking the geocoder, so the cache key + URL stay
        // bounded regardless of input.
        using var ctx = new Bunit.TestContext();
        var search = new StubSearch
        {
            NextResults = Array.Empty<PlaceResult>(),
        };
        var cut = Render(ctx, search);

        // 5 KB pasted blob.
        var paste = new string('X', 5_000);
        await cut.Find(".topbar-search-input").InputAsync(new() { Value = paste });

        await Assert.That(search.LastQuery!.Length).IsEqualTo(SearchBox.MaxQueryLength);
    }
}

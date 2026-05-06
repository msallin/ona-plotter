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
    /// assert call count + most-recent query.</summary>
    private sealed class StubSearch : IPlaceSearchService
    {
        public IReadOnlyList<PlaceResult> NextResults { get; set; } = [];
        public int CallCount { get; private set; }
        public string? LastQuery { get; private set; }
        public Task<IReadOnlyList<PlaceResult>> SearchAsync(string query, CancellationToken ct = default)
        {
            CallCount++;
            LastQuery = query;
            return Task.FromResult(NextResults);
        }
    }

    private static IRenderedComponent<SearchBox> Render(
        Bunit.TestContext ctx,
        StubSearch search,
        EventCallback<PlaceResult>? onPicked = null)
    {
        ctx.Services.AddSingleton<IPlaceSearchService>(search);
        return ctx.RenderComponent<SearchBox>(p => p
            .Add(x => x.DebounceMs, 0)
            .Add(x => x.OnPicked, onPicked ?? EventCallback<PlaceResult>.Empty));
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
}

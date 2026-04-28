using Bunit;
using OnaPlotter.Components.Map.Layers;

namespace OnaPlotter.Tests.Components;

/// <summary>
/// bUnit tests for MiscSection. Section bundles the "Rain radar"
/// (RainViewer) toggle with the now-attached opacity slider. Tests
/// pin:
///   - opacity slider only appears when the radar checkbox is on
///     (a slider for an inactive overlay reads as broken)
///   - the slider's preview vs commit split (helm-asked PERF-002:
///     drag fires preview, release fires commit)
///   - parse + clamp on the slider input value
///   - slider value attribute matches the parameter
/// </summary>
public class MiscSectionTests
{
    private static IRenderedComponent<MiscSection> RenderExpanded(
        Bunit.TestContext ctx,
        bool weatherVisible = false,
        int opacityPercent = 50,
        Action<int>? onPreview = null,
        Action<int>? onCommit = null)
    {
        var cut = ctx.RenderComponent<MiscSection>(p =>
        {
            p.Add(x => x.WeatherVisible, weatherVisible);
            p.Add(x => x.WeatherOpacityPercent, opacityPercent);
            if (onPreview is not null)
                p.Add(x => x.OnWeatherOpacityPreview, onPreview);
            if (onCommit is not null)
                p.Add(x => x.OnWeatherOpacityCommit, onCommit);
        });
        cut.Find(".section-toggle").Click();
        return cut;
    }

    [Test]
    public async Task OpacityRow_HiddenWhen_WeatherDisabled()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderExpanded(ctx, weatherVisible: false);
        await Assert.That(cut.FindAll(".weather-opacity-row").Count).IsEqualTo(0);
    }

    [Test]
    public async Task OpacityRow_VisibleWhen_WeatherEnabled()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderExpanded(ctx, weatherVisible: true);
        await Assert.That(cut.FindAll(".weather-opacity-row").Count).IsEqualTo(1);
    }

    [Test]
    public async Task OpacitySlider_InitialValue_MatchesParameter()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderExpanded(ctx, weatherVisible: true, opacityPercent: 35);
        var input = cut.Find(".weather-opacity-slider");
        await Assert.That(input.GetAttribute("value")).IsEqualTo("35");
    }

    [Test]
    public async Task OpacitySlider_ShowsPercentLabel()
    {
        using var ctx = new Bunit.TestContext();
        var cut = RenderExpanded(ctx, weatherVisible: true, opacityPercent: 70);
        await Assert.That(cut.Find(".weather-opacity-value").TextContent).IsEqualTo("70%");
    }

    // === preview vs commit split (PERF-002) ===

    [Test]
    public async Task OnInput_FiresPreview_NotCommit()
    {
        using var ctx = new Bunit.TestContext();
        int previewCalls = 0, commitCalls = 0;
        int previewLast = -1;
        var cut = RenderExpanded(ctx, weatherVisible: true,
            onPreview: v => { previewCalls++; previewLast = v; },
            onCommit: _ => commitCalls++);

        cut.Find(".weather-opacity-slider").Input("65");

        await Assert.That(previewCalls).IsEqualTo(1);
        await Assert.That(previewLast).IsEqualTo(65);
        await Assert.That(commitCalls).IsEqualTo(0);
    }

    [Test]
    public async Task OnChange_FiresCommit_NotPreview()
    {
        using var ctx = new Bunit.TestContext();
        int previewCalls = 0, commitCalls = 0;
        int commitLast = -1;
        var cut = RenderExpanded(ctx, weatherVisible: true,
            onPreview: _ => previewCalls++,
            onCommit: v => { commitCalls++; commitLast = v; });

        cut.Find(".weather-opacity-slider").Change("80");

        await Assert.That(commitCalls).IsEqualTo(1);
        await Assert.That(commitLast).IsEqualTo(80);
        await Assert.That(previewCalls).IsEqualTo(0);
    }

    // === clamp + parse ===

    [Test]
    public async Task OnInput_BelowFloor_ClampsTo5()
    {
        using var ctx = new Bunit.TestContext();
        int got = -1;
        var cut = RenderExpanded(ctx, weatherVisible: true,
            onPreview: v => got = v);
        cut.Find(".weather-opacity-slider").Input("0");
        await Assert.That(got).IsEqualTo(5);
    }

    [Test]
    public async Task OnInput_AboveCeiling_ClampsTo95()
    {
        using var ctx = new Bunit.TestContext();
        int got = -1;
        var cut = RenderExpanded(ctx, weatherVisible: true,
            onPreview: v => got = v);
        cut.Find(".weather-opacity-slider").Input("200");
        await Assert.That(got).IsEqualTo(95);
    }

    [Test]
    public async Task OnInput_NotANumber_NoCallback()
    {
        // TryParse returns false for "banana" -> early-return path.
        // Without this guard a malformed touchscreen event would
        // produce a NaN downstream of the slider.
        using var ctx = new Bunit.TestContext();
        int previewCalls = 0;
        var cut = RenderExpanded(ctx, weatherVisible: true,
            onPreview: _ => previewCalls++);
        cut.Find(".weather-opacity-slider").Input("banana");
        await Assert.That(previewCalls).IsEqualTo(0);
    }

    [Test]
    public async Task OnInput_EmptyString_NoCallback()
    {
        using var ctx = new Bunit.TestContext();
        int previewCalls = 0;
        var cut = RenderExpanded(ctx, weatherVisible: true,
            onPreview: _ => previewCalls++);
        cut.Find(".weather-opacity-slider").Input("");
        await Assert.That(previewCalls).IsEqualTo(0);
    }

    [Test]
    public async Task OnInput_InRange_PassesThrough()
    {
        using var ctx = new Bunit.TestContext();
        int got = -1;
        var cut = RenderExpanded(ctx, weatherVisible: true,
            onPreview: v => got = v);
        cut.Find(".weather-opacity-slider").Input("42");
        await Assert.That(got).IsEqualTo(42);
    }
}

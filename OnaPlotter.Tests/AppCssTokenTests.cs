using System.Text.RegularExpressions;

namespace OnaPlotter.Tests;

/// <summary>
/// Static checks over <c>wwwroot/css/app.css</c> (and the scoped
/// <c>*.razor.css</c> / inline Razor styles) that keep the design-token
/// system honest:
/// <list type="bullet">
/// <item>every <c>var(--token)</c> resolves to a token the stylesheet
///   defines, so nothing silently falls back to a hardcoded colour that
///   skips night mode and the light themes;</item>
/// <item>night mode redefines every token the light theme redefines, so
///   no light-or-dark-only value leaks white or blue chrome into the
///   red-shifted palette;</item>
/// <item>the light token block applies to the System theme only when the
///   OS is light (via <c>.theme-effective-light</c>), so System on a dark
///   OS keeps the full dark palette.</item>
/// </list>
/// </summary>
public class AppCssTokenTests
{
    private static readonly string WebRoot = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "OnaPlotter");

    private static readonly string CssPath = Path.Combine(WebRoot, "wwwroot", "css", "app.css");

    // Tokens defined outside app.css: Bootstrap's --bs-* palette, and
    // --map-rot, which leafletInterop.js writes on <html> for course-up.
    private static readonly Regex ExternallyDefined = new(@"^--(bs-|map-rot$)", RegexOptions.Compiled);

    // "/* ... */" comments, including multi-line ones.
    private static readonly Regex Comment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    // Declarations: "    --sev-warn:   #f59e0b;" -> "--sev-warn".
    private static readonly Regex TokenDeclaration = new(@"(--[\w-]+)\s*:", RegexOptions.Compiled);

    // References: "rgba(var(--sev-warn-rgb), 0.5)" -> "--sev-warn-rgb".
    private static readonly Regex TokenReference = new(@"var\(\s*(--[\w-]+)", RegexOptions.Compiled);

    private static string? ReadCss() => File.Exists(CssPath) ? Comment.Replace(File.ReadAllText(CssPath), "") : null;

    /// <summary>Body of the first rule whose selector text starts with
    /// <paramref name="selectorStart"/>; null when absent.</summary>
    private static string? RuleBody(string css, string selectorStart, out string selector)
    {
        selector = "";
        int at = css.IndexOf(selectorStart, StringComparison.Ordinal);
        if (at < 0) return null;
        int open = css.IndexOf('{', at);
        int close = css.IndexOf('}', open);
        selector = css[at..open];
        return css[(open + 1)..close];
    }

    [Test]
    public async Task EveryVarReference_ResolvesToADefinedToken()
    {
        string? css = ReadCss();
        // Assembly copied without the repo layout (e.g. dotnet publish):
        // nothing to check. CI runs from the repo root.
        if (css is null) return;

        var defined = TokenDeclaration.Matches(css).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var sources = new List<(string File, string Text)> { (CssPath, css) };
        foreach (var file in Directory.EnumerateFiles(Path.Combine(WebRoot, "Components"), "*.razor*", SearchOption.AllDirectories))
            sources.Add((file, Comment.Replace(File.ReadAllText(file), "")));

        var undefined = sources
            .SelectMany(s => TokenReference.Matches(s.Text).Select(m => (s.File, Token: m.Groups[1].Value)))
            .Where(r => !defined.Contains(r.Token) && !ExternallyDefined.IsMatch(r.Token))
            .Select(r => $"{r.Token} in {Path.GetFileName(r.File)}")
            .Distinct()
            .ToList();

        await Assert.That(undefined).IsEmpty()
            .Because("an undefined token silently uses its fallback (or nothing), bypassing the theme and night palettes");
    }

    [Test]
    public async Task NightMode_RedefinesEveryTokenTheLightThemeRedefines()
    {
        string? css = ReadCss();
        if (css is null) return;

        string? light = RuleBody(css, ":root.theme-light", out _);
        string? night = RuleBody(css, ".night-mode-global {", out _);
        await Assert.That(light).IsNotNull();
        await Assert.That(night).IsNotNull();

        var lightTokens = TokenDeclaration.Matches(light!).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var nightTokens = TokenDeclaration.Matches(night!).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        // A token the light theme has to retune is colour-bearing; if
        // night mode leaves it at the dark default, that dark value
        // (white ink, blue accent, bright severity) shows at night.
        var missing = lightTokens.Except(nightTokens).OrderBy(t => t, StringComparer.Ordinal).ToList();
        await Assert.That(missing).IsEmpty();
    }

    [Test]
    public async Task LightTokenBlock_AppliesToSystemThemeOnlyOnALightOs()
    {
        string? css = ReadCss();
        if (css is null) return;

        await Assert.That(RuleBody(css, ":root.theme-light", out string selector)).IsNotNull();

        // Each selector in the list that names theme-system must also
        // require theme-effective-light; a bare ":root.theme-system"
        // would paint the light palette onto System + dark OS.
        var systemSelectors = selector.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Contains("theme-system", StringComparison.Ordinal))
            .ToList();
        await Assert.That(systemSelectors).IsNotEmpty();
        foreach (string s in systemSelectors)
            await Assert.That(s).Contains(".theme-effective-light");
    }
}

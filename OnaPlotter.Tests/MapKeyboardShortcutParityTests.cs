using System.Text.RegularExpressions;

namespace OnaPlotter.Tests;

/// <summary>
/// Pins three-way parity between
///   1) the JS letter allowlist in
///      <c>OnaPlotter/wwwroot/js/leafletInterop.js</c>
///      (the gate that decides which keys reach .NET),
///   2) the C# <c>OnKeyShortcut</c> switch in
///      <c>OnaPlotter/Components/Pages/Map.razor</c>
///      (the handler that acts on them), and
///   3) the help card in
///      <c>OnaPlotter/Components/Map/MapShortcutsOverlay.razor</c>
///      (what we tell the user is wired up).
///
/// <para>Caught a real production bug: the letter set listed
/// <c>"mfnatlor"</c> (8 letters) while the C# switch handled 9 cases
/// including <c>"d"</c> for Measure, and the help card documented
/// <c>D</c>. Pressing D did nothing because the JS gate dropped it
/// before invokeMethodAsync could fire. A whole-class regression
/// guard makes any future drift fail on CI rather than at the
/// helm.</para>
/// </summary>
public class MapKeyboardShortcutParityTests
{
    private static string ReadRepoFile(params string[] relativeSegments)
    {
        var segments = new List<string>
        {
            AppContext.BaseDirectory, "..", "..", "..", ".."
        };
        segments.AddRange(relativeSegments);
        return File.ReadAllText(Path.Combine(segments.ToArray()));
    }

    /// <summary>Extracts the single-quoted string passed to
    /// <c>includes(key)</c> in the keyboard handler. Pinned to the
    /// `isLetter` line so a different `includes` call elsewhere in
    /// the file can't accidentally match.</summary>
    private static string ExtractJsLetterSet()
    {
        var js = ReadRepoFile("OnaPlotter", "wwwroot", "js", "leafletInterop.js");
        // Example line:
        //   const isLetter = 'fonamtldr'.includes(key) && key.length === 1;
        var rx = new Regex(@"isLetter\s*=\s*'(?<set>[a-z]+)'\.includes\(key\)");
        var m = rx.Match(js);
        if (!m.Success)
            throw new InvalidOperationException(
                "Could not find the isLetter allowlist in leafletInterop.js. " +
                "If the keyboard handler moved, update this test's regex.");
        return m.Groups["set"].Value;
    }

    /// <summary>Extracts every single-character lowercase letter case
    /// from the OnKeyShortcut switch in Map.razor. The switch is
    /// anchored by its method signature so unrelated `case "x":`
    /// lines elsewhere in Map.razor don't pollute the set.</summary>
    private static HashSet<char> ExtractCSharpShortcutLetters()
    {
        var razor = ReadRepoFile("OnaPlotter", "Components", "Pages", "Map.razor");

        // Find the OnKeyShortcut method body, bounded by its
        // declaration and the next method (a closing-brace-at-margin
        // followed by an attribute or method header).
        var startIdx = razor.IndexOf("public async Task OnKeyShortcut(", StringComparison.Ordinal);
        if (startIdx < 0)
            throw new InvalidOperationException(
                "Could not locate OnKeyShortcut in Map.razor. " +
                "If renamed, update this test.");

        // Crude but stable: scan forward until we see "[JSInvokable]"
        // or "public " at indent 4 which marks the next member.
        var endMarker = "    [JSInvokable]";
        var endIdx = razor.IndexOf(endMarker, startIdx + 1, StringComparison.Ordinal);
        if (endIdx < 0) endIdx = razor.Length;
        var body = razor[startIdx..endIdx];

        // Example case lines:
        //   case "m": await ToggleMob(); break;
        //   case "?": ...
        //   case "escape": ...
        // We want lowercase single-letter cases only.
        var rx = new Regex("case\\s+\"(?<key>[a-z])\"\\s*:");
        var letters = new HashSet<char>();
        foreach (Match m in rx.Matches(body))
        {
            letters.Add(m.Groups["key"].Value[0]);
        }
        return letters;
    }

    /// <summary>Extracts every <c>&lt;kbd&gt;X&lt;/kbd&gt;</c>
    /// single-letter row from the help card so the documented set
    /// can be compared against the wired set.</summary>
    private static HashSet<char> ExtractHelpCardLetters()
    {
        var razor = ReadRepoFile("OnaPlotter", "Components", "Map", "MapShortcutsOverlay.razor");
        // Match single-letter <kbd>X</kbd> rows (uppercase, A-Z).
        // Skip arrows / "Shift" / "Esc" / "Space" / "Enter" / "?" -
        // those are special keys or chord modifiers, not letter
        // shortcuts the JS allowlist gates.
        var rx = new Regex(@"<kbd>(?<key>[A-Z])</kbd>");
        var letters = new HashSet<char>();
        foreach (Match m in rx.Matches(razor))
        {
            letters.Add(char.ToLowerInvariant(m.Groups["key"].Value[0]));
        }
        return letters;
    }

    [Test]
    public async Task JsLetterSet_MatchesCSharpSwitchCases()
    {
        var jsSet = ExtractJsLetterSet().ToHashSet();
        var csSet = ExtractCSharpShortcutLetters();

        // Sort for a readable diff if it fails.
        var jsSorted = string.Concat(jsSet.OrderBy(c => c));
        var csSorted = string.Concat(csSet.OrderBy(c => c));

        await Assert.That(jsSorted).IsEqualTo(csSorted);
    }

    [Test]
    public async Task HelpCardLetters_MatchCSharpSwitchCases()
    {
        var helpSet = ExtractHelpCardLetters();
        var csSet = ExtractCSharpShortcutLetters();

        var helpSorted = string.Concat(helpSet.OrderBy(c => c));
        var csSorted = string.Concat(csSet.OrderBy(c => c));

        await Assert.That(helpSorted).IsEqualTo(csSorted);
    }

    [Test]
    public async Task JsHandler_AlsoForwardsQuestionMarkAndEscape()
    {
        // The two special keys are checked separately from the
        // letter set in the JS handler; pin both so a refactor can't
        // silently drop them.
        var js = ReadRepoFile("OnaPlotter", "wwwroot", "js", "leafletInterop.js");
        await Assert.That(js).Contains("key === '?'");
        await Assert.That(js).Contains("key === 'escape'");
    }
}

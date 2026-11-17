using System.Globalization;
using System.Text;

namespace OnaPlotter.Utilities;

/// <summary>
/// SVG <c>&lt;text&gt;</c> markup for the dials and charts. Razor reserves
/// <c>&lt;text&gt;</c> as a literal-text block marker in component markup,
/// so these elements are emitted as raw markup (wrapped in a
/// <c>MarkupString</c> by the caller). Content is HTML-encoded.
/// <para>
/// Attribute order is fixed: <c>x y (class | style fill | fill) font-size
/// font-weight text-anchor dominant-baseline</c>; absent parts are skipped.
/// </para>
/// </summary>
public static class SvgText
{
    /// <param name="cssClass">Class-routed colour, so the fill follows the
    /// theme tokens instead of being baked into the markup.</param>
    /// <param name="styleFill">Fill as <c>style="fill: ..."</c>, which
    /// (unlike the attribute) resolves CSS <c>var()</c> tokens.</param>
    /// <param name="fillAttr">Fill as a presentation attribute; literal
    /// colours only.</param>
    /// <param name="coordFormat">Number format for x / y; null keeps the
    /// shortest round-trip form ("20" rather than "20.0").</param>
    public static string Element(double x, double y, string content,
        string? cssClass = null, string? styleFill = null, string? fillAttr = null,
        int? fontSize = null, string? fontWeight = null,
        string anchor = "middle", bool centralBaseline = true, string? coordFormat = "F1")
    {
        var sb = new StringBuilder(96);
        Append(sb, x, y, content, cssClass, styleFill, fillAttr, fontSize, fontWeight,
            anchor, centralBaseline, coordFormat);
        return sb.ToString();
    }

    /// <summary>Same markup as <see cref="Element"/>, appended to an
    /// existing builder.</summary>
    public static void Append(StringBuilder sb, double x, double y, string content,
        string? cssClass = null, string? styleFill = null, string? fillAttr = null,
        int? fontSize = null, string? fontWeight = null,
        string anchor = "middle", bool centralBaseline = true, string? coordFormat = "F1")
    {
        var ci = CultureInfo.InvariantCulture;
        sb.Append("<text x=\"").Append(x.ToString(coordFormat, ci))
          .Append("\" y=\"").Append(y.ToString(coordFormat, ci)).Append('"');
        if (cssClass is not null) sb.Append(" class=\"").Append(cssClass).Append('"');
        if (styleFill is not null) sb.Append(" style=\"fill: ").Append(styleFill).Append(";\"");
        if (fillAttr is not null) sb.Append(" fill=\"").Append(fillAttr).Append('"');
        if (fontSize is { } fs) sb.Append(" font-size=\"").Append(fs.ToString(ci)).Append('"');
        if (fontWeight is not null) sb.Append(" font-weight=\"").Append(fontWeight).Append('"');
        sb.Append(" text-anchor=\"").Append(anchor).Append('"');
        if (centralBaseline) sb.Append(" dominant-baseline=\"central\"");
        sb.Append('>').Append(System.Net.WebUtility.HtmlEncode(content)).Append("</text>");
    }
}

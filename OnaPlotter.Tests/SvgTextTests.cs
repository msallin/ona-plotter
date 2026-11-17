using System.Text;
using OnaPlotter.Utilities;

namespace OnaPlotter.Tests;

/// <summary>Pins the exact markup of every SVG <c>&lt;text&gt;</c> shape the
/// dials and charts emit, so one builder serves them all without any
/// attribute drifting.</summary>
public class SvgTextTests
{
    [Test]
    public async Task ClassRouted_RawCoords_Dial()
    {
        // MapHud cardinals: shortest-form coordinates, class colour, central baseline.
        string s = SvgText.Element(0, -18.5, "N", cssClass: "hud-dial-cardinal-major", fontSize: 7, coordFormat: null);

        await Assert.That(s).IsEqualTo(
            "<text x=\"0\" y=\"-18.5\" class=\"hud-dial-cardinal-major\" font-size=\"7\" "
            + "text-anchor=\"middle\" dominant-baseline=\"central\">N</text>");
    }

    [Test]
    public async Task ClassOnly_NoFontSizeNoBaseline_SailSteerValue()
    {
        string s = SvgText.Element(12.34, 5, "12.3", cssClass: "sailsteer-value", centralBaseline: false);

        await Assert.That(s).IsEqualTo(
            "<text x=\"12.3\" y=\"5.0\" class=\"sailsteer-value\" text-anchor=\"middle\">12.3</text>");
    }

    [Test]
    public async Task StyleFill_WithWeight_SailSteerLabel()
    {
        string s = SvgText.Element(1, 2.26, "TWA", styleFill: "var(--sk-text)", fontSize: 14, fontWeight: "700");

        await Assert.That(s).IsEqualTo(
            "<text x=\"1.0\" y=\"2.3\" style=\"fill: var(--sk-text);\" font-size=\"14\" font-weight=\"700\" "
            + "text-anchor=\"middle\" dominant-baseline=\"central\">TWA</text>");
    }

    [Test]
    public async Task ClassWithWeight_Cardinals()
    {
        string s = SvgText.Element(-3, 4, "S", cssClass: "c", fontSize: 10, fontWeight: "bold");

        await Assert.That(s).IsEqualTo(
            "<text x=\"-3.0\" y=\"4.0\" class=\"c\" font-size=\"10\" font-weight=\"bold\" "
            + "text-anchor=\"middle\" dominant-baseline=\"central\">S</text>");
    }

    [Test]
    public async Task FillAttribute_AnchorStart_NoBaseline_SettingsLabel()
    {
        string s = SvgText.Element(10, 20, "0 kn", fillAttr: "#888", fontSize: 9, anchor: "start", centralBaseline: false);

        await Assert.That(s).IsEqualTo(
            "<text x=\"10.0\" y=\"20.0\" fill=\"#888\" font-size=\"9\" text-anchor=\"start\">0 kn</text>");
    }

    [Test]
    public async Task StyleFill_RawCoords_NoWeight_WindRose()
    {
        string s = SvgText.Element(100, 33.5, "E", styleFill: "red", fontSize: 12, coordFormat: null);

        await Assert.That(s).IsEqualTo(
            "<text x=\"100\" y=\"33.5\" style=\"fill: red;\" font-size=\"12\" "
            + "text-anchor=\"middle\" dominant-baseline=\"central\">E</text>");
    }

    [Test]
    public async Task Append_MatchesElement()
    {
        var sb = new StringBuilder("<svg>");
        SvgText.Append(sb, 5, 6.66, "mean", styleFill: "var(--sk-text-muted)", fontSize: 12, anchor: "start");

        await Assert.That(sb.ToString()).IsEqualTo(
            "<svg><text x=\"5.0\" y=\"6.7\" style=\"fill: var(--sk-text-muted);\" font-size=\"12\" "
            + "text-anchor=\"start\" dominant-baseline=\"central\">mean</text>");
    }

    [Test]
    public async Task Content_IsHtmlEncoded()
    {
        string s = SvgText.Element(0, 0, "<a&b>", cssClass: "c");

        await Assert.That(s).Contains(">&lt;a&amp;b&gt;</text>");
    }
}

using System.Text;
using OnaPlotter.Models;

namespace OnaPlotter.Utilities;

/// <summary>
/// Composes a human-readable text summary of a single trip segment
/// for the system share sheet or clipboard. This is what the helm
/// types into a chat to tell a friend "look what we did today" -
/// readable lines, knots and nautical miles, no GeoJSON or other
/// programmer artefacts.
///
/// <para>Pure: takes the segment's already-computed aggregates and
/// a name header, returns the formatted string. Lines whose source
/// aggregate is null (no SOG plumbing, no depth transducer) elide so
/// the helm doesn't ship a wall of "--" placeholders. Numeric
/// formatting reuses <see cref="Format"/> so the share text aligns
/// with the on-screen trip-detail panel down to the decimal places.
/// </para>
/// </summary>
public static class TripSummary
{
    public static string Build(string name, TrackSegment s)
    {
        var sb = new StringBuilder();
        sb.AppendLine(name);
        sb.AppendLine();
        sb.Append("From: ").AppendLine(Format.LatLonDms(s.StartLat, s.StartLon));
        sb.Append("To: ").AppendLine(Format.LatLonDms(s.EndLat, s.EndLon));
        sb.Append("Distance: ").Append(Format.Nm(s.DistanceMetres)).AppendLine(" nm");
        sb.Append("Duration: ").AppendLine(Format.TimeToGo(s.Duration.TotalSeconds));

        // SOG aggregates render as a single composite line so the helm
        // reads "what was the speed picture" at a glance. Each
        // aggregate independently elides on null - a feed that
        // dropped min mid-trip still surfaces avg + max rather than
        // hiding the whole row.
        var sogParts = new List<string>(3);
        if (s.SogAvgMs is not null) sogParts.Add($"avg {Format.Speed(s.SogAvgMs)} kn");
        if (s.SogMinMs is not null) sogParts.Add($"min {Format.Speed(s.SogMinMs)} kn");
        if (s.SogMaxMs is not null) sogParts.Add($"max {Format.Speed(s.SogMaxMs)} kn");
        if (sogParts.Count > 0)
        {
            sb.Append("SOG: ").AppendLine(string.Join(", ", sogParts));
        }

        // Min depth before any other depth aggregate: it's the
        // closest-to-grounding moment of the leg, which is the depth
        // headline a passage recap actually wants.
        if (s.DepthMinM is not null)
        {
            sb.Append("Min depth: ").Append(Format.Depth(s.DepthMinM)).AppendLine(" m");
        }

        // Wind: composite line shape matches SOG. Each aggregate
        // elides on null so a server without TWD derived
        // (signalk-derived-data missing) still shows the apparent
        // wind story, and a boat with only TWS / no AWS still gets
        // the true-wind summary. Helm reads "TWS avg X / max Y kn"
        // at a glance for the passage's wind character. Direction
        // last because it's the smaller-font number on the panel and
        // reads as a tag on the speed pair.
        var twsParts = new List<string>(2);
        if (s.WindSpeedAvgMs is not null) twsParts.Add($"avg {Format.Speed(s.WindSpeedAvgMs)} kn");
        if (s.WindSpeedTrueMaxMs is not null) twsParts.Add($"max {Format.Speed(s.WindSpeedTrueMaxMs)} kn");
        if (twsParts.Count > 0)
        {
            sb.Append("TWS: ").AppendLine(string.Join(", ", twsParts));
        }
        if (s.WindDirectionTrueAvgRad is not null)
        {
            sb.Append("Avg TWD: ").Append(Format.Degrees(s.WindDirectionTrueAvgRad)).AppendLine("°");
        }
        var awsParts = new List<string>(2);
        if (s.WindSpeedApparentAvgMs is not null) awsParts.Add($"avg {Format.Speed(s.WindSpeedApparentAvgMs)} kn");
        if (s.WindSpeedApparentMaxMs is not null) awsParts.Add($"max {Format.Speed(s.WindSpeedApparentMaxMs)} kn");
        if (awsParts.Count > 0)
        {
            sb.Append("AWS: ").AppendLine(string.Join(", ", awsParts));
        }

        // Drop the trailing newline AppendLine appended after the
        // last stat row - some chat apps render an empty line at the
        // bottom of a pasted block.
        return sb.ToString().TrimEnd();
    }
}

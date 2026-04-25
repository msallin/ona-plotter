using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

// DTOs for the Signal K Radar API v3.1
// (https://github.com/SignalK/signalk-server/blob/master/docs/develop/rest-api/radar_api.md)
//
// Split across one file because they form a tightly-coupled graph:
// a Radar has Capabilities which has a Legend and a Controls map of
// ControlDefinitions; each Control has a matching ControlValue; and
// there is one Target shape. Keeping them together saves a dozen
// one-record files + imports.
//
// Wire units follow the spec: distances in metres, angles in radians
// (0..2pi for bearings, -pi..+pi for sectors/zones), speed in m/s,
// time in seconds. We don't auto-convert on deserialise -- consumers
// format for the helm's chosen units.

/// <summary>
/// Top-level radar device info returned by <c>GET /radars</c>. Every
/// device we know how to talk to has one of these. The spoke and
/// control stream URLs are server-provided (may live on a separate
/// host+port when the radar provider runs standalone, e.g. Mayara).
/// </summary>
public sealed class RadarInfo
{
    /// <summary>Server-assigned radar id. Populated from the dict key
    /// in <c>GET /radars</c> responses where the map-of-id pattern is
    /// used; still present as a field in some servers' list shape, so
    /// both are tolerated.</summary>
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("brand")]
    public string? Brand { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("radarIpAddress")]
    public string? RadarIpAddress { get; set; }

    /// <summary>WebSocket URL for binary spoke data. Absolute; may be
    /// on a different host / port than the SK server.</summary>
    [JsonPropertyName("spokeDataUrl")]
    public string? SpokeDataUrl { get; set; }

    /// <summary>WebSocket URL for JSON control updates. Usually the SK
    /// stream <c>/signalk/v1/stream</c>, but spec allows redirection.</summary>
    [JsonPropertyName("streamUrl")]
    public string? StreamUrl { get; set; }

    // Some servers (including the openplotter.local box we prototyped
    // against) embed a snapshot of runtime state directly in the list
    // response instead of requiring a follow-up /capabilities +
    // /controls call. These fields are optional; a conforming client
    // should fall back to the dedicated endpoints when absent.

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("spokesPerRevolution")]
    public int? SpokesPerRevolution { get; set; }

    // Spec wire field is "maxSpokeLen" on the /radars list endpoint
    // and "maxSpokeLength" on /capabilities -- same concept, two
    // different keys depending on endpoint. We normalise to the
    // longer C# name so callers can write a uniform
    //   caps?.MaxSpokeLength ?? info?.MaxSpokeLength
    // fallback; [JsonPropertyName] keeps the wire mapping honest.
    [JsonPropertyName("maxSpokeLen")]
    public int? MaxSpokeLength { get; set; }

    /// <summary>Current configured range in metres. Note the spec's
    /// <c>maxRange</c> lives in Capabilities; this is the live value.</summary>
    [JsonPropertyName("range")]
    public int? Range { get; set; }

    /// <summary>Optional embedded snapshot of the control values, keyed
    /// by control id. Present on some servers in the list response so
    /// the client doesn't need a second round-trip.</summary>
    [JsonPropertyName("controls")]
    public Dictionary<string, ControlValue>? Controls { get; set; }
}

/// <summary>
/// Static radar capabilities from <c>GET /radars/{id}/capabilities</c>.
/// Fetch once per session per radar; the server guarantees these
/// values don't change during radar operation.
/// </summary>
public sealed class RadarCapabilities
{
    [JsonPropertyName("maxRange")]
    public int MaxRange { get; set; }

    [JsonPropertyName("minRange")]
    public int MinRange { get; set; }

    /// <summary>Every discrete range value the radar supports, in
    /// metres. Chartplotters should drive a stepper / dropdown off
    /// this list rather than assume evenly-spaced values.</summary>
    [JsonPropertyName("supportedRanges")]
    public int[] SupportedRanges { get; set; } = [];

    [JsonPropertyName("spokesPerRevolution")]
    public int SpokesPerRevolution { get; set; }

    [JsonPropertyName("maxSpokeLength")]
    public int MaxSpokeLength { get; set; }

    /// <summary>Number of distinct pixel intensity values in spoke
    /// data. The legend's <c>pixels</c> array length matches this.</summary>
    [JsonPropertyName("pixelValues")]
    public int PixelValues { get; set; }

    [JsonPropertyName("hasDoppler")]
    public bool HasDoppler { get; set; }

    [JsonPropertyName("hasDualRadar")]
    public bool HasDualRadar { get; set; }

    [JsonPropertyName("hasDualRange")]
    public bool HasDualRange { get; set; }

    /// <summary>True for radars that emit fewer spokes per revolution
    /// than <see cref="SpokesPerRevolution"/> implies (Furuno). Clients
    /// should wedge-paint from the received angle to the previously-
    /// received angle rather than assume adjacency.</summary>
    [JsonPropertyName("hasSparseSpokes")]
    public bool HasSparseSpokes { get; set; }

    [JsonPropertyName("noTransmitSectors")]
    public int NoTransmitSectors { get; set; }

    [JsonPropertyName("controls")]
    public Dictionary<string, ControlDefinition> Controls { get; set; } = new();

    [JsonPropertyName("legend")]
    public RadarLegend? Legend { get; set; }
}

/// <summary>
/// Pixel-byte-to-semantic-color lookup table shipped in
/// <see cref="RadarCapabilities.Legend"/>. Each byte in a spoke's
/// data indexes into <see cref="Pixels"/>; the <see cref="PixelColors"/>
/// field says how many of those leading entries are normal returns
/// (0 = no echo, increasing = stronger) before the semantic byte
/// values (target border, doppler, history trails) take over.
/// </summary>
public sealed class RadarLegend
{
    /// <summary>Byte threshold for "low return" smoothing.</summary>
    [JsonPropertyName("lowReturn")]
    public int LowReturn { get; set; }

    [JsonPropertyName("mediumReturn")]
    public int MediumReturn { get; set; }

    [JsonPropertyName("strongReturn")]
    public int StrongReturn { get; set; }

    /// <summary>Byte value that marks the edge of a tracked ARPA
    /// target in the spoke stream. Null when the provider doesn't
    /// annotate targets on the sweep itself (ARPA is still published
    /// separately via the targets API).</summary>
    [JsonPropertyName("targetBorder")]
    public int? TargetBorder { get; set; }

    [JsonPropertyName("dopplerApproaching")]
    public int? DopplerApproaching { get; set; }

    [JsonPropertyName("dopplerReceding")]
    public int? DopplerReceding { get; set; }

    /// <summary>First byte value in the history-trail range. Null when
    /// the provider doesn't emit trails.</summary>
    [JsonPropertyName("historyStart")]
    public int? HistoryStart { get; set; }

    /// <summary>Count of pure-return colours (byte values 0 to
    /// <c>PixelColors - 1</c>). Values at / above this but below
    /// <see cref="HistoryStart"/> are semantic markers.</summary>
    [JsonPropertyName("pixelColors")]
    public int PixelColors { get; set; }

    /// <summary>Full per-byte-value table. A byte of N in a spoke
    /// means "paint pixel with Pixels[N].Color if it's not transparent".
    /// </summary>
    [JsonPropertyName("pixels")]
    public LegendPixel[] Pixels { get; set; } = [];
}

/// <summary>
/// Single entry of <see cref="RadarLegend.Pixels"/>. The color is
/// sent either as a hex string (<c>"#RRGGBBAA"</c>) OR as an object
/// <c>{r,g,b,a}</c>, depending on server version; the custom converter
/// normalises both into an RGBA tuple.
/// </summary>
public sealed class LegendPixel
{
    /// <summary>One of <c>"normal"</c>, <c>"dopplerApproaching"</c>,
    /// <c>"dopplerReceding"</c>, <c>"history"</c>, or
    /// <c>"targetBorder"</c>. Consumers can recolour based on semantics
    /// rather than the server-chosen palette.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("color")]
    [JsonConverter(typeof(LegendColorConverter))]
    public RadarColor Color { get; set; }
}

/// <summary>RGBA pixel, 0-255 per channel. A zero alpha means the
/// byte should render as transparent (no echo, hidden).</summary>
public readonly record struct RadarColor(byte R, byte G, byte B, byte A)
{
    public string ToCssRgba() =>
        A == 255
            ? $"rgb({R},{G},{B})"
            : $"rgba({R},{G},{B},{(A / 255.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)})";
}

/// <summary>
/// Accepts both <c>"#RRGGBBAA"</c> strings and
/// <c>{r,g,b,a}</c> objects for the legend pixel colour. The spec
/// documents the string form but the reference implementation emits
/// the object form; tolerate both for portability.
/// </summary>
internal sealed class LegendColorConverter : JsonConverter<RadarColor>
{
    public override RadarColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return ParseHex(reader.GetString());

        // Defensive: if the value is something we don't recognise (a
        // packed-int colour, an [r,g,b] tuple, null, etc.), skip the
        // entire value so the parent reader stays in sync. Without
        // this a stray array would leave the reader at StartArray and
        // the LegendPixel deserialiser would either throw or silently
        // mis-parse subsequent properties. Transparent black is the
        // right "I don't know what colour you meant" rendering.
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            if (reader.TokenType == JsonTokenType.StartArray) reader.Skip();
            return default;
        }

        byte r = 0, g = 0, b = 0, a = 255;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = reader.GetString();
            reader.Read();
            // If the value isn't a primitive number, advance the
            // reader PAST the whole value (object / array / etc.)
            // rather than swallowing only the first token. The
            // earlier code left the reader mid-value and the outer
            // loop's EndObject check tripped on the nested object's
            // closing brace, dropping every subsequent property.
            byte v;
            if (reader.TokenType == JsonTokenType.Number)
            {
                v = (byte)Math.Clamp(reader.GetInt32(), 0, 255);
            }
            else
            {
                v = 0;
                reader.Skip();
            }
            switch (name)
            {
                case "r": r = v; break;
                case "g": g = v; break;
                case "b": b = v; break;
                case "a": a = v; break;
            }
        }
        return new RadarColor(r, g, b, a);
    }

    public override void Write(Utf8JsonWriter writer, RadarColor value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"#{value.R:X2}{value.G:X2}{value.B:X2}{value.A:X2}");
    }

    // Parses "#RRGGBB" or "#RRGGBBAA"; empty / malformed -> transparent black.
    private static RadarColor ParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return default;
        if (hex.Length == 7 &&
            TryHex(hex, 1, out byte r) && TryHex(hex, 3, out byte g) && TryHex(hex, 5, out byte b))
            return new RadarColor(r, g, b, 255);
        if (hex.Length == 9 &&
            TryHex(hex, 1, out byte r2) && TryHex(hex, 3, out byte g2) &&
            TryHex(hex, 5, out byte b2) && TryHex(hex, 7, out byte a2))
            return new RadarColor(r2, g2, b2, a2);
        return default;
    }

    private static bool TryHex(string s, int offset, out byte value) =>
        byte.TryParse(s.AsSpan(offset, 2), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// Schema for a single radar control. Clients render UI widgets off
/// <see cref="DataType"/> + the min/max/step/descriptions fields --
/// no brand-specific rendering code required.
/// </summary>
public sealed class ControlDefinition
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>One of <c>"base"</c>, <c>"targets"</c>, <c>"guardZones"</c>,
    /// <c>"trails"</c>, <c>"advanced"</c>, <c>"installation"</c>,
    /// <c>"info"</c>. Controls the section / tab the UI renders in.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "base";

    /// <summary>One of <c>"number"</c>, <c>"enum"</c>, <c>"string"</c>,
    /// <c>"button"</c>, <c>"sector"</c>, <c>"zone"</c>, <c>"rect"</c>.
    /// Drives the widget choice on the client.</summary>
    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = "number";

    [JsonPropertyName("isReadOnly")]
    public bool IsReadOnly { get; set; }

    [JsonPropertyName("hasEnabled")]
    public bool HasEnabled { get; set; }

    [JsonPropertyName("hasAuto")]
    public bool HasAuto { get; set; }

    [JsonPropertyName("hasAutoAdjustable")]
    public bool HasAutoAdjustable { get; set; }

    [JsonPropertyName("minValue")]
    public double? MinValue { get; set; }

    [JsonPropertyName("maxValue")]
    public double? MaxValue { get; set; }

    [JsonPropertyName("stepValue")]
    public double? StepValue { get; set; }

    [JsonPropertyName("maxDistance")]
    public double? MaxDistance { get; set; }

    /// <summary>SI unit suffix: <c>"m"</c> / <c>"m/s"</c> /
    /// <c>"rad"</c> / <c>"rad/s"</c> / <c>"s"</c>. UI can display-
    /// convert to nm / kn / deg / min / h.</summary>
    [JsonPropertyName("units")]
    public string? Units { get; set; }

    /// <summary>Enum-only: integer-keyed value -> human label map
    /// for rendering a dropdown / button group.</summary>
    [JsonPropertyName("descriptions")]
    public Dictionary<string, string>? Descriptions { get; set; }

    /// <summary>Enum-only: subset of <see cref="Descriptions"/> keys
    /// the client is allowed to set. Values outside this list exist
    /// (e.g. "Preparing" on power) but the server rejects PUTs for
    /// them.</summary>
    [JsonPropertyName("validValues")]
    public int[]? ValidValues { get; set; }
}

/// <summary>
/// Live control value. Field set depends on the control's DataType;
/// anything irrelevant is left null. <c>value</c> is the numeric (or
/// string) primary value for everything except <c>rect</c>, which
/// uses the x1/y1/x2/y2/width tuple instead.
/// </summary>
public sealed class ControlValue
{
    /// <summary>Primary value. Numeric for most controls, string for
    /// text controls. Left as <see cref="JsonElement"/> so we don't
    /// pick a C# primitive before the caller knows the DataType.
    /// <para>Consumers: <b>do not</b> bind <c>@Value</c> directly from
    /// a Razor component; read through the typed helpers
    /// <see cref="NumericValue"/> / <see cref="StringValue"/> instead.
    /// A JsonElement bound via <c>@bind</c> will stringify the entire
    /// node (e.g. <c>{"value":42}</c>) into the input.</para></summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonPropertyName("auto")]
    public bool? Auto { get; set; }

    [JsonPropertyName("autoValue")]
    public double? AutoValue { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    /// <summary>End of a sector / zone, radians. Start is in
    /// <see cref="Value"/>.</summary>
    [JsonPropertyName("endValue")]
    public double? EndValue { get; set; }

    [JsonPropertyName("startDistance")]
    public double? StartDistance { get; set; }

    [JsonPropertyName("endDistance")]
    public double? EndDistance { get; set; }

    [JsonPropertyName("x1")]
    public double? X1 { get; set; }

    [JsonPropertyName("y1")]
    public double? Y1 { get; set; }

    [JsonPropertyName("x2")]
    public double? X2 { get; set; }

    [JsonPropertyName("y2")]
    public double? Y2 { get; set; }

    [JsonPropertyName("width")]
    public double? Width { get; set; }

    /// <summary>Server hint: when present and false, the control is
    /// currently read-only because of mode (e.g. a gain slider while
    /// in auto mode). UI should disable.</summary>
    [JsonPropertyName("allowed")]
    public bool? Allowed { get; set; }

    /// <summary>Echoed from the server after a failed PUT; renders
    /// as a toast / inline warning.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    /// <summary>Numeric-valued controls: the primary value as a
    /// double, or null when the control is a string / button / the
    /// value is missing.</summary>
    public double? NumericValue
    {
        get
        {
            if (Value is not JsonElement el) return null;
            if (el.ValueKind == JsonValueKind.Number) return el.GetDouble();
            return null;
        }
    }

    public string? StringValue
    {
        get
        {
            if (Value is not JsonElement el) return null;
            if (el.ValueKind == JsonValueKind.String) return el.GetString();
            return null;
        }
    }
}

/// <summary>
/// Single ARPA/MARPA target from <c>GET /radars/{id}/targets</c> or
/// the delta stream. Bearing/distance are always present; lat/lon are
/// filled in when the radar knows own-ship position. Motion + danger
/// substructures can be absent on freshly-acquired or stationary
/// targets.
/// </summary>
public sealed class RadarArpaTarget
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>One of <c>"acquiring"</c>, <c>"tracking"</c>,
    /// <c>"lost"</c>. A target transitions acquiring -> tracking; the
    /// lost state is terminal and precedes deletion.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("position")]
    public RadarTargetPosition? Position { get; set; }

    [JsonPropertyName("motion")]
    public RadarTargetMotion? Motion { get; set; }

    [JsonPropertyName("danger")]
    public RadarTargetDanger? Danger { get; set; }

    [JsonPropertyName("acquisition")]
    public string? Acquisition { get; set; }

    [JsonPropertyName("sourceZone")]
    public int? SourceZone { get; set; }

    [JsonPropertyName("firstSeen")]
    public string? FirstSeen { get; set; }

    [JsonPropertyName("lastSeen")]
    public string? LastSeen { get; set; }
}

public sealed class RadarTargetPosition
{
    /// <summary>Radians, 0..2pi, clockwise from true north.</summary>
    [JsonPropertyName("bearing")]
    public double Bearing { get; set; }

    /// <summary>Metres from own-ship / radar antenna.</summary>
    [JsonPropertyName("distance")]
    public double Distance { get; set; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
}

public sealed class RadarTargetMotion
{
    /// <summary>COG in radians, 0..2pi.</summary>
    [JsonPropertyName("course")]
    public double Course { get; set; }

    /// <summary>SOG in m/s. Zero with <see cref="Course"/>=0 means
    /// confirmed stationary (anchored / buoy).</summary>
    [JsonPropertyName("speed")]
    public double Speed { get; set; }
}

public sealed class RadarTargetDanger
{
    /// <summary>Closest point of approach in metres.</summary>
    [JsonPropertyName("cpa")]
    public double Cpa { get; set; }

    /// <summary>Seconds to CPA.</summary>
    [JsonPropertyName("tcpa")]
    public double Tcpa { get; set; }
}

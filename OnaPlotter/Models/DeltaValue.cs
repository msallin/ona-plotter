using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnaPlotter.Utilities;

namespace OnaPlotter.Models;

/// <summary>Shape of a <see cref="DeltaValue"/>.</summary>
public enum DeltaValueKind
{
    /// <summary>JSON <c>null</c>, or no <c>value</c> property at all.</summary>
    Null,
    Number,
    Bool,
    String,
    /// <summary>A flat <c>{latitude, longitude[, altitude]}</c> object,
    /// read without materialising a JSON document.</summary>
    Position,
    /// <summary>Any other object or array, kept as a
    /// <see cref="JsonElement"/> for the handlers that walk it
    /// (notifications, resources, identity bundles, radar targets).</summary>
    Element,
}

/// <summary>
/// The <c>value</c> of one SignalK delta entry, as a small tagged union.
/// A live feed carries a few hundred values per second and almost all
/// of them are numbers, strings, booleans or positions; reading those
/// straight off the <see cref="Utf8JsonReader"/> keeps them free of the
/// per-value <see cref="JsonDocument"/> that an <c>object</c>-typed
/// property would allocate. Only genuinely structured values
/// (notification objects, resource documents, identity bundles) pay for
/// a <see cref="JsonElement"/>.
/// <para>Implicit conversions from <c>double</c>, <c>bool</c>,
/// <c>string</c> and <see cref="JsonElement"/> keep call sites that
/// feed REST-seeded or hand-built values terse; a <see cref="JsonElement"/>
/// is classified exactly like the wire converter would.</para>
/// </summary>
[JsonConverter(typeof(DeltaValueConverter))]
public readonly struct DeltaValue : IEquatable<DeltaValue>
{
    // Number: _a. Position: _a = latitude, _b = longitude. Bool: _a = 0/1.
    private readonly double _a;
    private readonly double _b;
    private readonly string? _string;
    private readonly JsonElement _element;

    public DeltaValueKind Kind { get; }

    private DeltaValue(DeltaValueKind kind, double a = 0, double b = 0,
        string? str = null, JsonElement element = default)
    {
        Kind = kind;
        _a = a;
        _b = b;
        _string = str;
        _element = element;
    }

    public static DeltaValue Null => default;

    public static DeltaValue FromNumber(double value) => new(DeltaValueKind.Number, value);

    public static DeltaValue FromBool(bool value) => new(DeltaValueKind.Bool, value ? 1 : 0);

    /// <summary>A null string is <see cref="DeltaValueKind.Null"/>.</summary>
    public static DeltaValue FromString(string? value) =>
        value is null ? default : new(DeltaValueKind.String, str: value);

    public static DeltaValue FromPosition(double latitude, double longitude) =>
        new(DeltaValueKind.Position, latitude, longitude);

    /// <summary>Classifies an already-parsed element the same way
    /// <see cref="DeltaValueConverter"/> classifies wire bytes. The
    /// element is kept by reference for <see cref="DeltaValueKind.Element"/>,
    /// so it must outlive this value.</summary>
    public static DeltaValue FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDouble(out var d) && double.IsFinite(d)
                    ? FromNumber(d)
                    : new(DeltaValueKind.Element, element: element);
            case JsonValueKind.True:
                return FromBool(true);
            case JsonValueKind.False:
                return FromBool(false);
            case JsonValueKind.String:
                return FromString(element.GetString());
            case JsonValueKind.Object:
                return IsFlatNumericObject(element) && element.TryGetLatLon(out var lat, out var lon)
                    ? FromPosition(lat, lon)
                    : new(DeltaValueKind.Element, element: element);
            case JsonValueKind.Array:
                return new(DeltaValueKind.Element, element: element);
            default:
                return default;
        }
    }

    public static implicit operator DeltaValue(double? value) =>
        value is double d ? FromNumber(d) : default;
    public static implicit operator DeltaValue(bool value) => FromBool(value);
    public static implicit operator DeltaValue(string? value) => FromString(value);
    public static implicit operator DeltaValue(JsonElement element) => FromElement(element);

    public bool IsNull => Kind == DeltaValueKind.Null;

    /// <summary>True for a JSON object, whether the converter kept it as
    /// an element or reduced a flat <c>{latitude, longitude}</c> to a
    /// position.</summary>
    public bool IsObject =>
        Kind == DeltaValueKind.Position
        || (Kind == DeltaValueKind.Element && _element.ValueKind == JsonValueKind.Object);

    public bool TryGetNumber(out double value)
    {
        value = Kind == DeltaValueKind.Number ? _a : 0;
        return Kind == DeltaValueKind.Number;
    }

    public bool TryGetBool(out bool value)
    {
        value = Kind == DeltaValueKind.Bool && _a != 0;
        return Kind == DeltaValueKind.Bool;
    }

    public bool TryGetString(out string value)
    {
        value = Kind == DeltaValueKind.String ? _string! : "";
        return Kind == DeltaValueKind.String;
    }

    /// <summary>Position from either a <see cref="DeltaValueKind.Position"/>
    /// value or a structured element in one of the shapes
    /// <see cref="JsonElementExtensions.TryGetLatLon"/> accepts (e.g. the
    /// <c>{type, position: {...}}</c> course next-point object).</summary>
    public bool TryGetLatLon(out double latitude, out double longitude)
    {
        if (Kind == DeltaValueKind.Position)
        {
            latitude = _a;
            longitude = _b;
            return true;
        }
        if (Kind == DeltaValueKind.Element)
            return _element.TryGetLatLon(out latitude, out longitude);
        latitude = 0;
        longitude = 0;
        return false;
    }

    /// <summary>The structured payload of an
    /// <see cref="DeltaValueKind.Element"/> value (object, array, or a
    /// number outside double range).</summary>
    public bool TryGetElement(out JsonElement element)
    {
        element = Kind == DeltaValueKind.Element ? _element : default;
        return Kind == DeltaValueKind.Element;
    }

    /// <summary>The value as a <see cref="JsonElement"/>, for consumers
    /// that hand the raw JSON on (resource documents). Free for
    /// <see cref="DeltaValueKind.Element"/>; parses a small document
    /// for every other kind.</summary>
    public JsonElement ToJsonElement()
    {
        if (Kind == DeltaValueKind.Element) return _element;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(32);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            DeltaValueConverter.WriteValue(writer, this);
        }
        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }

    /// <summary>Text form, matching <see cref="JsonElement.ToString"/>
    /// for the same JSON: numbers in round-trip invariant form, booleans
    /// as <c>True</c>/<c>False</c>, objects as raw JSON, null as empty.</summary>
    public override string ToString() => Kind switch
    {
        DeltaValueKind.Null => "",
        DeltaValueKind.Number => _a.ToString("R", CultureInfo.InvariantCulture),
        DeltaValueKind.Bool => _a != 0 ? bool.TrueString : bool.FalseString,
        DeltaValueKind.String => _string!,
        DeltaValueKind.Position => string.Create(CultureInfo.InvariantCulture,
            $"{{\"latitude\":{_a:R},\"longitude\":{_b:R}}}"),
        _ => _element.GetRawText(),
    };

    public bool Equals(DeltaValue other) => Kind == other.Kind && Kind switch
    {
        DeltaValueKind.Null => true,
        DeltaValueKind.Number or DeltaValueKind.Bool => _a.Equals(other._a),
        DeltaValueKind.Position => _a.Equals(other._a) && _b.Equals(other._b),
        DeltaValueKind.String => string.Equals(_string, other._string, StringComparison.Ordinal),
        _ => _element.GetRawText() == other._element.GetRawText(),
    };

    public override bool Equals(object? obj) => obj is DeltaValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Kind, _a, _b, _string);

    public static bool operator ==(DeltaValue left, DeltaValue right) => left.Equals(right);
    public static bool operator !=(DeltaValue left, DeltaValue right) => !left.Equals(right);

    /// <summary>True when every property is a number or null, i.e. the
    /// object could be a position and nothing structured is lost by
    /// reducing it to one.</summary>
    private static bool IsFlatNumericObject(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Reads a SignalK delta <c>value</c> straight into a
/// <see cref="DeltaValue"/>. Examples of what lands where:
/// <code>
/// 3.52                                        -> Number 3.52
/// "motoring"                                  -> String
/// true                                        -> Bool
/// null                                        -> Null
/// {"latitude":59.91,"longitude":10.75}        -> Position (no JsonDocument)
/// {"latitude":59.9,"longitude":10.7,"altitude":null} -> Position
/// {"state":"alarm","message":"MOB"}           -> Element
/// {"type":"RoutePoint","position":{...}}      -> Element (nested position
///                                                stays readable through
///                                                TryGetLatLon)
/// [1,2]                                       -> Element
/// </code>
/// A position candidate is scanned on a copy of the reader; anything
/// other than a flat object of numbers / nulls with finite latitude and
/// longitude rewinds and is parsed as an element.
/// </summary>
public sealed class DeltaValueConverter : JsonConverter<DeltaValue>
{
    public override bool HandleNull => true;

    public override DeltaValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return DeltaValue.Null;
            case JsonTokenType.Number:
                return reader.TryGetDouble(out var d) && double.IsFinite(d)
                    ? DeltaValue.FromNumber(d)
                    : DeltaValue.FromElement(JsonElement.ParseValue(ref reader));
            case JsonTokenType.True:
                return DeltaValue.FromBool(true);
            case JsonTokenType.False:
                return DeltaValue.FromBool(false);
            case JsonTokenType.String:
                return DeltaValue.FromString(reader.GetString());
            case JsonTokenType.StartObject:
                {
                    var probe = reader;
                    if (TryReadFlatPosition(ref probe, out var lat, out var lon))
                    {
                        reader = probe;
                        return DeltaValue.FromPosition(lat, lon);
                    }
                    return DeltaValue.FromElement(JsonElement.ParseValue(ref reader));
                }
            default:
                return DeltaValue.FromElement(JsonElement.ParseValue(ref reader));
        }
    }

    /// <summary>Scans an object from its StartObject token to its
    /// EndObject. Succeeds only for a flat object whose values are all
    /// numbers or nulls and which carries finite latitude and longitude.
    /// On success the reader sits on the EndObject token.</summary>
    private static bool TryReadFlatPosition(ref Utf8JsonReader reader, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        bool hasLat = false, hasLon = false;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return hasLat && hasLon;
            if (reader.TokenType != JsonTokenType.PropertyName)
                return false;
            bool isLat = reader.ValueTextEquals("latitude"u8);
            bool isLon = !isLat && reader.ValueTextEquals("longitude"u8);
            if (!reader.Read()) return false;
            if (reader.TokenType == JsonTokenType.Null)
            {
                if (isLat || isLon) return false;
                continue;
            }
            if (reader.TokenType != JsonTokenType.Number) return false;
            if (!isLat && !isLon) continue;
            if (!reader.TryGetDouble(out var v) || !double.IsFinite(v)) return false;
            if (isLat) { latitude = v; hasLat = true; }
            else { longitude = v; hasLon = true; }
        }
        return false;
    }

    public override void Write(Utf8JsonWriter writer, DeltaValue value, JsonSerializerOptions options) =>
        WriteValue(writer, value);

    internal static void WriteValue(Utf8JsonWriter writer, DeltaValue value)
    {
        switch (value.Kind)
        {
            case DeltaValueKind.Number:
                value.TryGetNumber(out var d);
                writer.WriteNumberValue(d);
                break;
            case DeltaValueKind.Bool:
                value.TryGetBool(out var b);
                writer.WriteBooleanValue(b);
                break;
            case DeltaValueKind.String:
                value.TryGetString(out var s);
                writer.WriteStringValue(s);
                break;
            case DeltaValueKind.Position:
                value.TryGetLatLon(out var lat, out var lon);
                writer.WriteStartObject();
                writer.WriteNumber("latitude", lat);
                writer.WriteNumber("longitude", lon);
                writer.WriteEndObject();
                break;
            case DeltaValueKind.Element:
                value.TryGetElement(out var el);
                el.WriteTo(writer);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}

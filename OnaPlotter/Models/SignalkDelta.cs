// SignalK delta protocol DTOs.
// A delta message looks like:
// {
//   "context": "vessels.urn:mrn:imo:mmsi:...",
//   "updates": [{
//     "source": { "label": "...", "type": "..." },
//     "timestamp": "2026-04-06T12:00:00Z",
//     "values": [{ "path": "navigation.speedOverGround", "value": 3.5 }]
//   }]
// }

using System.Text.Json.Serialization;

namespace OnaPlotter.Models;

/// <summary>
/// DTO for a SignalK delta protocol message containing one or more value updates
/// for a specific vessel context.
/// </summary>
public sealed class SignalkDelta
{
    [JsonPropertyName("context")]
    public string? Context { get; set; }

    [JsonPropertyName("updates")]
    public List<SignalkUpdate>? Updates { get; set; }
}

/// <summary>A single timestamped update containing one or more path/value pairs.</summary>
public sealed class SignalkUpdate
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("values")]
    public List<SignalkValue>? Values { get; set; }
}

/// <summary>A single SignalK path/value pair (e.g. "navigation.speedOverGround" = 3.5).</summary>
public sealed class SignalkValue
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("value")]
    public object? Value { get; set; }
}

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

/// <summary>A single SignalK path/value pair (e.g. "navigation.speedOverGround" = 3.5).
/// <see cref="Value"/> is <see cref="DeltaValue.Null"/> both for an explicit
/// JSON null and for a missing property; SignalK treats the two alike.</summary>
public sealed class SignalkValue
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("value")]
    public DeltaValue Value { get; set; }
}

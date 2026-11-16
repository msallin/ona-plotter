using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// What services need from the live SignalK connection: own-boat state,
/// connection status and the change / resource notifications. Lets
/// services be tested against a fake instead of a real WebSocket
/// client. The subscription debugging surface (RawStream extra paths)
/// stays on <see cref="SignalkClient"/>.
/// </summary>
public interface ISignalkClient
{
    /// <summary>Own-boat navigation state, updated in place by the delta stream.</summary>
    NavigationData Data { get; }

    bool IsConnected { get; }

    /// <summary>Connected, but no message for more than 5 seconds.</summary>
    bool IsDataStale { get; }

    /// <summary>MMSI from the server's self URN, when known.</summary>
    string? OwnMmsi { get; }

    /// <summary>Own-boat VHF callsign, when the server publishes it.</summary>
    string? OwnCallsign { get; }

    /// <summary>Own-boat navigation data changed. Raised on the receive
    /// loop; subscribers marshal to their own context.</summary>
    event Action? OnDataChanged;

    /// <summary>Raised on both connection edges; read
    /// <see cref="IsConnected"/> for the new state.</summary>
    event Action? OnConnectionChanged;

    /// <summary>(type, id, value) for every <c>resources.&lt;type&gt;.&lt;id&gt;</c>
    /// delta; value is a Null-kind element on delete.</summary>
    event Action<string, string, JsonElement>? OnResourceDelta;

    /// <summary>Skip the current reconnect backoff and retry now.</summary>
    void ReconnectNow();
}

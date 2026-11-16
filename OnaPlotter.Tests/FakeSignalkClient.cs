using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services;

namespace OnaPlotter.Tests;

/// <summary>
/// Hand-driven <see cref="ISignalkClient"/>: tests flip
/// <see cref="IsConnected"/> through <see cref="Connect"/> /
/// <see cref="Disconnect"/> (which raise the connection edge like the
/// receive loop does) and push data / resource events explicitly.
/// </summary>
internal sealed class FakeSignalkClient : ISignalkClient
{
    public NavigationData Data { get; } = new();
    public bool IsConnected { get; private set; }
    public bool IsDataStale { get; set; }
    public string? OwnMmsi { get; set; }
    public string? OwnCallsign { get; set; }
    public int ReconnectNowCalls { get; private set; }

    public event Action? OnDataChanged;
    public event Action? OnConnectionChanged;
    public event Action<string, string, JsonElement>? OnResourceDelta;

    public void Connect()
    {
        IsConnected = true;
        OnConnectionChanged?.Invoke();
    }

    public void Disconnect()
    {
        IsConnected = false;
        OnConnectionChanged?.Invoke();
    }

    public void RaiseDataChanged() => OnDataChanged?.Invoke();

    public void RaiseResourceDelta(string type, string id, JsonElement value) =>
        OnResourceDelta?.Invoke(type, id, value);

    public void ReconnectNow() => ReconnectNowCalls++;
}

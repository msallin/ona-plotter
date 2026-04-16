// Service that maintains a websocket connection to a SignalK server,
// receives delta messages, and updates shared NavigationData state.

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OnaPlotter.Models;

namespace OnaPlotter.Services;

/// <summary>
/// Maintains a persistent WebSocket connection to a SignalK server, receives
/// delta messages, and distributes parsed navigation/AIS data to the shared
/// <see cref="NavigationData"/>, <see cref="TrackBuffer"/>, and <see cref="AisStore"/>.
/// Automatically reconnects with exponential backoff on connection loss.
/// </summary>
public sealed class SignalkClient : IAsyncDisposable
{
    private readonly NavigationData _data;
    private readonly TrackBuffer _track;
    private readonly AisStore _ais;
    private readonly Uri _wsUri;
    private string _selfContext;
    private readonly ILogger<SignalkClient> _logger;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Extra paths subscribed to by the RawStream page (re-applied on reconnect).
    private readonly HashSet<string> _extraPaths = [];

    private static readonly string[] SelfPaths =
    [
        "navigation.position",
        "navigation.speedOverGround",
        "navigation.courseOverGroundTrue",
        "navigation.headingTrue",
        "environment.depth.belowTransducer",
        "environment.wind.angleApparent",
        "environment.wind.speedApparent",
        "environment.wind.angleTrueWater",
        "environment.wind.speedTrue",
        "environment.wind.directionTrue",
        // Anchor alarm plugin (sbender9/signalk-anchoralarm-plugin)
        "navigation.anchor.position",
        "navigation.anchor.maxRadius",
        "navigation.anchor.currentRadius",
        // Active course / route info
        "navigation.courseGreatCircle.activeRoute.href",
        "navigation.courseGreatCircle.activeRoute.name",
        "navigation.courseGreatCircle.nextPoint.position",
        "navigation.courseGreatCircle.nextPoint.distance",
        "navigation.courseGreatCircle.nextPoint.bearingTrue",
        "navigation.courseGreatCircle.nextPoint.timeToGo",
        "navigation.courseGreatCircle.nextPoint.velocityMadeGood",
        "navigation.courseRhumbline.nextPoint.position",
        "navigation.courseRhumbline.nextPoint.distance",
        "navigation.courseRhumbline.nextPoint.bearingTrue",
        "navigation.courseRhumbline.nextPoint.timeToGo",
        "navigation.courseRhumbline.nextPoint.velocityMadeGood",
        // Cross-track error and previous waypoint
        "navigation.courseGreatCircle.crossTrackError",
        "navigation.courseRhumbline.crossTrackError",
        "navigation.courseGreatCircle.previousPoint.position",
        "navigation.courseRhumbline.previousPoint.position"
    ];

    private static readonly string[] AisPaths =
    [
        "navigation.position",
        "navigation.speedOverGround",
        "navigation.courseOverGroundTrue",
        "navigation.headingTrue",
        "name",
        "mmsi",
        "communication.callsignVhf",
        "design.aisShipType"
    ];

    /// <summary>
    /// Raised when navigation data changes. Subscribers must handle thread-safety themselves
    /// (e.g. InvokeAsync(StateHasChanged) in Blazor components).
    /// </summary>
    public event Action? OnDataChanged;

    /// <summary>
    /// Raised for every raw JSON message received from the websocket.
    /// </summary>
    public event Action<string>? OnRawMessage;

    /// <summary>
    /// Raised when connection status changes.
    /// </summary>
    public event Action? OnConnectionChanged;

    public NavigationData Data => _data;
    public bool IsConnected { get; private set; }

    /// <summary>
    /// UTC ticks of the last websocket message received (any type, including heartbeats).
    /// Uses <see cref="Interlocked"/> for thread-safe cross-thread reads.
    /// </summary>
    private long _lastMessageTicks;

    /// <summary>
    /// Returns true when the websocket is open but no message has arrived
    /// for more than 5 seconds.
    /// </summary>
    public bool IsDataStale => IsConnected
        && (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastMessageTicks)) > 5 * TimeSpan.TicksPerSecond;

    public SignalkClient(IConfiguration configuration, ILogger<SignalkClient> logger, TrackBuffer track, AisStore ais)
    {
        _logger = logger;
        _data = new NavigationData();
        _track = track;
        _ais = ais;

        string serverUrl = configuration["SignalK:ServerUrl"]
            ?? throw new InvalidOperationException("SignalK:ServerUrl is not configured.");

        // Convert http(s) URL to ws(s) and append the stream path.
        // subscribe=none: we send explicit subscription messages after connecting.
        var baseUri = new Uri(serverUrl);
        string wsScheme = baseUri.Scheme == "https" ? "wss" : "ws";
        _wsUri = new Uri($"{wsScheme}://{baseUri.Host}:{baseUri.Port}/signalk/v1/stream?subscribe=none");
        _selfContext = "";
    }

    /// <summary>
    /// Starts the websocket receive loop. Called from Program.cs after host build.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = ReceiveLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        int backoffMs = 1000;
        const int maxBackoffMs = 30_000;

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                _logger.LogInformation("Connecting to SignalK at {Uri}", _wsUri);
                await ws.ConnectAsync(_wsUri, ct);
                _ws = ws;
                _logger.LogInformation("Connected to SignalK");
                IsConnected = true;
                OnConnectionChanged?.Invoke();
                backoffMs = 1000;

                // Subscribe to the paths the app needs.
                await SendSubscriptionAsync("vessels.self", SelfPaths);
                await SendSubscriptionAsync("vessels.*", AisPaths);
                if (_extraPaths.Count > 0)
                    await SendSubscriptionAsync("vessels.self", _extraPaths);

                var buffer = new byte[8192];
                var messageBuffer = new StringBuilder();

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    if (result.EndOfMessage)
                    {
                        ProcessMessage(messageBuffer.ToString());
                        messageBuffer.Clear();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SignalK connection lost, reconnecting in {BackoffMs}ms", backoffMs);
            }

            _ws = null;
            IsConnected = false;
            OnConnectionChanged?.Invoke();

            try
            {
                await Task.Delay(backoffMs, ct);
                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ProcessMessage(string json)
    {
        Interlocked.Exchange(ref _lastMessageTicks, DateTime.UtcNow.Ticks);
        OnRawMessage?.Invoke(json);

        try
        {
            // Single parse: deserialize to SignalkDelta, then check for hello message.
            var delta = JsonSerializer.Deserialize<SignalkDelta>(json);

            // The hello message has no updates but contains "self" in the raw JSON.
            // SignalkDelta ignores unknown properties, so check if updates are present.
            if (delta?.Updates is null)
            {
                // Could be the hello message with "self" identifier.
                if (json.Contains("\"self\"", StringComparison.Ordinal))
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("self", out var selfProp))
                    {
                        _selfContext = selfProp.GetString() ?? "";
                        _logger.LogInformation("Self context: {Self}", _selfContext);
                    }
                }
                return;
            }

            bool isSelf = string.IsNullOrEmpty(delta.Context)
                || delta.Context == "vessels.self"
                || delta.Context == _selfContext;

            if (isSelf)
                ProcessSelfDelta(delta);
            else
                ProcessAisDelta(delta);
        }
        catch (JsonException)
        {
            // Ignore malformed messages.
        }
    }

    private void ProcessSelfDelta(SignalkDelta delta)
    {
        bool changed = false;

        foreach (var update in delta.Updates!)
        {
            _data.SetTimestamp(update.Timestamp);

            if (update.Values is null)
                continue;

            foreach (var val in update.Values)
            {
                if (val.Path is null)
                    continue;

                if (val.Path == "navigation.position" && val.Value is JsonElement posEl
                    && posEl.ValueKind == JsonValueKind.Object)
                {
                    if (posEl.TryGetProperty("latitude", out var lat)
                        && posEl.TryGetProperty("longitude", out var lon)
                        && lat.ValueKind == JsonValueKind.Number
                        && lon.ValueKind == JsonValueKind.Number)
                    {
                        _data.ApplyPosition(lat.GetDouble(), lon.GetDouble());
                        changed = true;
                    }
                    continue;
                }

                // Anchor position from signalk-anchoralarm-plugin.
                if (val.Path == "navigation.anchor.position" && val.Value is JsonElement anchorEl)
                {
                    if (anchorEl.ValueKind == JsonValueKind.Object
                        && anchorEl.TryGetProperty("latitude", out var aLat)
                        && anchorEl.TryGetProperty("longitude", out var aLon)
                        && aLat.ValueKind == JsonValueKind.Number
                        && aLon.ValueKind == JsonValueKind.Number)
                    {
                        _data.ApplyAnchorPosition(aLat.GetDouble(), aLon.GetDouble());
                        changed = true;
                    }
                    else if (anchorEl.ValueKind == JsonValueKind.Null)
                    {
                        _data.ClearAnchor();
                        changed = true;
                    }
                    continue;
                }

                // Course next-point position (lat/lon object).
                if ((val.Path == "navigation.courseGreatCircle.nextPoint.position"
                    || val.Path == "navigation.courseRhumbline.nextPoint.position")
                    && val.Value is JsonElement wpEl
                    && wpEl.ValueKind == JsonValueKind.Object)
                {
                    if (wpEl.TryGetProperty("latitude", out var wpLat)
                        && wpEl.TryGetProperty("longitude", out var wpLon)
                        && wpLat.ValueKind == JsonValueKind.Number
                        && wpLon.ValueKind == JsonValueKind.Number)
                    {
                        _data.ApplyCourseNextPointPosition(wpLat.GetDouble(), wpLon.GetDouble());
                        changed = true;
                    }
                    continue;
                }

                // Course previous-point position (lat/lon object).
                if ((val.Path == "navigation.courseGreatCircle.previousPoint.position"
                    || val.Path == "navigation.courseRhumbline.previousPoint.position")
                    && val.Value is JsonElement prevWpEl
                    && prevWpEl.ValueKind == JsonValueKind.Object)
                {
                    if (prevWpEl.TryGetProperty("latitude", out var prevWpLat)
                        && prevWpEl.TryGetProperty("longitude", out var prevWpLon)
                        && prevWpLat.ValueKind == JsonValueKind.Number
                        && prevWpLon.ValueKind == JsonValueKind.Number)
                    {
                        _data.ApplyCoursePreviousPointPosition(prevWpLat.GetDouble(), prevWpLon.GetDouble());
                        changed = true;
                    }
                    continue;
                }

                // String-valued paths (route href, route name).
                if (val.Value is JsonElement strEl && strEl.ValueKind == JsonValueKind.String)
                {
                    if (_data.ApplyString(val.Path, strEl.GetString()))
                    {
                        changed = true;
                        continue;
                    }
                }

                // Route deactivation: href arrives as null.
                if ((val.Path == "navigation.courseGreatCircle.activeRoute.href"
                    || val.Path == "navigation.courseRhumbline.activeRoute.href")
                    && (val.Value is null
                        || (val.Value is JsonElement nullEl && nullEl.ValueKind == JsonValueKind.Null)))
                {
                    _data.ClearCourse();
                    changed = true;
                    continue;
                }

                if (_data.Apply(val.Path, val.Value))
                    changed = true;
            }
        }

        if (changed)
        {
            if (_data.Latitude is not null && _data.Longitude is not null)
            {
                _track.Add(new TrackPoint(
                    DateTime.UtcNow,
                    _data.Latitude.Value,
                    _data.Longitude.Value,
                    _data.SpeedOverGround,
                    _data.CourseOverGround,
                    _data.Heading,
                    _data.WindAngleApparent,
                    _data.WindSpeedApparent,
                    _data.WindAngleTrue,
                    _data.WindSpeedTrue));
            }

            OnDataChanged?.Invoke();
        }
    }

    private void ProcessAisDelta(SignalkDelta delta)
    {
        foreach (var update in delta.Updates!)
        {
            if (update.Values is null)
                continue;

            foreach (var val in update.Values)
            {
                if (val.Path is null)
                    continue;

                _ais.Apply(delta.Context!, val.Path, val.Value);
            }
        }
    }

    /// <summary>
    /// Subscribes to additional paths on the active WebSocket (used by RawStream).
    /// The path is remembered and re-applied on reconnect.
    /// </summary>
    public async Task SubscribeExtraPathAsync(string path)
    {
        if (_extraPaths.Add(path))
            await SendSubscriptionAsync("vessels.self", [path]);
    }

    /// <summary>
    /// Unsubscribes from a previously added extra path.
    /// </summary>
    public async Task UnsubscribeExtraPathAsync(string path)
    {
        if (_extraPaths.Remove(path))
            await SendUnsubscribeAsync("vessels.self", [path]);
    }

    public IReadOnlyCollection<string> CoreSelfPaths => SelfPaths;

    private async Task SendSubscriptionAsync(string context, IEnumerable<string> paths)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;

        var message = JsonSerializer.Serialize(new
        {
            context,
            subscribe = paths.Select(p => new { path = p })
        });
        await SendRawAsync(message);
    }

    private async Task SendUnsubscribeAsync(string context, IEnumerable<string> paths)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;

        var message = JsonSerializer.Serialize(new
        {
            context,
            unsubscribe = paths.Select(p => new { path = p })
        });
        await SendRawAsync(message);
    }

    private async Task SendRawAsync(string json)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_ws is null || _ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; }
            catch (OperationCanceledException) { }
        }

        _sendLock.Dispose();
    }
}

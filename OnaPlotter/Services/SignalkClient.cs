using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;
using OnaPlotter.Services.Auth;
using OnaPlotter.Services.Json;
using OnaPlotter.Services.ServerNotifications;
using OnaPlotter.Services.Signalk;

namespace OnaPlotter.Services;

/// <summary>
/// The SignalK WebSocket transport: connects (token + base URL resolved
/// per attempt), subscribes per the <see cref="Tiers"/> table, assembles
/// frames, tracks liveness and reconnects with exponential backoff.
/// Each frame goes to <see cref="SignalkDeltaDispatcher"/>, which routes
/// it into <see cref="NavigationData"/>, <see cref="AisStore"/>,
/// <see cref="AtonStore"/>, <see cref="TrackBuffer"/> and
/// <see cref="ServerNotificationStore"/>; REST seeding on connect is
/// <see cref="SignalkConnectSeeding"/>.
/// </summary>
public sealed class SignalkClient : ISignalkClient, OnaPlotter.Services.Startup.ISocketStarter, IAsyncDisposable
{
    private const int InitialBackoffMs = 1_000;
    private const int MaxBackoffMs = 30_000;
    private const int ReceiveBufferBytes = 8 * 1024;
    /// <summary>Cap on the per-message ArrayBufferWriter used to assemble
    /// fragmented WebSocket frames. Above this we drop the buffer +
    /// force a reconnect rather than let a misbehaving server / debug
    /// endpoint exhaust the WASM heap. 4 MB is well above any plausible
    /// SignalK delta - even a 200-vessel bulk AIS push is single-digit KB.</summary>
    private const int MaxMessageBufferBytes = 4 * 1024 * 1024;
    private const int StaleDataThresholdSec = 5;

    /// <summary>
    /// Default SignalK subscription period for our standard paths:
    /// position / speed / heading / wind / depth / course / autopilot /
    /// tide. 1000 ms with <c>policy: "ideal"</c> caps per-path updates
    /// at 1 Hz on the server side, coalescing the 10+ Hz firehose a
    /// well-instrumented N2K bus produces. Cuts websocket bytes and
    /// downstream Blazor renders without any client-side throttling.
    /// </summary>
    public const int StandardSubscriptionPeriodMs = 1_000;

    /// <summary>
    /// Cap for slow-changing self paths (anchor radii, tide heights,
    /// solar state, active-route static fields). These don't change on
    /// a per-second cadence; subscribing at 1 Hz wastes bandwidth and
    /// (on Raspi Chrome) costs frame time the WASM client could be
    /// using to stay smooth. 10 s lines up with typical plugin
    /// publication cadences. User-facing effect: the anchor card still
    /// updates within a tick of a change; everyday traffic is an order
    /// of magnitude lower.
    /// </summary>
    public const int SlowSubscriptionPeriodMs = 10_000;

    /// <summary>
    /// Tighter period for Raw-Stream user-added subscriptions. The
    /// RawStream page is for inspecting live data; a 200 ms cap
    /// (5 Hz ceiling under <c>policy: "instant"</c>) keeps the display
    /// feeling real-time without drowning the viewer.
    /// </summary>
    public const int RawStreamSubscriptionPeriodMs = 200;

    /// <summary>
    /// One subscription bucket: a (context, period, policy) tuple plus
    /// the paths that belong in it. The reconnect loop iterates the
    /// <see cref="Tiers"/> registry and issues one Signal K
    /// <c>subscribe</c> message per non-empty tier.
    ///
    /// Adding a new context (atons.*, notifications.*, aircraft.*) is a
    /// new <see cref="SubscriptionTier"/> entry below. Tiers are
    /// always-on today; per-tier opt-out via Settings is a future
    /// extension when a real "I don't care about AIS" use case appears.
    /// </summary>
    internal sealed record SubscriptionTier(
        string Name,
        string Context,
        IReadOnlyList<string> Paths,
        int PeriodMs = StandardSubscriptionPeriodMs,
        string Policy = "ideal"
    );

    // --- Path lists by tier ---------------------------------------------
    //
    // Each list lives next to the tier that subscribes it so the wire-
    // format intent is local. Adding a new path is one entry in the
    // matching list - no enum to extend, no set algebra to maintain.

    /// <summary>vessels.self @ 1 Hz. Self-only fields (wind, depth,
    /// course-next-point, autopilot, tidal current). The shared nav
    /// fields (position / SOG / COG / heading) are NOT here - they
    /// live in the AIS tier and reach self via the vessels.* wildcard,
    /// avoiding duplicate delivery.</summary>
    private static readonly string[] SelfFastTierPaths =
    [
        "environment.depth.belowTransducer",
        // belowKeel is the helm-relevant reading ("metres under the
        // keel") and the clean input for the ANCHOR-TIDE prediction
        // (clearance_at_LW = belowKeel - drop, no draft / transducer
        // offset arithmetic). When the bus publishes it the HUD +
        // tide alarm prefer it; we keep belowTransducer subscribed too
        // so installs that only publish that path keep working.
        "environment.depth.belowKeel",
        // Depth from the waterline. Some installs publish only this
        // (transducer offset configured as surface offset); the HUD
        // falls back to it when neither keel nor transducer depth is
        // fresh.
        "environment.depth.belowSurface",
        Utilities.SkPaths.Environment.Wind.AngleApparent,
        Utilities.SkPaths.Environment.Wind.SpeedApparent,
        Utilities.SkPaths.Environment.Wind.AngleTrueWater,
        Utilities.SkPaths.Environment.Wind.SpeedTrue,
        Utilities.SkPaths.Environment.Wind.DirectionTrue,
        // Mayara radar ARPA targets. Paths arrive as
        // radars.<radarId>.targets.<targetId>.(position|course|speed|...)
        // under context vessels.self; ProcessSelfDelta detects and
        // routes them into AisStore with a synthesised radar.* context.
        "radars.*.targets.*",
        // Active course / route next-WP fields. The Signal K v2 course
        // surface lives at navigation.course.*; the course-provider
        // plugin derives per-leg numbers under
        // navigation.course.calcValues.* (bearing, distance, TTG, VMG,
        // XTE, route totals). v2 is the only subscribed surface; the
        // v1 subtrees (navigation.courseGreatCircle.*,
        // navigation.courseRhumbline.*) are not requested.
        // NavigationData.Apply still recognises the v1 case labels
        // as a safety net for any plugin that pushes them outside the
        // subscription list.
        Utilities.SkPaths.Navigation.Course.ActiveRoute,
        Utilities.SkPaths.Navigation.Course.ActiveRouteHref,
        Utilities.SkPaths.Navigation.Course.ActiveRouteName,
        Utilities.SkPaths.Navigation.Course.ActiveRoutePointIndex,
        Utilities.SkPaths.Navigation.Course.ActiveRoutePointTotal,
        Utilities.SkPaths.Navigation.Course.NextPoint,
        Utilities.SkPaths.Navigation.Course.NextPointPosition,
        Utilities.SkPaths.Navigation.Course.PreviousPointPosition,
        // Server's per-leg arrival-circle radius (metres). Drives the
        // chart ring + APPROACH alarm threshold so they agree with the
        // server's arrivalCircleEntered boundary instead of drifting
        // from the helm-configured fallback.
        Utilities.SkPaths.Navigation.Course.ArrivalCircle,
        Utilities.SkPaths.Navigation.Course.CalcValues.Distance,
        Utilities.SkPaths.Navigation.Course.CalcValues.BearingTrue,
        Utilities.SkPaths.Navigation.Course.CalcValues.TimeToGo,
        Utilities.SkPaths.Navigation.Course.CalcValues.VelocityMadeGood,
        Utilities.SkPaths.Navigation.Course.CalcValues.CrossTrackError,
        Utilities.SkPaths.Navigation.Course.CalcValues.RouteDistance,
        Utilities.SkPaths.Navigation.Course.CalcValues.RouteTimeToGo,
        // Bare-boolean leg-advance fallbacks. The notification-shaped
        // siblings live in SelfFastNotificationsTierPaths below (separate
        // tier so they ride policy=instant and don't get coalesced).
        Utilities.SkPaths.Navigation.Course.CalcValues.PerpendicularPassed,
        Utilities.SkPaths.Navigation.Course.CalcValues.ArrivalCircleEntered,
        // Speed through water (paddlewheel / sonic log). Self-only -
        // AIS targets don't publish their own STW. Surfaced in the SOG
        // HUD so the helm sees SOG vs STW side by side and can read
        // current effect (delta along track ≈ current contribution).
        Utilities.SkPaths.Navigation.SpeedThroughWater,
        // Autopilot state + target heading (true + magnetic) + target
        // AWA (wind mode). We subscribe both heading variants because
        // some AP plugins publish the target on the magnetic path
        // (helm steering by magnetic compass), others on true. The
        // extended HDG HUD surfaces whichever the bus actually carries.
        "steering.autopilot.state",
        "steering.autopilot.target.headingTrue",
        "steering.autopilot.target.headingMagnetic",
        "steering.autopilot.target.windAngleApparent",
        // Rudder angle. Spec path is steering.rudderAngle; some AP
        // plugins publish steering.autopilot.rudderAngle instead, so
        // we subscribe both and let NavigationData prefer the canonical
        // one.
        "steering.rudderAngle",
        "steering.autopilot.rudderAngle",
        // Tidal current: drift + set, used for the map arrow overlay.
        "environment.current.setTrue",
        "environment.current.drift",
    ];

    /// <summary>vessels.self @ 100 ms with policy=instant. The standard
    /// course-provider plugin emits leg-advance flags as NOTIFICATIONS
    /// (prepended "notifications." in src/lib/alarms.ts). Value is
    /// {state, method, message} when armed and null when cleared;
    /// both shapes are normalised into NavigationData's
    /// PerpendicularPassed / ArrivalCircleEntered booleans, whose
    /// enter transition drives AutoAdvanceService.
    ///
    /// policy=instant matters: these are edge-triggered state
    /// transitions (normal -> alert -> null) that the default "ideal"
    /// policy can coalesce away when surrounded by a high-frequency
    /// delta burst - swallowing the exact transition that drives
    /// auto-advance.</summary>
    private static readonly string[] SelfFastNotificationsTierPaths =
    [
        "notifications.navigation.course.perpendicularPassed",
        "notifications.navigation.course.arrivalCircleEntered",
    ];

    /// <summary>vessels.self @ 10 s. Plugin-driven slow fields (anchor
    /// radii, tide, solar state, design draft). Subscribed separately
    /// so we don't wake the WASM main thread once per second for data
    /// that changes every few minutes.</summary>
    private static readonly string[] SelfSlowTierPaths =
    [
        // Anchor alarm plugin (sbender9/signalk-anchoralarm-plugin).
        Utilities.SkPaths.Navigation.Anchor.Position,
        Utilities.SkPaths.Navigation.Anchor.MaxRadius,
        Utilities.SkPaths.Navigation.Anchor.CurrentRadius,
        // v2.0.0+ paths - silently absent on older plugin / no-plugin
        // self-host installs (subscription is harmless when no
        // publisher exists). Plumbing them through now means the HUD
        // adopts the plugin's authoritative readouts (rode counter,
        // bow-corrected distance + bearing) the moment a helm
        // upgrades the plugin - no client redeploy required. The
        // client-side GeoBearing fallback in the HUD card stays put
        // for the JS-only manual flow.
        Utilities.SkPaths.Navigation.Anchor.BearingTrue,
        Utilities.SkPaths.Navigation.Anchor.ApparentBearing,
        Utilities.SkPaths.Navigation.Anchor.RodeLength,
        Utilities.SkPaths.Navigation.Anchor.DistanceFromBow,
        // Tide height + next extremes (openwatersio/signalk-tides &
        // similar plugins). No-ops when the plugin isn't installed.
        "environment.tide.heightNow",
        "environment.tide.heightHigh",
        "environment.tide.heightLow",
        "environment.tide.timeHigh",
        "environment.tide.timeLow",
        "environment.tide.stationName",
        // Solar state string for auto night-mode. Published by
        // signalk-solar / signalk-sun-position: "day" / "dawn" /
        // "dusk" / "night". Servers without such a plugin get a
        // dormant auto toggle.
        "environment.sun",
        // Vessel design draft. Usually static (in vessel.json) but
        // signalk-load-data updates it per voyage; either way, 10 s
        // is more than fast enough and the slow tier keeps it off
        // the per-fix stream. Feeds the anchor-tide alarm and the
        // Settings draft auto-fill.
        "design.draft.current",
        "design.draft.maximum",
        // Local magnetic variation. Changes by fractions of a degree
        // per day of sailing, so the slow tier is plenty. Converts a
        // magnetic-only COG to true for CPA / COLREGS geometry
        // (NavigationData.CourseOverGroundTrueResolved); without it a
        // boat whose GPS publishes only magnetic COG runs collision
        // math off by the local variation.
        "navigation.magneticVariation",
        // (Route-total progress: the v2 equivalents live at
        //  navigation.course.calcValues.route.* on the fast tier; the
        //  legacy v1 aggregates (courseGreatCircle.activeRoute.*) and
        //  v1 per-leg paths are not subscribed. NavigationData.Apply
        //  still handles v1 values if a server emits them on the fast
        //  stream.)
    ];

    /// <summary>vessels.* (which matches self too) @ 1 Hz. Shared nav
    /// fields + AIS-only static data (name, MMSI, callsign, ship type,
    /// buddy). Going via the wildcard means self gets these once AND
    /// every AIS vessel gets them once - no duplication on self.</summary>
    private static readonly string[] AisTierPaths =
    [
        Utilities.SkPaths.Navigation.Position,
        Utilities.SkPaths.Navigation.SpeedOverGround,
        Utilities.SkPaths.Navigation.CourseOverGroundTrue,
        Utilities.SkPaths.Navigation.CourseOverGroundMagnetic,
        Utilities.SkPaths.Navigation.HeadingTrue,
        Utilities.SkPaths.Navigation.HeadingMagnetic,
        "name",
        "mmsi",
        "communication.callsignVhf",
        "design.aisShipType",
        // AIS Type 5 / Type 24 static carries vessel dimensions
        // (length-overall = dim A + dim B, beam = dim C + dim D).
        // signalk-ais-* plugins publish these under design.length /
        // design.beam. Optional - many recreational targets never
        // broadcast static (Type 24 isn't mandatory for class B), so
        // these stay null for that majority. Popup hides the row
        // when null rather than showing "--".
        "design.length",
        "design.beam",
        // Published by sbender9/signalk-buddylist-plugin when installed.
        // AisVessel.Apply sets IsBuddy; unknown when the plugin is absent.
        "buddy",
    ];

    /// <summary>vessels.self @ 1 Hz. The notifications.* wildcard catches
    /// every server-side notification (signalk-anchoralarm-plugin,
    /// signalk-mob-notifier, depth alarms, custom plugin alerts) so they
    /// can be surfaced in our alarm banner. Self-context only - expand
    /// to AIS-context notifications (server alerts about a specific
    /// vessel) when a real plugin produces them.
    /// <para>
    /// The two course-provider notification flags
    /// (perpendicularPassed / arrivalCircleEntered) are ALSO handled
    /// by SelfFastNotificationsTierPaths above at 100 ms / instant
    /// for auto-advance edge detection - we'll see them via both
    /// subscriptions; the handlers are idempotent.
    /// </para></summary>
    private static readonly string[] ServerNotificationsTierPaths =
    [
        "notifications.*",
    ];

    /// <summary>atons.* @ 60 s. AIS Type 21 broadcasts AtoN positions
    /// every ~3 min and the data is mostly static (a buoy doesn't
    /// move much) - 1 Hz would burn bandwidth + WASM main-thread
    /// time for nothing. 60 s lands well inside the 3-min refresh
    /// window. Anything under the path tree (name, position,
    /// atonType, virtual, communication) lands in
    /// <see cref="OnaPlotter.Services.AtonStore"/>.</summary>
    private static readonly string[] AtonsTierPaths =
    [
        "*",
    ];

    /// <summary>Period for atons.* and shore.basestations.* tiers.
    /// Both are static enough that 60 s is plenty.</summary>
    public const int AtonsSubscriptionPeriodMs = 60_000;

    /// <summary>resources.{routes,waypoints,notes,regions}.* with no
    /// vessel context. The signalk-server's resources API broadcasts
    /// PUT/POST/DELETE on these paths automatically (see
    /// <c>buildDeltaMsg</c> in <c>signalk-server/packages/server-api/
    /// src/resourcesapi.ts</c>); subscribing here lets a remote plotter
    /// edit a route on plotter A and have the change reach plotter B
    /// without REST polling. Value field carries the full resource
    /// document on create / update; <c>null</c> on delete. Routed via
    /// <see cref="OnResourceDelta"/> to <c>ResourceStore</c> which
    /// owns the in-memory cache + typed change events.
    /// <para>The four subpaths listed explicitly (instead of a single
    /// <c>resources.*</c> wildcard) so a future <c>resources.charts.*</c>
    /// or other resource type isn't accidentally pulled into the helm-
    /// route + waypoint + note + region working set, where it'd add
    /// dead code paths in the resource dispatch.</para></summary>
    private static readonly string[] ResourcesTierPaths =
    [
        "resources.routes.*",
        "resources.waypoints.*",
        "resources.notes.*",
        "resources.regions.*",
    ];

    /// <summary>
    /// The shipped-with-app subscription set. Reconnect iterates this
    /// in order and issues one <c>subscribe</c> per entry. Each tier
    /// is independent: dropping one (toggling AIS off, say) is a
    /// matter of filtering this list, no other code path knows about
    /// the wire shape.
    /// </summary>
    internal static readonly SubscriptionTier[] Tiers =
    [
        new SubscriptionTier(
            Name: "SelfFast",
            Context: "vessels.self",
            Paths: SelfFastTierPaths),
        new SubscriptionTier(
            Name: "SelfFastNotifications",
            Context: "vessels.self",
            Paths: SelfFastNotificationsTierPaths,
            PeriodMs: 100,
            Policy: "instant"),
        new SubscriptionTier(
            Name: "SelfSlow",
            Context: "vessels.self",
            Paths: SelfSlowTierPaths,
            PeriodMs: SlowSubscriptionPeriodMs),
        new SubscriptionTier(
            Name: "Ais",
            Context: "vessels.*",
            Paths: AisTierPaths),
        new SubscriptionTier(
            Name: "ServerNotifications",
            Context: "vessels.self",
            Paths: ServerNotificationsTierPaths),
        new SubscriptionTier(
            Name: "Atons",
            Context: "atons.*",
            Paths: AtonsTierPaths,
            PeriodMs: AtonsSubscriptionPeriodMs),
        // Resources have no vessel context; the SK server emits
        // delta updates with context = "vessels.self" by default
        // when a resource is PUT/POST/DELETE'd. Subscribing under
        // "*" matches that default and any future server that
        // emits without a context.
        new SubscriptionTier(
            Name: "Resources",
            Context: "*",
            Paths: ResourcesTierPaths,
            Policy: "instant"),
    ];

    /// <summary>Flattened view: every self-context path across all
    /// self tiers (fast + notifications + slow). Used by RawStream's
    /// chip-list defaults and by <c>SubscribedPaths</c>. Cached eagerly
    /// because callers iterate it in tight UI loops.</summary>
    internal static IReadOnlyList<string> SelfPaths { get; } =
        Tiers.Where(t => t.Context == "vessels.self")
             .SelectMany(t => t.Paths)
             .Distinct(StringComparer.Ordinal)
             .ToArray();

    /// <summary>Flattened view: just the slow self tier. Subscription
    /// tests assert it doesn't leak fast-moving fields.</summary>
    internal static IReadOnlyList<string> SlowSelfPaths { get; } =
        Tiers.Where(t => t.Name == "SelfSlow")
             .SelectMany(t => t.Paths)
             .ToArray();

    /// <summary>Flattened view: AIS tier. vessels.* matches self too,
    /// so these reach own-boat without appearing in SelfPaths.</summary>
    internal static IReadOnlyList<string> AisPaths { get; } =
        Tiers.Where(t => t.Context == "vessels.*")
             .SelectMany(t => t.Paths)
             .ToArray();

    /// <summary>
    /// Raised when own-boat navigation data changes (live delta, REST
    /// seed, or a heading-preference flip). Subscribers must handle
    /// thread-safety themselves (e.g. InvokeAsync(StateHasChanged) in
    /// Blazor components).
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

    /// <inheritdoc cref="SignalkDeltaDispatcher.OnResourceDelta"/>
    public event Action<string, string, JsonElement>? OnResourceDelta
    {
        add => _dispatcher.OnResourceDelta += value;
        remove => _dispatcher.OnResourceDelta -= value;
    }

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
        && (_time.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastMessageTicks)) > StaleDataThresholdSec * TimeSpan.TicksPerSecond;

    /// <inheritdoc cref="SignalkDeltaDispatcher.OwnMmsi"/>
    public string? OwnMmsi => _dispatcher.OwnMmsi;

    /// <inheritdoc cref="SelfDeltaHandler.OwnCallsign"/>
    public string? OwnCallsign => _dispatcher.OwnCallsign;

    private readonly NavigationData _data;
    private readonly ISignalKBaseUrl _baseUrl;
    private readonly ITokenStore? _tokens;
    private readonly ILogger<SignalkClient> _logger;
    private readonly TimeProvider _time;
    private readonly SignalkDeltaDispatcher _dispatcher;
    private readonly SignalkConnectSeeding _seeding;
    private readonly HeadingPreferenceSync _headingPreference;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Fired when the user pokes "Reconnect now" via <see cref="ReconnectNow"/>.
    // The reconnect loop breaks its backoff Task.Delay on this token and
    // resets backoffMs so the next connect attempt is immediate.
    private CancellationTokenSource? _backoffCts;

    // Extra paths subscribed to by the RawStream page (re-applied on reconnect).
    private readonly HashSet<string> _extraPaths = [];

    public SignalkClient(ISignalKBaseUrl baseUrl, ILoggerFactory loggerFactory,
        NavigationData data, TrackBuffer track, AisStore ais, HttpClient http, IAppSettings settings,
        ServerNotificationStore serverNotifs,
        AtonStore atons,
        TimeProvider time,
        ITokenStore? tokens = null)
    {
        var logger = loggerFactory.CreateLogger<SignalkClient>();
        _logger = logger;
        _data = data;
        _baseUrl = baseUrl;
        _time = time;
        _dispatcher = new SignalkDeltaDispatcher(data, track, ais, atons, serverNotifs, time, logger);
        _seeding = new SignalkConnectSeeding(http, baseUrl, data, ais,
            setSelfContext: _dispatcher.SetSelfContext,
            onDataChanged: RaiseDataChanged,
            loggerFactory);
        _headingPreference = new HeadingPreferenceSync(settings, data, RaiseDataChanged);

        // The WS URL is resolved per connect attempt against
        // _baseUrl.StreamUri() so a runtime origin change (helm flips
        // Standalone mode or edits the server URL) lands on the next
        // reconnect without restarting the whole client. The URL-
        // changed event aborts the current socket so the loop turns
        // over immediately.
        _baseUrl.OnBaseUrlChanged += HandleBaseUrlChanged;
        // The embedded-webapp deploy keeps cookie auth and has no token
        // store. When one is wired, a token change (login / logout /
        // 401 clear) drops the WS so the next reconnect carries the new
        // ?token=... in the URL (or drops it cleanly on logout).
        _tokens = tokens;
        if (_tokens is not null)
        {
            _tokens.OnTokenChanged += HandleBaseUrlChanged;
        }
    }

    private void RaiseDataChanged() => OnDataChanged?.Invoke();

    /// <summary>
    /// Starts the websocket receive loop. Called from ConnectionBootstrap.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = ReceiveLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        int backoffMs = InitialBackoffMs;

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            // Resolve the current URL at attempt-time, not at ctor
            // time, so a Standalone-mode toggle / URL edit picks up on
            // the next loop turn without a restart. Token also
            // resolved per attempt: if the helm signs in / out between
            // reconnects, we pick up the new auth state without
            // touching this code path.
            var wsUri = AppendTokenIfPresent(_baseUrl.StreamUri());
            try
            {
                // Log the URL with the token redacted - the token in
                // logs would be a credential leak through any helm
                // who runs a screen recording for support.
                _logger.LogInformation("Connecting to SignalK at {Uri}",
                    RedactTokenForLog(wsUri));
                await ws.ConnectAsync(wsUri, ct);
                _ws = ws;
                _logger.LogInformation("Connected to SignalK");
                MarkConnectionOpened();
                OnConnectionChanged?.Invoke();
                backoffMs = InitialBackoffMs;

                // One Signal K subscribe message per tier with the
                // matching (context, period, policy). Tier order is
                // preserved on the wire so the /raw debug viewer sees a
                // stable shape across reconnects. RawStream's extra
                // paths are runtime-discovered, not a shipped set, so
                // they stay outside the Tiers registry.
                foreach (var tier in Tiers)
                {
                    if (tier.Paths.Count == 0) continue;
                    await SendSubscriptionAsync(tier.Context, tier.Paths,
                        tier.PeriodMs, tier.Policy);
                }
                if (_extraPaths.Count > 0)
                    await SendSubscriptionAsync("vessels.self", _extraPaths,
                        periodMs: RawStreamSubscriptionPeriodMs, policy: "instant");

                _seeding.StartOnConnect(ct);

                var buffer = new byte[ReceiveBufferBytes];
                // UTF-8 bytes are accumulated as-is and fed to the
                // span-based deserializer; a UTF-16 string is only built
                // when an OnRawMessage subscriber needs the text (the
                // RawStream page is rarely open). ArrayBufferWriter grows
                // on demand without the LOH thrash a single contiguous
                // reallocation would cause near the 4 MB cap.
                var messageBuffer = new ArrayBufferWriter<byte>(ReceiveBufferBytes);

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    messageBuffer.Write(buffer.AsSpan(0, result.Count));

                    // A buggy or malicious server emitting a fragmented
                    // stream without EndOfMessage indefinitely (or a
                    // single huge message from a misbehaving /raw debug
                    // endpoint) would otherwise grow the buffer until the
                    // WASM heap is exhausted and the tab dies.
                    if (messageBuffer.WrittenCount > MaxMessageBufferBytes)
                    {
                        _logger.LogWarning(
                            "Message buffer exceeded {Limit} bytes (got {Got}) - dropping and forcing reconnect",
                            MaxMessageBufferBytes, messageBuffer.WrittenCount);
                        messageBuffer.ResetWrittenCount();
                        break;
                    }

                    if (result.EndOfMessage)
                    {
                        ProcessMessageBytes(messageBuffer.WrittenSpan);
                        messageBuffer.ResetWrittenCount();
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // _ws stays referenced until the finally clears it;
                // keep the convention that _ws is null only when there's
                // no usable socket so concurrent SendAsync paths don't
                // race on a half-aborted instance.
                _ws = null;
                IsConnected = false;
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SignalK connection lost, reconnecting in {BackoffMs}ms", backoffMs);
            }
            finally
            {
                // Null _ws AS PART OF THE SAME FRAME that observes the
                // ReceiveAsync exception so a concurrent SendSubscriptionAsync
                // / SendRawAsync (RawStream subscribe) can't see a non-null
                // _ws referencing an aborted socket. The State==Open guard
                // inside Send* is otherwise a tight race with the throw.
                _ws = null;
            }
            IsConnected = false;
            _dispatcher.ResetOnDisconnect();
            OnConnectionChanged?.Invoke();

            // Linked delay token: cancels either when the whole client
            // is disposed (ct) OR when the user asks for an immediate
            // retry via ReconnectNow (_backoffCts). The two dispositions
            // are distinguished inside the catch so we exit on disposal
            // but loop on user-retry with a reset backoff.
            _backoffCts?.Dispose();
            _backoffCts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _backoffCts.Token);
            try
            {
                await Task.Delay(backoffMs, linked.Token);
                backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;              // client disposed
            }
            catch (OperationCanceledException)
            {
                // User tapped Reconnect now - break out of the delay and
                // retry immediately. Reset the backoff so the next drop
                // doesn't inherit a fast-retry cadence.
                backoffMs = InitialBackoffMs;
            }
        }
    }

    /// <summary>
    /// Cancel the current reconnect-backoff delay (if any) and retry
    /// immediately. No-op when already connected. Safe to call from UI
    /// threads; the receive loop handles the cancellation inline.
    /// </summary>
    public void ReconnectNow()
    {
        if (IsConnected) return;
        try { _backoffCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Origin-changed handler. Called from the settings
    /// fan-out (so on the UI thread, in WASM's single-threaded
    /// runtime). Two paths:
    ///   - Currently connected: abort the open WS so the receive
    ///     loop's outer try sees the close, drops down to the backoff
    ///     branch, then we cancel the backoff to retry immediately
    ///     against the new URL.
    ///   - Currently in backoff: cancel the backoff so the next
    ///     attempt picks up the new URL.
    /// Either way the next loop iteration reads <c>_baseUrl.StreamUri()</c>
    /// fresh and connects to the new origin.</summary>
    private void HandleBaseUrlChanged()
    {
        try { _ws?.Abort(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogWarning("WS abort during URL change failed: {Message}", ex.Message);
        }
        try { _backoffCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Append <c>&amp;token=&lt;jwt&gt;</c> to the WS URL when
    /// the token store holds a valid JWT. SignalK 1.7 spec routes WS
    /// auth via the URL query (cookies don't cross origins; headers
    /// aren't supported on browser WebSocket upgrades). No-op when
    /// the store is empty / expired or wasn't injected at all (the
    /// embedded-webapp deploy keeps cookie auth and doesn't need
    /// this).</summary>
    private Uri AppendTokenIfPresent(Uri baseStreamUri)
    {
        if (_tokens is null || !_tokens.IsValid) return baseStreamUri;
        // _tokens.Token is non-null when IsValid is true.
        var withToken = baseStreamUri.ToString()
            + "&token=" + Uri.EscapeDataString(_tokens.Token!);
        return new Uri(withToken);
    }

    /// <summary>Strip the <c>token</c> query parameter for log
    /// emission so a screen recording / log share doesn't leak the
    /// helm's session JWT. Keeps everything else - subscribe param,
    /// any other future query - so logs still help diagnose connect
    /// failures.
    /// <code>
    /// ws://h:3000/signalk/v1/stream?subscribe=none&amp;token=eyJ...
    ///   -> ws://h:3000/signalk/v1/stream?subscribe=none&amp;token=***
    /// </code></summary>
    private static Uri RedactTokenForLog(Uri uri)
    {
        var query = uri.Query;
        if (string.IsNullOrEmpty(query) || !query.Contains("token=", StringComparison.Ordinal))
            return uri;
        // Manual rewrite to avoid pulling in QueryHelpers + a
        // dictionary alloc per connect log line.
        var trimmed = query.TrimStart('?');
        var parts = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var redacted = parts
            .Select(p => p.StartsWith("token=", StringComparison.Ordinal) ? "token=***" : p);
        return new Uri($"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}?{string.Join('&', redacted)}");
    }

    /// <summary>Flips <see cref="IsConnected"/> to true and seeds
    /// <c>_lastMessageTicks</c> with the current UTC ticks. Without
    /// the seed, the field defaults to 0 (Unix epoch) and
    /// <see cref="IsDataStale"/> reports true the moment IsConnected
    /// becomes true - before any deltas have arrived - and the
    /// connection chip sticks on "Stale" until an unrelated render.
    /// Exposed internally so tests can drive the same flow without
    /// opening a real websocket.</summary>
    internal void MarkConnectionOpened()
    {
        Interlocked.Exchange(ref _lastMessageTicks, _time.GetUtcNow().UtcTicks);
        IsConnected = true;
    }

    /// <summary>Mirror of <see cref="MarkConnectionOpened"/> for the
    /// disconnect side. Sets <see cref="IsConnected"/> to false
    /// without touching the WS pump. Tests pair it with
    /// <see cref="RaiseConnectionChanged"/> to drive reconnect-reconcile
    /// flows deterministically.</summary>
    internal void MarkConnectionClosed()
    {
        IsConnected = false;
    }

    /// <summary>Fire the public <see cref="OnConnectionChanged"/>
    /// event from a test, as the WS loop does right after each
    /// connection edge.</summary>
    internal void RaiseConnectionChanged() => OnConnectionChanged?.Invoke();

    /// <summary>
    /// Test-facing string entry point for hand-crafted JSON fixtures;
    /// the receive loop calls <see cref="ProcessMessageBytes"/> with
    /// the raw UTF-8 bytes.
    /// </summary>
    internal void ProcessMessage(string json)
    {
        ProcessMessageBytes(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Hot path: invoked once per fully-assembled WebSocket frame.
    /// Stamps liveness, feeds the RawStream subscriber, hands the
    /// frame to the dispatcher and raises <see cref="OnDataChanged"/>
    /// when own-boat state moved.
    /// </summary>
    internal void ProcessMessageBytes(ReadOnlySpan<byte> utf8Bytes)
    {
        Interlocked.Exchange(ref _lastMessageTicks, _time.GetUtcNow().UtcTicks);

        // OnRawMessage is the RawStream debug page subscription; only
        // allocate the UTF-16 string when someone is actually listening.
        // The captured local also pins the delegate snapshot so a
        // concurrent unsubscribe can't NRE us.
        var rawHandler = OnRawMessage;
        if (rawHandler is not null)
        {
            rawHandler.Invoke(Encoding.UTF8.GetString(utf8Bytes));
        }

        try
        {
            if (_dispatcher.Dispatch(utf8Bytes))
                OnDataChanged?.Invoke();
        }
        catch (JsonException)
        {
            // Ignore malformed messages.
        }
        catch (Exception ex)
        {
            // Don't let a single bad message (or a throwing subscriber)
            // crash the receive loop.
            _logger.LogWarning(ex, "Error processing SignalK message");
        }
    }

    /// <inheritdoc cref="SignalkDeltaDispatcher.SetSelfContext"/>
    internal void SetSelfContext(string? raw) => _dispatcher.SetSelfContext(raw);

    /// <inheritdoc cref="SignalkDeltaDispatcher.IsSelfContext"/>
    internal bool IsSelfContext(string? context) => _dispatcher.IsSelfContext(context);

    /// <summary>Runs the REST course seed directly; tests drive it with
    /// a mock HttpClient instead of opening a socket.</summary>
    internal Task SeedSelfCourseFromRestAsync(CancellationToken ct) => _seeding.SeedCourseAsync(ct);

    /// <summary>
    /// Subscribes to additional paths on the active WebSocket (used by RawStream).
    /// The path is remembered and re-applied on reconnect.
    /// </summary>
    public async Task SubscribeExtraPathAsync(string path)
    {
        if (_extraPaths.Add(path))
            await SendSubscriptionAsync("vessels.self", [path],
                periodMs: RawStreamSubscriptionPeriodMs, policy: "instant");
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

    /// <summary>
    /// Every path the client is currently subscribed to: the core self
    /// tier, the AIS tier, plus any <see cref="SubscribeExtraPathAsync"/>
    /// additions. Used by RawStream to populate its chip list.
    /// <para><b>Cold-path:</b> allocates a fresh set per call; the only
    /// consumer is the RawStream debug page's chip-list render. Do NOT
    /// wire this into a per-tick / per-delta path - cache with
    /// invalidation on <see cref="SubscribeExtraPathAsync"/> instead.</para>
    /// </summary>
    public IReadOnlyCollection<string> SubscribedPaths
    {
        get
        {
            var set = new HashSet<string>(SelfPaths, StringComparer.Ordinal);
            foreach (var p in AisPaths) set.Add(p);
            foreach (var p in _extraPaths) set.Add(p);
            return set;
        }
    }

    /// <summary>
    /// Sends a SignalK subscribe request with a per-path period + policy.
    /// Defaults are the <see cref="StandardSubscriptionPeriodMs"/> + "ideal"
    /// profile, which caps updates at 1 Hz and lets the server coalesce
    /// bursts. RawStream usage overrides with a shorter period and "instant"
    /// policy so raw deltas keep flowing.
    /// </summary>
    private async Task SendSubscriptionAsync(string context, IEnumerable<string> paths,
        int periodMs = StandardSubscriptionPeriodMs, string policy = "ideal")
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;
        var request = SignalkSubscribeRequest.For(context, paths, periodMs, policy);
        var message = JsonSerializer.Serialize(request, OnaJsonContext.Default.SignalkSubscribeRequest);
        await SendRawAsync(message);
    }

    private async Task SendUnsubscribeAsync(string context, IEnumerable<string> paths)
    {
        if (_ws is null || _ws.State != WebSocketState.Open) return;
        var request = SignalkUnsubscribeRequest.For(context, paths);
        var message = JsonSerializer.Serialize(request, OnaJsonContext.Default.SignalkUnsubscribeRequest);
        await SendRawAsync(message);
    }

    private async Task SendRawAsync(string json)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_ws is null || _ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            var ct = _cts?.Token ?? CancellationToken.None;
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Symmetric unsubscribe so a settings stub shared across several
        // clients in tests doesn't accumulate handlers.
        _headingPreference.Dispose();
        _baseUrl.OnBaseUrlChanged -= HandleBaseUrlChanged;
        if (_tokens is not null)
        {
            _tokens.OnTokenChanged -= HandleBaseUrlChanged;
        }

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

        _backoffCts?.Dispose();
        _sendLock.Dispose();
    }
}

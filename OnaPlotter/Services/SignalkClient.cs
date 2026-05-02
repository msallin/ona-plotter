using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OnaPlotter.Models;
using OnaPlotter.Services.Api;

namespace OnaPlotter.Services;

/// <summary>
/// Maintains a persistent WebSocket connection to a SignalK server, receives
/// delta messages, and distributes parsed navigation/AIS data to the shared
/// <see cref="NavigationData"/>, <see cref="TrackBuffer"/>, and <see cref="AisStore"/>.
/// Automatically reconnects with exponential backoff on connection loss.
/// </summary>
public sealed class SignalkClient : IAsyncDisposable
{
    private const int InitialBackoffMs = 1_000;
    private const int MaxBackoffMs = 30_000;
    private const int ReceiveBufferBytes = 8 * 1024;
    /// <summary>Cap on the per-message ArrayBufferWriter used to assemble
    /// fragmented WebSocket frames. Above this we drop the buffer +
    /// force a reconnect rather than let a misbehaving server / debug
    /// endpoint exhaust the WASM heap. 4 MB is well above any plausible
    /// SignalK delta -- even a 200-vessel bulk AIS push is single-digit KB.</summary>
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

    private readonly NavigationData _data;
    private readonly TrackBuffer _track;
    private readonly AisStore _ais;
    private readonly AtonStore _atons;
    private readonly OnaPlotter.Services.ServerNotifications.ServerNotificationStore _serverNotifs;
    private readonly Uri _wsUri;
    private readonly HttpClient _http;
    private readonly IAppSettings _settings;
    private readonly ISignalKBaseUrl _baseUrl;
    private string _selfContext;
    private readonly ILogger<SignalkClient> _logger;
    private readonly TimeProvider _time;
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
    // matching list -- no enum to extend, no set algebra to maintain.

    /// <summary>vessels.self @ 1 Hz. Self-only fields (wind, depth,
    /// course-next-point, autopilot, tidal current). The shared nav
    /// fields (position / SOG / COG / heading) are NOT here -- they
    /// live in the AIS tier and reach self via the vessels.* wildcard,
    /// avoiding duplicate delivery.</summary>
    private static readonly string[] SelfFastTierPaths =
    [
        "environment.depth.belowTransducer",
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
        // XTE, route totals). SK Node Server >= 2.x has shipped the
        // v2 surface as the default for years; the legacy v1 subtrees
        // (navigation.courseGreatCircle.*, navigation.courseRhumbline.*)
        // were dropped from the subscription list as part of the
        // tech-debt pass. NavigationData.Apply's v1 case labels
        // remain as a safety net for any plugin that still emits
        // them.
        Utilities.SkPaths.Navigation.Course.ActiveRoute,
        Utilities.SkPaths.Navigation.Course.ActiveRouteHref,
        Utilities.SkPaths.Navigation.Course.ActiveRouteName,
        Utilities.SkPaths.Navigation.Course.ActiveRoutePointIndex,
        Utilities.SkPaths.Navigation.Course.ActiveRoutePointTotal,
        Utilities.SkPaths.Navigation.Course.NextPoint,
        Utilities.SkPaths.Navigation.Course.NextPointPosition,
        Utilities.SkPaths.Navigation.Course.PreviousPointPosition,
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
        // Autopilot state + target heading + target AWA (wind mode).
        "steering.autopilot.state",
        "steering.autopilot.target.headingTrue",
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
    /// ProcessSelfDelta normalises both shapes into NavigationData's
    /// PerpendicularPassed / ArrivalCircleEntered booleans so
    /// MaybeAutoAdvanceWaypoint's edge trigger fires on the enter
    /// transition.
    ///
    /// policy=instant matters: these are edge-triggered state
    /// transitions (normal -> alert -> null) that the default "ideal"
    /// policy can coalesce away when surrounded by a high-frequency
    /// delta burst -- swallowing the exact transition that drives
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
        // Note: bearing-to-anchor is NOT subscribed -- we compute it
        // client-side via Utilities.GeoBearing from anchor lat/lon
        // and own-ship lat/lon. That keeps the HUD bearing needle
        // working on the JS-only manual-anchor fallback (when the
        // plugin isn't installed) instead of being silent there.
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
        // (Route-total progress: the v2 equivalents live at
        //  navigation.course.calcValues.route.* on the fast tier; the
        //  legacy v1 aggregates (courseGreatCircle.activeRoute.*) were
        //  removed in the same sweep that dropped the v1 per-leg
        //  subscriptions. NavigationData.Apply still handles v1 values
        //  if a server emits them on the fast stream.)
    ];

    /// <summary>vessels.* (which matches self too) @ 1 Hz. Shared nav
    /// fields + AIS-only static data (name, MMSI, callsign, ship type,
    /// buddy). Going via the wildcard means self gets these once AND
    /// every AIS vessel gets them once -- no duplication on self.</summary>
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
        // Published by sbender9/signalk-buddylist-plugin when installed.
        // AisVessel.Apply sets IsBuddy; unknown when the plugin is absent.
        "buddy",
    ];

    /// <summary>vessels.self @ 1 Hz. The notifications.* wildcard catches
    /// every server-side notification (signalk-anchoralarm-plugin,
    /// signalk-mob-notifier, depth alarms, custom plugin alerts) so they
    /// can be surfaced in our alarm banner. Self-context only -- expand
    /// to AIS-context notifications (server alerts about a specific
    /// vessel) when a real plugin produces them.
    /// <para>
    /// The two course-provider notification flags
    /// (perpendicularPassed / arrivalCircleEntered) are ALSO handled
    /// by SelfFastNotificationsTierPaths above at 100 ms / instant
    /// for auto-advance edge detection -- we'll see them via both
    /// subscriptions; the handlers are idempotent.
    /// </para></summary>
    private static readonly string[] ServerNotificationsTierPaths =
    [
        "notifications.*",
    ];

    /// <summary>atons.* @ 60 s. AIS Type 21 broadcasts AtoN positions
    /// every ~3 min and the data is mostly static (a buoy doesn't
    /// move much) -- 1 Hz would burn bandwidth + WASM main-thread
    /// time for nothing. 60 s is what Freeboard-SK uses too. Anything
    /// under the path tree (name, position, atonType, virtual,
    /// communication) lands in <see cref="OnaPlotter.Services.AtonStore"/>.</summary>
    private static readonly string[] AtonsTierPaths =
    [
        "*",
    ];

    /// <summary>Period for atons.* and shore.basestations.* tiers.
    /// Both are static enough that 60 s is plenty.</summary>
    public const int AtonsSubscriptionPeriodMs = 60_000;

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
    ];

    /// <summary>Back-compat view: every self-context path across all
    /// self tiers (fast + notifications + slow). Used by RawStream's
    /// chip-list defaults and by <c>SubscribedPaths</c>. Cached eagerly
    /// because callers iterate it in tight UI loops.</summary>
    internal static IReadOnlyList<string> SelfPaths { get; } =
        Tiers.Where(t => t.Context == "vessels.self")
             .SelectMany(t => t.Paths)
             .Distinct(StringComparer.Ordinal)
             .ToArray();

    /// <summary>Back-compat view: just the slow self tier. Subscription
    /// tests assert it doesn't leak fast-moving fields.</summary>
    internal static IReadOnlyList<string> SlowSelfPaths { get; } =
        Tiers.Where(t => t.Name == "SelfSlow")
             .SelectMany(t => t.Paths)
             .ToArray();

    /// <summary>Back-compat view: AIS tier. vessels.* matches self too,
    /// so these reach own-boat without appearing in SelfPaths.</summary>
    internal static IReadOnlyList<string> AisPaths { get; } =
        Tiers.Where(t => t.Context == "vessels.*")
             .SelectMany(t => t.Paths)
             .ToArray();

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
        && (_time.GetUtcNow().UtcTicks - Interlocked.Read(ref _lastMessageTicks)) > StaleDataThresholdSec * TimeSpan.TicksPerSecond;

    // --- Own-track sampling ------------------------------------------
    // TrackBuffer capacity is 1000. Adding a point every delta (typical
    // 1 Hz with the course-provider feeding 1-2 paths per tick, but
    // bursty up to 3-5 Hz under heavy wind/depth traffic) burned the
    // buffer in ~15-30 minutes of sailing -- the on-map trail kept
    // losing the first half-hour of the leg. 0.2 Hz sampling (one
    // point every 5 s) stretches the same buffer to ~83 min of
    // history, which is enough to visually follow a typical day-sail.
    // The live position indicator still updates on every delta;
    // only the persisted trail is throttled.
    private const int TrackSampleIntervalMs = 5_000;
    private long _lastTrackSampleTicks;

    private bool ShouldSampleTrackPoint()
    {
        long nowTicks = _time.GetUtcNow().UtcTicks;
        long lastTicks = Interlocked.Read(ref _lastTrackSampleTicks);
        if (nowTicks - lastTicks < TrackSampleIntervalMs * TimeSpan.TicksPerMillisecond)
            return false;
        Interlocked.Exchange(ref _lastTrackSampleTicks, nowTicks);
        return true;
    }

    /// <summary>ARCH-006 extracts the four REST seeders into focused
    /// classes under <c>OnaPlotter.Services.Signalk</c>. Each owns ONE
    /// REST round-trip and its parsing rules; SignalkClient still
    /// fires-and-forgets them on connect. Constructed inline from the
    /// dependencies SignalkClient already holds so the public ctor
    /// signature doesn't grow more DI parameters (which would have
    /// rippled through every test that builds a SignalkClient).</summary>
    private readonly OnaPlotter.Services.Signalk.SignalkDraftSeeder _draftSeeder;
    private readonly OnaPlotter.Services.Signalk.SignalkSelfContextSeeder _selfContextSeeder;
    private readonly OnaPlotter.Services.Signalk.SignalkVesselNamesSeeder _vesselNamesSeeder;
    private readonly OnaPlotter.Services.Signalk.SignalkCourseSeeder _courseSeeder;

    public SignalkClient(ISignalKBaseUrl baseUrl, ILogger<SignalkClient> logger,
        TrackBuffer track, AisStore ais, HttpClient http, IAppSettings settings,
        OnaPlotter.Services.ServerNotifications.ServerNotificationStore serverNotifs,
        AtonStore atons,
        TimeProvider time)
    {
        _logger = logger;
        _data = new NavigationData();
        _track = track;
        _ais = ais;
        _atons = atons;
        _http = http;
        _baseUrl = baseUrl;
        _wsUri = baseUrl.StreamUri();
        _selfContext = "";
        _settings = settings;
        _serverNotifs = serverNotifs;
        _time = time;

        // Each seeder takes only the dependencies it actually touches.
        // We use NullLogger here rather than weaving a per-category
        // ILogger<T> through DI because all four seeders are
        // hand-constructed (not registered) and the receive-loop
        // already logs the surrounding lifecycle. Threading per-
        // category loggers through is a follow-on once these classes
        // earn their own DI registration.
        _draftSeeder = new OnaPlotter.Services.Signalk.SignalkDraftSeeder(
            http, baseUrl, _data,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OnaPlotter.Services.Signalk.SignalkDraftSeeder>.Instance,
            onDataChanged: () => OnDataChanged?.Invoke());
        _selfContextSeeder = new OnaPlotter.Services.Signalk.SignalkSelfContextSeeder(
            http, baseUrl,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OnaPlotter.Services.Signalk.SignalkSelfContextSeeder>.Instance,
            setSelfContext: SetSelfContext);
        _vesselNamesSeeder = new OnaPlotter.Services.Signalk.SignalkVesselNamesSeeder(
            http, baseUrl, _ais,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OnaPlotter.Services.Signalk.SignalkVesselNamesSeeder>.Instance);
        _courseSeeder = new OnaPlotter.Services.Signalk.SignalkCourseSeeder(
            http, baseUrl, _data,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OnaPlotter.Services.Signalk.SignalkCourseSeeder>.Instance,
            onDataChanged: () => OnDataChanged?.Invoke());

        // Heading / COG preference: settings drive which SignalK path
        // wins when both true + magnetic are published. Sync now and on
        // every change so toggling the preference re-evaluates without
        // waiting for the next delta (the getter reads the cached
        // raw values, so the next render picks up immediately).
        ApplyHeadingPreferenceFromSettings();
        settings.OnSettingsChanged += OnSettingsChangedSync;
    }

    private void OnSettingsChangedSync()
    {
        ApplyHeadingPreferenceFromSettings();
        // HUDs re-derive Heading / CourseOverGround on next render; a
        // nudge wakes any component that isn't also listening to
        // OnSettingsChanged directly.
        OnDataChanged?.Invoke();
    }

    private void ApplyHeadingPreferenceFromSettings()
    {
        _data.PreferMagneticHeading = _settings.PreferMagneticHeading;
        _data.PreferMagneticCourse = _settings.PreferMagneticCourse;
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
        int backoffMs = InitialBackoffMs;

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                _logger.LogInformation("Connecting to SignalK at {Uri}", _wsUri);
                await ws.ConnectAsync(_wsUri, ct);
                _ws = ws;
                _logger.LogInformation("Connected to SignalK");
                MarkConnectionOpened();
                OnConnectionChanged?.Invoke();
                backoffMs = InitialBackoffMs;

                // Iterate the Tiers registry: one Signal K
                // <c>subscribe</c> message per tier with the matching
                // (context, period, policy). Adding a new context type
                // (atons.*, notifications.*, aircraft.*) is a new tier
                // entry above; this loop doesn't change. Tier order
                // is preserved on the wire so the /raw debug viewer
                // sees a stable shape across reconnects.
                //
                // Extra paths added by the RawStream page use the
                // instant-with-minPeriod profile for live debugging;
                // they stay outside the Tiers registry because they're
                // runtime-discovered, not a shipped-with-app set.
                foreach (var tier in Tiers)
                {
                    if (tier.Paths.Count == 0) continue;
                    await SendSubscriptionAsync(tier.Context, tier.Paths,
                        tier.PeriodMs, tier.Policy);
                }
                if (_extraPaths.Count > 0)
                    await SendSubscriptionAsync("vessels.self", _extraPaths,
                        periodMs: RawStreamSubscriptionPeriodMs, policy: "instant");

                // SignalK "ideal" subscriptions only send deltas as values
                // change; static AIS data (names, MMSI) received BEFORE we
                // connected is never replayed over the stream. Seed those
                // from the REST snapshot so vessels show their name instead
                // of a bare MMSI on first paint. Best-effort -- any failure
                // just leaves names to trickle in via live deltas.
                // Fire-and-forget the seed calls: WASM is single-threaded
                // so Task.Run is pointless (just hides continuations from
                // the debugger and swallows exceptions into a faulted
                // wrapper). Bare `_ = FooAsync(ct)` surfaces unhandled
                // exceptions on TaskScheduler.UnobservedTaskException
                // instead.
                _ = _vesselNamesSeeder.SeedAsync(ct);

                // Identify ourselves via REST so we can filter own-boat out
                // of the AIS list even on servers that never emit a hello
                // with "self" or push own-boat only as "vessels.<urn>".
                _ = _selfContextSeeder.SeedAsync(ct);

                // Subscriptions only fire on change, so a route active
                // BEFORE our socket opens (another plotter, freeboard-sk
                // in a second tab, a previous browser session) never
                // arrives on the delta stream. The v2 Course API lives
                // on its own REST surface which the delta stream doesn't
                // cover for the initial state; fetch it once on connect.
                _ = _courseSeeder.SeedAsync(ct);

                // design.draft: narrow fetch on /vessels/self/design/draft.
                _ = _draftSeeder.SeedAsync(ct);

                var buffer = new byte[ReceiveBufferBytes];
                // Accumulate UTF-8 bytes directly. Earlier shape
                // decoded each frame to a string + appended to a
                // StringBuilder; that allocated a UTF-16 string per
                // frame (2x the byte payload) just to throw it away
                // after deserialise. The byte buffer feeds
                // JsonSerializer.Deserialize<T>(ReadOnlySpan<byte>)
                // directly; the string allocation only happens when
                // an OnRawMessage subscriber needs the text (mostly
                // unsubscribed -- the RawStream page is rarely open).
                // ArrayBufferWriter handles the grow-on-demand without
                // the LOH thrash a single contiguous reallocation
                // would cause for the pathological 4 MB cap case.
                var messageBuffer = new ArrayBufferWriter<byte>(ReceiveBufferBytes);

                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    messageBuffer.Write(buffer.AsSpan(0, result.Count));

                    // Cap the per-message buffer at 4 MB. A buggy or
                    // malicious server emitting a fragmented stream
                    // without EndOfMessage indefinitely (or a single
                    // >>RAM message from a misbehaving /raw debug
                    // endpoint) would otherwise grow the buffer
                    // until the WASM heap is exhausted and the tab
                    // dies, with no recovery short of a reload. 4 MB
                    // is well above any plausible delta payload --
                    // even a ~200-vessel AIS bulk update is single-
                    // digit KB. Note: the cap counts bytes here; the
                    // earlier StringBuilder version counted UTF-16
                    // chars (which were 2x for ASCII / 1x for BMP);
                    // for typical SK JSON (mostly ASCII) the byte
                    // count is half the previous char count, so a
                    // 4 MB byte cap is roughly equivalent to the
                    // earlier 4 MB char cap on payload size in practice.
                    if (messageBuffer.WrittenCount > MaxMessageBufferBytes)
                    {
                        _logger.LogWarning(
                            "Message buffer exceeded {Limit} bytes (got {Got}) -- dropping and forcing reconnect",
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
            // Drop any cached server notifications: while we're
            // offline we can't see if the server has cleared one,
            // and a stale "ANCHOR DRAGGING" banner from before the
            // drop would be misleading. The server re-publishes the
            // active set on reconnect so they reappear naturally.
            _serverNotifs.Reset();
            // Same logic for AtoNs: the server re-broadcasts the
            // active set on reconnect (each AtoN sends Type 21 every
            // ~3 min anyway) so a stale buoy 100 nm astern is just
            // noise on the chart while we're disconnected.
            _atons.Reset();
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
                // User tapped Reconnect now -- break out of the delay and
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

    /// <summary>Flips <see cref="IsConnected"/> to true and seeds
    /// <c>_lastMessageTicks</c> with the current UTC ticks. Without
    /// the seed, the field defaults to 0 (Unix epoch) and
    /// <see cref="IsDataStale"/> reports true the moment IsConnected
    /// becomes true -- before any deltas have arrived. The connection
    /// chip then renders "Stale" on first paint, and the
    /// MainLayout-side change tracker (wasStale flips on
    /// IsDataStale-state transitions) misses the false-to-false
    /// "data started flowing" event because both before and after
    /// values look identical from its point of view, leaving the
    /// chip stuck on "Stale" until the next unrelated render.
    /// Exposed internally so tests can drive the same flow without
    /// opening a real websocket.</summary>
    internal void MarkConnectionOpened()
    {
        Interlocked.Exchange(ref _lastMessageTicks, _time.GetUtcNow().UtcTicks);
        IsConnected = true;
    }

    /// <summary>
    /// Test-facing string entry point. The WebSocket receive loop calls
    /// <see cref="ProcessMessageBytes"/> directly with the raw UTF-8
    /// bytes; this overload exists so existing test fixtures that
    /// hand-craft JSON strings (and the SignalkDelta-fixture replay
    /// path) keep working without each test having to encode to UTF-8.
    /// </summary>
    internal void ProcessMessage(string json)
    {
        // Encode once and reuse: avoids two passes (one for the byte
        // path, one for the OnRawMessage subscriber path) and keeps
        // the test surface a one-liner.
        ProcessMessageBytes(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>"self" key as UTF-8 literal. The receive loop scans the
    /// raw byte buffer for this only once per session (until
    /// <see cref="_selfContext"/> resolves), so the cost is negligible;
    /// pre-computing as a static UTF-8 byte array avoids re-encoding
    /// on every guarded check.</summary>
    private static readonly byte[] SelfKeyUtf8 = "\"self\""u8.ToArray();

    /// <summary>
    /// Hot path: invoked once per fully-assembled WebSocket frame on a
    /// live SignalK feed. Takes the raw UTF-8 bytes directly; the older
    /// shape decoded each frame to a UTF-16 string only to feed the
    /// reflection-based JSON deserializer. By keeping the bytes as a
    /// span and calling <c>JsonSerializer.Deserialize&lt;T&gt;(ReadOnlySpan&lt;byte&gt;)</c>,
    /// we skip the per-frame string allocation entirely on the
    /// (overwhelmingly common) case where no <see cref="OnRawMessage"/>
    /// subscriber is listening.
    /// </summary>
    internal void ProcessMessageBytes(ReadOnlySpan<byte> utf8Bytes)
    {
        Interlocked.Exchange(ref _lastMessageTicks, _time.GetUtcNow().UtcTicks);

        // OnRawMessage is the RawStream debug page subscription. It's
        // unsubscribed by default; only allocate the UTF-16 string when
        // someone is actually listening. Cache the materialised string
        // so we don't transcode twice if the hello-detection path also
        // needs it (rare; only fires until _selfContext resolves).
        string? raw = null;
        if (OnRawMessage is not null)
        {
            raw = Encoding.UTF8.GetString(utf8Bytes);
            OnRawMessage.Invoke(raw);
        }

        try
        {
            // Single parse: deserialize to SignalkDelta, then check for hello message.
            var delta = JsonSerializer.Deserialize<SignalkDelta>(utf8Bytes);

            // The hello message has no updates but contains "self" in the raw JSON.
            // SignalkDelta ignores unknown properties, so check if updates are present.
            if (delta?.Updates is null)
            {
                // Could be the hello message with "self" identifier.
                // Once we already know our self context (the hello message
                // arrives once per session, not once per non-update tick),
                // there's no point scanning every empty / unknown delta
                // for "self". The IndexOf scan walks the whole raw payload
                // and runs at message cadence; on a steady SK feed that
                // adds up to nontrivial CPU on the Pi. Comparing UTF-8
                // bytes against a UTF-8 literal keeps us off the string-
                // allocation path that the earlier String.Contains needed.
                if (string.IsNullOrEmpty(_selfContext)
                    && utf8Bytes.IndexOf(SelfKeyUtf8) >= 0)
                {
                    using var doc = JsonDocument.Parse(utf8Bytes.ToArray());
                    if (doc.RootElement.TryGetProperty("self", out var selfProp))
                    {
                        SetSelfContext(selfProp.GetString());
                    }
                }
                return;
            }

            // Own-boat identification. Own-boat data may arrive BEFORE the
            // hello message resolves _selfContext, or on servers that only
            // emit the URN form ("vessels.urn:mrn:imo:mmsi:..."). Match all
            // three shapes: empty, "vessels.self", and the normalised
            // _selfContext. SetSelfContext also retro-evicts any vessel
            // we stored in AIS before we knew who we were.
            bool isSelf = IsSelfContext(delta.Context);

            if (isSelf)
                ProcessSelfDelta(delta);
            else if (IsAtonContext(delta.Context))
                ProcessAtonDelta(delta);
            else
                ProcessAisDelta(delta);
        }
        catch (JsonException)
        {
            // Ignore malformed messages.
        }
        catch (Exception ex)
        {
            // Don't let a single bad message crash the receive loop.
            _logger.LogWarning(ex, "Error processing SignalK message");
        }
    }

    /// <summary>
    /// Normalises the self identifier supplied by the server's hello
    /// message and retro-evicts any vessel we may have stored in
    /// <see cref="AisStore"/> for that context before we knew about it.
    /// Servers supply self either as "vessels.urn:mrn:imo:mmsi:..." OR
    /// just "urn:mrn:imo:mmsi:..."; we store the prefixed form so
    /// equality checks against delta contexts (always prefixed) match.
    /// </summary>
    internal void SetSelfContext(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            _selfContext = "";
            return;
        }
        _selfContext = raw.StartsWith("vessels.", StringComparison.Ordinal)
            ? raw
            : "vessels." + raw;
        _logger.LogInformation("Self context: {Self}", _selfContext);

        // Retro-evict: if an AIS delta for this context arrived before
        // the hello (or from a server that never sends "vessels.self"),
        // we'll have own-boat sitting in the AIS list. Remove it and
        // blocklist so subsequent deltas can't re-add.
        _ais.Evict(_selfContext);
    }

    /// <summary>
    /// MMSI extracted from the self-context URN, or null when the
    /// server's hello hasn't resolved yet or the context doesn't carry
    /// a mmsi segment. Used by the map to fetch the country flag for
    /// the own-boat popup (same signalk-flags endpoint as AIS markers).
    /// </summary>
    public string? OwnMmsi
    {
        get
        {
            if (string.IsNullOrEmpty(_selfContext)) return null;
            return AisVessel.ExtractMmsi(_selfContext);
        }
    }

    internal bool IsSelfContext(string? context)
    {
        if (string.IsNullOrEmpty(context)) return true;
        if (context == "vessels.self") return true;
        if (string.IsNullOrEmpty(_selfContext)) return false;
        return context == _selfContext;
    }

    private void ProcessSelfDelta(SignalkDelta delta)
    {
        bool changed = false;

        foreach (var update in delta.Updates!)
        {
            _data.MarkDataReceived();

            if (update.Values is null)
                continue;

            foreach (var val in update.Values)
            {
                if (val.Path is null)
                    continue;

                // Mayara radar ARPA targets come in under vessels.self on
                // paths like radars.<rid>.targets.<tid>.position. Route
                // each target field into AisStore with a synthesised
                // radar.<rid>.<tid> context so the alarm pipeline treats
                // them uniformly with AIS vessels.
                if (val.Path.StartsWith("radars.", StringComparison.Ordinal))
                {
                    RouteRadarDelta(val.Path, val.Value);
                    continue;
                }

                if (val.Path == Utilities.SkPaths.Navigation.Position && val.Value is JsonElement posEl
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

                // Anchor position from signalk-anchoralarm-plugin. Three
                // delta shapes we observed from the plugin in the field:
                //   1. { lat, lon } object   -> drop/update anchor
                //   2. explicit JSON null    -> anchor weighed on another
                //      plotter ("Weigh Anchor" button in the plugin UI)
                //   3. value = null (not a JsonElement at all) -- same
                //      as case 2 but surfaced by some SK server versions
                //      as a property-missing rather than JSON null.
                // User-reported bug: anchor stayed on ONA after being
                // cleared in freeboard-sk because only case 1 was
                // handled. All three now clear our side.
                if (val.Path == Utilities.SkPaths.Navigation.Anchor.Position)
                {
                    if (val.Value is JsonElement anchorEl)
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
                    }
                    else if (val.Value is null)
                    {
                        _data.ClearAnchor();
                        changed = true;
                    }
                    continue;
                }

                // Plugins also deactivate by nulling maxRadius (the alarm
                // is only armed when both position + radius are set).
                // Treat a null maxRadius as "anchor no longer armed" too,
                // covering servers that don't re-emit a null position on
                // deactivation.
                if (val.Path == Utilities.SkPaths.Navigation.Anchor.MaxRadius && IsNullDelta(val.Value))
                {
                    _data.ClearAnchor();
                    changed = true;
                    continue;
                }

                // Route activation: signalk-server v2's built-in course
                // API publishes the activation as a SINGLE delta with
                // path = "navigation.course.activeRoute" and value =
                // { href, name, reverse, pointIndex, pointTotal }.
                // It does NOT emit the individual leaf paths (.href,
                // .name, .pointIndex, .pointTotal) separately. Before
                // this branch existed, OnaPlotter silently dropped the
                // activation delta (ApplyString only handles strings,
                // Apply only handles doubles, neither accepts objects)
                // so a route activated externally (freeboard, a second
                // plotter, a REST call) never showed up on OnA -- and
                // even ONA's own activate-then-draw path broke because
                // SyncActiveRouteAsync reads Data.ActiveRouteHref,
                // which stayed null. See the symmetric null-branch
                // further down for deactivation.
                if (val.Path == Utilities.SkPaths.Navigation.Course.ActiveRoute
                    && val.Value is JsonElement arEl
                    && arEl.ValueKind == JsonValueKind.Object)
                {
                    if (arEl.TryGetProperty("href", out var arHref)
                        && arHref.ValueKind == JsonValueKind.String)
                    {
                        _data.ApplyString(Utilities.SkPaths.Navigation.Course.ActiveRouteHref, arHref.GetString());
                    }
                    if (arEl.TryGetProperty("name", out var arName)
                        && arName.ValueKind == JsonValueKind.String)
                    {
                        _data.ApplyString(Utilities.SkPaths.Navigation.Course.ActiveRouteName, arName.GetString());
                    }
                    if (arEl.TryGetProperty("pointIndex", out var arIdx)
                        && arIdx.ValueKind == JsonValueKind.Number
                        && arIdx.TryGetDouble(out var idxD))
                    {
                        _data.Apply(Utilities.SkPaths.Navigation.Course.ActiveRoutePointIndex, idxD);
                    }
                    if (arEl.TryGetProperty("pointTotal", out var arTot)
                        && arTot.ValueKind == JsonValueKind.Number
                        && arTot.TryGetDouble(out var totD))
                    {
                        _data.Apply(Utilities.SkPaths.Navigation.Course.ActiveRoutePointTotal, totD);
                    }
                    changed = true;
                    continue;
                }

                // Course next-point: server can publish either the
                // parent object (with nested position) on a state
                // change OR the bare position leaf on updates. Handle
                // the parent-object form first so activation deltas
                // aren't dropped.
                if (val.Path == "navigation.course.nextPoint"
                    && val.Value is JsonElement npParentEl
                    && npParentEl.ValueKind == JsonValueKind.Object
                    && npParentEl.TryGetProperty("position", out var npParentPos)
                    && npParentPos.ValueKind == JsonValueKind.Object
                    && npParentPos.TryGetProperty("latitude", out var npPLat)
                    && npParentPos.TryGetProperty("longitude", out var npPLon)
                    && npPLat.ValueKind == JsonValueKind.Number
                    && npPLon.ValueKind == JsonValueKind.Number)
                {
                    _data.ApplyCourseNextPointPosition(npPLat.GetDouble(), npPLon.GetDouble());
                    changed = true;
                    continue;
                }

                // Course next-point position (lat/lon object). SK v2
                // built-in path; the legacy v1 courseGreatCircle /
                // courseRhumbline variants were dropped in the
                // tech-debt pass because the v2 surface has been
                // default on SK Node Server for years.
                if (val.Path == Utilities.SkPaths.Navigation.Course.NextPointPosition
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

                // Course previous-point parent-object form (same
                // activation-delta shape as nextPoint above).
                if (val.Path == "navigation.course.previousPoint"
                    && val.Value is JsonElement ppParentEl
                    && ppParentEl.ValueKind == JsonValueKind.Object
                    && ppParentEl.TryGetProperty("position", out var ppParentPos)
                    && ppParentPos.ValueKind == JsonValueKind.Object
                    && ppParentPos.TryGetProperty("latitude", out var ppPLat)
                    && ppParentPos.TryGetProperty("longitude", out var ppPLon)
                    && ppPLat.ValueKind == JsonValueKind.Number
                    && ppPLon.ValueKind == JsonValueKind.Number)
                {
                    _data.ApplyCoursePreviousPointPosition(ppPLat.GetDouble(), ppPLon.GetDouble());
                    changed = true;
                    continue;
                }

                // Course previous-point position. v2 only, for the
                // same reason as nextPoint.position above.
                if (val.Path == Utilities.SkPaths.Navigation.Course.PreviousPointPosition
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

                // Notification paths from the course-provider plugin
                // (notifications.navigation.course.perpendicularPassed /
                // arrivalCircleEntered). Value is {state, method,
                // message} when armed, JSON null when the plugin
                // clears the alarm on exit. MaybeAutoAdvanceWaypoint's
                // edge trigger fires on the armed->cleared cycle, so
                // map the "has a notification object" case to true
                // and everything else (null, cleared) to false.
                //
                // Logged at Info so a user who sees auto-advance fail
                // can grep the browser console for "CourseNotification"
                // and tell whether the notification is reaching us at
                // all (plumbing) or whether our edge / cooldown logic
                // is what skipped it (downstream).
                if (val.Path == "notifications.navigation.course.perpendicularPassed"
                    || val.Path == "notifications.navigation.course.arrivalCircleEntered")
                {
                    // Fail-safe: treat any non-"normal" state as armed, AND
                    // treat a notification object with a MISSING state
                    // property as armed too. Older course-provider builds
                    // sometimes emit the object without `state` when the
                    // notification is active; the stricter "state must be
                    // present and non-normal" interpretation silently
                    // dropped those. Better to nuisance-fire once than
                    // miss a genuine arrival signal.
                    bool armed = false;
                    if (val.Value is JsonElement ne && ne.ValueKind == JsonValueKind.Object)
                    {
                        if (ne.TryGetProperty("state", out var stateEl)
                            && stateEl.ValueKind == JsonValueKind.String)
                        {
                            var s = stateEl.GetString();
                            // Any state other than the explicit normal/null
                            // clearance words counts as armed. Unknown
                            // severities (alert/warn/alarm/emergency/
                            // whatever the plugin invents) fail safe as
                            // armed rather than being silently dropped.
                            armed = !string.Equals(s, "normal", StringComparison.Ordinal)
                                 && !string.Equals(s, "cleared", StringComparison.Ordinal);
                        }
                        else
                        {
                            // Object present but no `state` -- treat as armed.
                            armed = true;
                        }
                    }
                    else if (val.Value is JsonElement boolEl2
                        && boolEl2.ValueKind == JsonValueKind.True)
                    {
                        // Bare bool `true` form (some v3 plugin builds).
                        armed = true;
                    }
                    _logger.LogInformation("CourseNotification {Path} armed={Armed}", val.Path, armed);
                    _data.ApplyBool(val.Path, armed);
                    changed = true;
                    continue;
                }

                // Generic server-side SignalK notifications. Anything
                // under "notifications.*" that we didn't already
                // intercept above (the course-specific flags use a
                // dedicated NavigationData boolean path for
                // auto-advance) lands in the ServerNotificationStore
                // so the ServerNotificationsAlarmRule can surface it
                // in the banner stack on the next Evaluate tick.
                if (val.Path.StartsWith("notifications.", StringComparison.Ordinal))
                {
                    if (RouteServerNotification(val.Path, val.Value))
                        changed = true;
                    continue;
                }

                // Plain boolean delta (legacy / non-standard plugin
                // builds that publish booleans directly). ApplyBool
                // returns false for unknown paths so random bool
                // fields don't bind anywhere unexpected.
                if (val.Value is JsonElement boolEl
                    && (boolEl.ValueKind == JsonValueKind.True || boolEl.ValueKind == JsonValueKind.False))
                {
                    if (_data.ApplyBool(val.Path, boolEl.GetBoolean()))
                    {
                        changed = true;
                        continue;
                    }
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

                // Route deactivation: after DELETE /navigation/course
                // the server publishes one of two delta shapes depending
                // on version, and the client has to cope with BOTH:
                //
                //   (a) navigation.course.activeRoute.href = null
                //       -- older course-provider-plugin; the specific
                //       leaf path flips to null.
                //   (b) navigation.course.activeRoute = null
                //       OR navigation.course.nextPoint = null
                //       -- signalk-server 2.x's built-in course API
                //       nulls the PARENT object in one delta instead
                //       of enumerating every leaf. We must treat a null
                //       parent the same as nulling every child, otherwise
                //       course state stays stale until a fresh route is
                //       set. Freeboard does the same (its processCourseData
                //       treats a null value as "clear everything").
                if (val.Path == Utilities.SkPaths.Navigation.Course.ActiveRouteHref
                    || val.Path == Utilities.SkPaths.Navigation.Course.ActiveRoute
                    || val.Path == Utilities.SkPaths.Navigation.Course.NextPoint)
                {
                    bool isNull = val.Value is null
                        || (val.Value is JsonElement nel && nel.ValueKind == JsonValueKind.Null);
                    if (isNull)
                    {
                        _data.ClearCourse();
                        changed = true;
                        continue;
                    }
                }

                if (_data.Apply(val.Path, val.Value))
                    changed = true;
            }
        }

        if (changed)
        {
            if (_data.Latitude is not null && _data.Longitude is not null
                && ShouldSampleTrackPoint())
            {
                _track.Add(new TrackPoint(
                    _time.GetUtcNow().UtcDateTime,
                    _data.Latitude.Value,
                    _data.Longitude.Value,
                    _data.SpeedOverGround,
                    _data.CourseOverGround,
                    _data.Heading,
                    _data.WindAngleApparent,
                    _data.WindSpeedApparent,
                    _data.WindAngleTrue,
                    _data.WindSpeedTrue,
                    _data.Depth));
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

    /// <summary>True when the delta context is an AtoN context. SignalK
    /// publishes Aids to Navigation under <c>atons.urn:mrn:imo:mmsi:NNN</c>
    /// (and <c>shore.basestations.*</c> by extension, which a future
    /// commit may route here too). Same dispatch site as the AIS branch
    /// in ProcessMessage; an AtoN context never matches IsSelfContext
    /// because we only ever set <c>_selfContext</c> from a vessels.* URN.</summary>
    internal static bool IsAtonContext(string? context)
    {
        if (string.IsNullOrEmpty(context)) return false;
        return context.StartsWith("atons.", StringComparison.Ordinal);
    }

    private void ProcessAtonDelta(SignalkDelta delta)
    {
        foreach (var update in delta.Updates!)
        {
            if (update.Values is null) continue;
            foreach (var val in update.Values)
            {
                if (val.Path is null) continue;
                _atons.Apply(delta.Context!, val.Path, val.Value);
            }
        }
    }

    /// <summary>
    /// Routes a radar-target delta into <see cref="AisStore"/>. Two
    /// wire shapes seen in the wild, both handled:
    ///
    /// <list type="number">
    ///   <item>Spec (v3.1): <c>radars.&lt;rid&gt;.targets.&lt;tid&gt;</c>
    ///     with the whole <see cref="Models.RadarArpaTarget"/> as
    ///     value. <c>value: null</c> signals target deletion.</item>
    ///   <item>Legacy (early Mayara):
    ///     <c>radars.&lt;rid&gt;.targets.&lt;tid&gt;.&lt;field&gt;</c>
    ///     with per-field values (position / course / speed).
    ///     Handled by dispatching each recognised field.</item>
    /// </list>
    ///
    /// Malformed / unknown shapes drop silently -- the receive loop
    /// must stay alive no matter what the server emits.
    /// </summary>
    private void RouteRadarDelta(string path, object? value)
    {
        if (TryParseRadarTargetPath(path) is not var (radarId, targetId, field)) return;
        string ctx = $"{AisStore.RadarContextPrefix}{radarId}.{targetId}";

        // Spec shape: value is the whole target object (or null for
        // deletion). Field is null because the path stops at target id.
        if (field is null)
        {
            if (value is null
                || (value is JsonElement nullEl && nullEl.ValueKind == JsonValueKind.Null))
            {
                _ais.RemoveContext(ctx);
                return;
            }
            if (value is JsonElement targetEl && targetEl.ValueKind == JsonValueKind.Object)
            {
                // Translate into the field-keyed shape AisVessel.Apply
                // understands. We emit position first so the vessel has
                // coordinates on the first delta; motion fields follow
                // and update what's already in the store.
                if (targetEl.TryGetProperty("position", out var pos)
                    && pos.ValueKind == JsonValueKind.Object)
                {
                    _ais.Apply(ctx, "position", pos);
                }
                if (targetEl.TryGetProperty("motion", out var motion)
                    && motion.ValueKind == JsonValueKind.Object)
                {
                    if (motion.TryGetProperty("course", out var cog)
                        && cog.ValueKind == JsonValueKind.Number)
                        _ais.Apply(ctx, "course", cog);
                    if (motion.TryGetProperty("speed", out var sog)
                        && sog.ValueKind == JsonValueKind.Number)
                        _ais.Apply(ctx, "speed", sog);
                }
            }
            return;
        }

        // Legacy shape: value is the per-field primitive; pass through.
        _ais.Apply(ctx, field, value);
    }

    /// <summary>
    /// Parses a radar-target delta path. Accepts both
    /// <c>radars.&lt;rid&gt;.targets.&lt;tid&gt;</c> (spec; field = null)
    /// and <c>radars.&lt;rid&gt;.targets.&lt;tid&gt;.&lt;field&gt;</c>
    /// (legacy). Splits on the literal <c>.targets.</c> separator so
    /// radar ids containing dots (IPv4-style hardware identifiers)
    /// still parse. Returns <c>null</c> for anything that doesn't
    /// match either shape.
    /// </summary>
    internal static (string radarId, string targetId, string? field)? TryParseRadarTargetPath(string path)
    {
        if (!path.StartsWith("radars.", StringComparison.Ordinal)) return null;

        const string sep = ".targets.";
        int sepIdx = path.IndexOf(sep, StringComparison.Ordinal);
        if (sepIdx < 0) return null;

        string radarId = path.Substring("radars.".Length, sepIdx - "radars.".Length);
        if (radarId.Length == 0) return null;

        string after = path[(sepIdx + sep.Length)..];
        if (after.Length == 0) return null;

        int dot = after.IndexOf('.');
        if (dot < 0)
        {
            // Spec shape: path stops at the target id; value carries
            // the whole target object (or null for delete).
            return (radarId, after, null);
        }
        if (dot == 0 || dot == after.Length - 1) return null;

        string targetId = after[..dot];
        string field = after[(dot + 1)..];
        return (radarId, targetId, field);
    }

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
    /// additions. Used by RawStream to populate its chip list -- the
    /// page previously showed the server's full discoverable paths,
    /// which was noise when the plotter only ingests a narrow slice.
    ///
    /// Returns a fresh snapshot each call; Blazor WASM is single-
    /// threaded so a mid-enumeration mutation isn't possible, but
    /// handing out a copy keeps callers from accidentally relying on
    /// live-update semantics.
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

        var message = JsonSerializer.Serialize(new
        {
            context,
            subscribe = paths.Select(p => new
            {
                path = p,
                period = periodMs,
                policy,
            })
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
            var ct = _cts?.Token ?? CancellationToken.None;
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // Test-facing shim for SignalkClientCourseTests, which still drives
    // the seed via this entry point with a mock HttpClient. Delegates
    // to SignalkCourseSeeder so the production path stays consolidated.
    internal Task SeedSelfCourseFromRestAsync(CancellationToken ct) =>
        _courseSeeder.SeedAsync(ct);

    /// <summary>
    /// Routes a notifications.* delta into the
    /// <see cref="ServerNotifications.ServerNotificationStore"/>. The
    /// SignalK shape is one of:
    /// <list type="bullet">
    /// <item>Object: <c>{ "state": "alarm"|"warn"|..., "method": [...], "message": "..." }</c> (armed)</item>
    /// <item>JSON null OR object with state="normal" (cleared)</item>
    /// </list>
    /// Unknown shapes (bare booleans, strings) fail safe to a clear --
    /// no point flapping the alarm stack on a malformed message.
    /// Returns true when the store changed (caller fires OnDataChanged
    /// so AlarmManager re-evaluates promptly).
    /// </summary>
    private bool RouteServerNotification(string path, object? rawValue)
    {
        // JSON null on the value: server cleared the notification.
        if (IsNullDelta(rawValue))
        {
            return _serverNotifs.Clear(path);
        }

        // Anything that isn't a JsonElement at this point is unexpected
        // (the parser hands us JsonElement consistently). Treat as a
        // clear rather than an arm so we don't manufacture an alarm
        // out of garbage.
        if (rawValue is not JsonElement el)
        {
            return _serverNotifs.Clear(path);
        }

        // Object form: pull state + message. Missing state on a
        // present object treats as "alarm" (most plugins emit only
        // object-when-armed, omit-state-when-they-mean-it; safer to
        // surface than to drop).
        //
        // SignalK v2 (≥ 2.21) enriches every notification value with
        // `id` (stable UUID) and `status` (silenced / acknowledged /
        // canSilence / canAcknowledge / canClear). Both are optional;
        // we read them when present and store them on the
        // ServerNotification so the alarm pipeline can drive
        // server-side ack and the banner can hide the Acknowledge
        // button when the server's PGN-derived flags say "no".
        if (el.ValueKind == JsonValueKind.Object)
        {
            string? state = null;
            string? message = null;
            string? id = null;
            OnaPlotter.Services.ServerNotifications.NotificationStatus? status = null;
            if (el.TryGetProperty("state", out var stateEl)
                && stateEl.ValueKind == JsonValueKind.String)
            {
                state = stateEl.GetString();
            }
            if (el.TryGetProperty("message", out var msgEl)
                && msgEl.ValueKind == JsonValueKind.String)
            {
                message = msgEl.GetString();
            }
            if (el.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.String)
            {
                id = idEl.GetString();
            }
            if (el.TryGetProperty("status", out var statusEl)
                && statusEl.ValueKind == JsonValueKind.Object)
            {
                status = ParseNotificationStatus(statusEl);
            }
            // Missing-state-on-an-object: assume armed at "alarm"
            // severity. Clears require an explicit normal/cleared
            // string OR a JSON null payload.
            state ??= "alarm";
            return _serverNotifs.Apply(path, state, message, id, status);
        }

        // Bare bool true: rare legacy form ("we have a notification");
        // treat as armed. Anything else (numbers, strings, arrays):
        // clear, so a malformed delta doesn't latch.
        if (el.ValueKind == JsonValueKind.True)
        {
            return _serverNotifs.Apply(path, "alarm", null);
        }
        return _serverNotifs.Clear(path);
    }

    /// <summary>Parses a SignalK v2 notification <c>status</c> object.
    /// Every field defaults to false on a missing / non-bool entry --
    /// fail-safe semantics ("if I can't tell, assume the action is
    /// unsupported"). The block is optional in the wire shape; the
    /// caller only invokes us when an Object kind is actually present.</summary>
    private static OnaPlotter.Services.ServerNotifications.NotificationStatus ParseNotificationStatus(JsonElement el)
    {
        return new OnaPlotter.Services.ServerNotifications.NotificationStatus(
            Silenced: ReadBool(el, "silenced"),
            Acknowledged: ReadBool(el, "acknowledged"),
            CanSilence: ReadBool(el, "canSilence"),
            CanAcknowledge: ReadBool(el, "canAcknowledge"),
            CanClear: ReadBool(el, "canClear"));

        static bool ReadBool(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var v)) return false;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => false,
            };
        }
    }

    // True if the value in a SignalK delta represents "no data here" --
    // either a literal JSON null, or a C# null deserialized as such.
    // Used by anchor / course deactivation handlers to treat both wire
    // shapes uniformly.
    private static bool IsNullDelta(object? value)
    {
        if (value is null) return true;
        if (value is JsonElement el && el.ValueKind == JsonValueKind.Null) return true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        // Symmetric unsubscribe to the constructor's `+= OnSettingsChangedSync`.
        // Singleton+singleton lifetime in production means the GC sees both
        // go down together so the leak is benign there, but a test that
        // shares a settings stub across multiple SignalkClient instances
        // would otherwise see handlers accumulate on the settings object.
        _settings.OnSettingsChanged -= OnSettingsChangedSync;

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

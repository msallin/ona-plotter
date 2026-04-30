using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using OnaPlotter.Components;
using OnaPlotter.Services;
using OnaPlotter.Services.Api;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Shared HttpClient used by every *Api.
builder.Services.AddSingleton(new HttpClient());

// Storage + settings.
builder.Services.AddSingleton<IKeyValueStore, LocalStorageKeyValueStore>();
builder.Services.AddSingleton<IAppSettings, AppSettingsService>();
// Detected once per session (navigator.hardwareConcurrency + UA).
// Drives renderer / tile-prefetch decisions; no user-facing knob.
builder.Services.AddSingleton<IClientCapabilities, ClientCapabilitiesService>();

// Domain state.
builder.Services.AddSingleton<TrackBuffer>();
builder.Services.AddSingleton<AisStore>();

// UI services.
builder.Services.AddSingleton<IToastService, ToastService>();
builder.Services.AddSingleton<IConfirmationService, ConfirmationService>();
builder.Services.AddSingleton<IPolarService, PolarService>();
// Polls the server's version.g.js every 5 min and surfaces a
// "Reload to update" chip when the published bundle hash changes.
// The check fires fire-and-forget; a 404 / offline / timeout is
// swallowed and retried on the next tick.
builder.Services.AddSingleton<OnaPlotter.Services.UpdateChecker>();
// Page-state caches: keep the helm's last range / loaded data on
// History + Stats so navigating to /map and back doesn't reset
// the view. Singleton-scoped (one per circuit); cleared on a
// hard reload.
builder.Services.AddSingleton<OnaPlotter.Services.State.StatsPageState>();
builder.Services.AddSingleton<OnaPlotter.Services.State.HistoryPageState>();
// Server-side SignalK notifications store. SignalkClient pushes
// notifications.* deltas here; the rule below reads the active set
// every Evaluate tick and surfaces each as an alarm-banner entry.
builder.Services.AddSingleton<OnaPlotter.Services.ServerNotifications.ServerNotificationStore>();
// AIS Aids to Navigation. atons.* deltas land here; the map
// renderer subscribes to OnAtonsUpdated and pushes the snapshot
// to Leaflet. Static enough to live on a 60s subscription tier.
builder.Services.AddSingleton<OnaPlotter.Services.AtonStore>();
// Alarm rules are DI-registered; AlarmManager picks them up via
// IEnumerable<IAlarmRule>. Adding a new rule is a one-line registration.
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.AisSartAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.ShallowAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.AnchorTideAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.AnchorDragAlarmRule>();
// Shared moored-vessel classifier. Singleton so CpaAlarmRule and the
// Harbor-mode filter in Map.razor.PushAisTargets see the same dwell
// state and the same SK navigation.state interpretation.
builder.Services.AddSingleton<OnaPlotter.Services.IMooredVesselTracker, OnaPlotter.Services.MooredVesselTracker>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.CpaAlarmRule>();
// Region store is a thin singleton AisStore-shaped: Map.razor pushes
// regions in on every resource refresh; HazardousRegionAlarmRule reads
// the snapshot per tick. Threaded as a separate dependency rather than
// added to AlarmEvaluationContext so the context shape stays minimal
// for every other rule that doesn't care about regions.
builder.Services.AddSingleton<OnaPlotter.Services.IRegionStore, OnaPlotter.Services.RegionStore>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.HazardousRegionAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.WindShiftAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.WaypointApproachAlarmRule>();
// Bridge rule needs the v2 notifications API (to attach an
// IAlarmAcknowledger to each AlarmInfo it emits) and the published-
// alarms tracker (to skip our own publication echoes). Both are
// optional on the rule's ctor for backward compat with legacy test
// constructors that don't wire them; production DI threads them in.
builder.Services.AddSingleton<IAlarmRule>(sp =>
    new OnaPlotter.Services.Alarms.ServerNotificationsAlarmRule(
        sp.GetRequiredService<OnaPlotter.Services.ServerNotifications.ServerNotificationStore>(),
        sp.GetRequiredService<OnaPlotter.Services.Api.INotificationsApi>(),
        sp.GetRequiredService<OnaPlotter.Services.Alarms.IPublishedAlarmTracker>()));
builder.Services.AddSingleton<DeadmanTracker>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.DeadmanAlarmRule>();
builder.Services.AddSingleton<IAlarmManager, AlarmManager>();
// Phase B: cross-plotter publish. Tracker is the read-side that the
// bridge rule queries to avoid echoing our own publications back into
// the banner; AlarmPublisher is the writer that POSTs raises/clears
// to the SK server when the local alarm stack changes. Resolved
// post-build so subscription wiring happens once at startup.
builder.Services.AddSingleton<OnaPlotter.Services.Alarms.PublishedAlarmTracker>();
builder.Services.AddSingleton<OnaPlotter.Services.Alarms.IPublishedAlarmTracker>(
    sp => sp.GetRequiredService<OnaPlotter.Services.Alarms.PublishedAlarmTracker>());
builder.Services.AddSingleton<OnaPlotter.Services.Alarms.AlarmPublisher>();

// SignalK REST API clients (one per concern).
builder.Services.AddSingleton<ISignalKBaseUrl, SignalKBaseUrl>();
builder.Services.AddSingleton<IChartApi, ChartApi>();
builder.Services.AddSingleton<IRouteApi, RouteApi>();
builder.Services.AddSingleton<IWaypointApi, WaypointApi>();
builder.Services.AddSingleton<INoteApi, NoteApi>();
builder.Services.AddSingleton<IRegionApi, RegionApi>();
builder.Services.AddSingleton<ICourseApi, CourseApi>();
// SignalK v2 notifications client. Drives cross-plotter alarm sync
// via Acknowledge / Silence and (Phase B) lets client-side rules
// publish their alarms back to SK so other plotters can see them.
builder.Services.AddSingleton<INotificationsApi, NotificationsApi>();
builder.Services.AddSingleton<IAutopilotApi, AutopilotApi>();
// Optional: signalk-anchoralarm-plugin. Endpoint 404s when the plugin
// isn't installed; the map surfaces that as a toast rather than failing
// the app startup.
builder.Services.AddSingleton<IAnchorAlarmApi, AnchorAlarmApi>();
builder.Services.AddSingleton<IPathApi, PathApi>();
builder.Services.AddSingleton<ITrackApi, TrackApi>();
// Signal K Radar API v3.1. Optional; empty list when no provider plugin.
builder.Services.AddSingleton<IRadarApi, RadarApi>();
// Radar overlay: host wraps the JS module; manager owns session
// state (capabilities cache, sticky-off prefs, last-range cache,
// auto-toggle decisions). The page injects the manager and calls
// into it; the host's module ref is wired by the page after the
// JS module loads.
builder.Services.AddSingleton<OnaPlotter.Services.Radar.JsRadarOverlayHost>();
builder.Services.AddSingleton<OnaPlotter.Services.Radar.IRadarOverlayHost>(
    sp => sp.GetRequiredService<OnaPlotter.Services.Radar.JsRadarOverlayHost>());
builder.Services.AddSingleton<OnaPlotter.Services.Radar.RadarOverlayManager>();
// Optional: detects sbender9/signalk-buddylist-plugin at runtime.
builder.Services.AddSingleton<IBuddyListApi, BuddyListApi>();

// Wall-clock provider. Stale-data detection and track-point timestamps go
// through this so tests can advance time deterministically (FakeTimeProvider
// from Microsoft.Extensions.TimeProvider.Testing) instead of sleeping.
builder.Services.AddSingleton(TimeProvider.System);

// SignalK WebSocket delta stream.
builder.Services.AddSingleton<SignalkClient>();

// Client-error relay to the SignalK plugin's /log endpoint. Makes
// iPad Safari exceptions visible in the SignalK server log for
// SSH-based debugging at the helm. The actual install happens in
// wwwroot/js/errorRelayBoot.js (loaded synchronously from index.html
// BEFORE the Blazor runtime, so it catches boot-time exceptions);
// this DI singleton is the C# entry-point for code that wants to
// relay a caught exception explicitly. See Services/ClientErrorRelay.cs.
builder.Services.AddSingleton<ClientErrorRelay>();

var host = builder.Build();

// Kick off the WebSocket loop (no IHostedService in Blazor WASM).
var signalkClient = host.Services.GetRequiredService<SignalkClient>();
_ = signalkClient.StartAsync();

// Activate the cross-plotter alarm publisher. Resolving the singleton
// runs the constructor which subscribes to IAlarmManager.OnAlarmsChanged;
// without this line the type would never be instantiated (no other
// component injects it -- the bridge rule injects the tracker, not
// the publisher) and locally-emitted alarms would never reach other
// plotters. Stashed in a discard so the GC keeps the subscription alive.
_ = host.Services.GetRequiredService<OnaPlotter.Services.Alarms.AlarmPublisher>();

await host.RunAsync();

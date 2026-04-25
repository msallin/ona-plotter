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

// Domain state.
builder.Services.AddSingleton<TrackBuffer>();
builder.Services.AddSingleton<AisStore>();

// UI services.
builder.Services.AddSingleton<IToastService, ToastService>();
builder.Services.AddSingleton<IConfirmationService, ConfirmationService>();
builder.Services.AddSingleton<IPolarService, PolarService>();
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
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.CpaAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.WindShiftAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.WaypointApproachAlarmRule>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.ServerNotificationsAlarmRule>();
builder.Services.AddSingleton<DeadmanTracker>();
builder.Services.AddSingleton<IAlarmRule, OnaPlotter.Services.Alarms.DeadmanAlarmRule>();
builder.Services.AddSingleton<IAlarmManager, AlarmManager>();

// SignalK REST API clients (one per concern).
builder.Services.AddSingleton<ISignalKBaseUrl, SignalKBaseUrl>();
builder.Services.AddSingleton<IChartApi, ChartApi>();
builder.Services.AddSingleton<IRouteApi, RouteApi>();
builder.Services.AddSingleton<IWaypointApi, WaypointApi>();
builder.Services.AddSingleton<INoteApi, NoteApi>();
builder.Services.AddSingleton<IRegionApi, RegionApi>();
builder.Services.AddSingleton<ICourseApi, CourseApi>();
builder.Services.AddSingleton<IAutopilotApi, AutopilotApi>();
// Optional: signalk-anchoralarm-plugin. Endpoint 404s when the plugin
// isn't installed; the map surfaces that as a toast rather than failing
// the app startup.
builder.Services.AddSingleton<IAnchorAlarmApi, AnchorAlarmApi>();
builder.Services.AddSingleton<IPathApi, PathApi>();
builder.Services.AddSingleton<ITrackApi, TrackApi>();
// Signal K Radar API v3.1. Optional; empty list when no provider plugin.
builder.Services.AddSingleton<IRadarApi, RadarApi>();
// Optional: detects sbender9/signalk-buddylist-plugin at runtime.
builder.Services.AddSingleton<IBuddyListApi, BuddyListApi>();

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

await host.RunAsync();

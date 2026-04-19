# agents.md

Notes for future agents. Keep this short; grow it only when something
bit you.

## What this is

Blazor WebAssembly chartplotter for SignalK servers. Targets an iPad
in the cockpit, a phone in a jacket pocket, and a 21" helm touchscreen.
Deploys as a SignalK webapp on a Pi. .NET 10, net10.0 target, no
server-side Blazor.

## Commands

```bash
dotnet build                                             # root builds everything
dotnet run --project OnaPlotter.Tests --no-build         # C# + bUnit (~360 tests)
node --test OnaPlotter/wwwroot/js/geoMath.test.js        # JS math tests
```

CI runs both via `.github/workflows/ci.yml`. `dotnet test` is NOT the
path -- .NET 10 SDK removed the VSTest dispatch and we run TUnit as
an executable via `dotnet run`.

## Priorities (from the operator)

1. Stability & functional correctness -- **rock solid first**.
2. Features second. Any new code lands with defensive null checks,
   swallowed exceptions at the JS-interop boundary, and tests that
   pin the correctness contract.
3. World-class plotter UX, measured against B&G Vulcan / Raymarine
   Axiom / Garmin GPSMAP / Aqua Map, not against Freeboard-SK.

## Layout

- `OnaPlotter/Components/Pages/*.razor` -- top-level pages (Map,
  Dashboard, Gauges, SailSteer, WindRose, RawStream, History, Settings).
- `OnaPlotter/Components/Map/*` -- Map sub-components: `MapHud`,
  `MapControls`, `LayersPanel`, `RouteEditPanel`, `MapShortcutsOverlay`,
  `LegendOverlay`.
- `OnaPlotter/Components/Map/Layers/*Section.razor` -- per-resource
  rows in the Layers panel (Charts / Routes / Waypoints / Notes /
  Regions / Vessels / Buddies / Weather / Legend / TrackHistory).
- `OnaPlotter/Components/Pages/Map.razor.cs` -- code-behind partial
  class. Resource CRUD + weather routing + NearestWP lives there
  so Map.razor stays scannable.
- `OnaPlotter/Services/Api/*Api.cs` -- one HTTP client per SignalK
  resource. Share `ResourceHttp` for GET-dict + DELETE plumbing.
- `OnaPlotter/Services/Alarms/*AlarmRule.cs` -- rule-per-file,
  DI-registered, `IAlarmRule.Check(ctx) -> AlarmInfo?`. New types
  go here; AlarmManager picks them up via `IEnumerable<IAlarmRule>`.
- `OnaPlotter/Utilities/*.cs` -- pure math / classifiers: `Cpa`,
  `Colregs`, `IsochroneRouter`, `AisPalette`, `AisSart`, `Format`.
- `OnaPlotter/wwwroot/js/leafletInterop.js` -- the JS side. 2000+
  lines. Five `MarkerLayer` instances manage chart / route /
  waypoint / note / region lifecycle. Keep logic in C# unless
  inside a synchronous Leaflet event handler.

## Patterns that matter

- **Resource CRUD payload shape**: `{ name, feature: { type, geometry,
  properties: { ... } } }`. The `properties` object is mandatory for
  Freeboard-SK interop -- waypoints / routes / regions all need it
  even if it's empty-ish. Notes are the odd one out; they use a bare
  `position: { latitude, longitude }` instead.
- **C# owns classification**. Ship-type colour, SART category,
  glyph bucket, CPA/TCPA -- all computed in C# and pushed to JS as
  resolved values on the vessel payload. JS is a renderer.
- **IAlarmRule** is the extension point for alarms. Priority is an
  int; the stack order is `(severity desc, TTI asc, priority asc)`.
  Set `AlarmInfo.TimeToEventMinutes` when the rule knows when the
  event happens (0 = now).
- **KV persistence** via `IKeyValueStore`. Everything survives-a-
  reload goes through this: `AppSettings*`, `alarmSnoozes.v1`,
  `hints.welcome.v1.dismissed`, polar CSV. Keys are versioned
  (e.g. `.v1` suffix) so schema changes can migrate cleanly.
- **MarkerLayer class** in JS wraps a by-id dict of Leaflet layers
  with `has/get/set/remove/clear`. Use it for every new resource
  type; do not hand-roll another `{}` + `delete` dict.
- **Welcome + touch coachmark + first-run offline toast** are three
  different signals; they all gate on their own KV key. Don't collapse
  them into one.

## UX principles

- Cruise vs Race modes are a light preset. Mode does not lock out
  features; it picks sensible defaults and surfaces mode-specific
  overlays (Race HUD, optimal TWA). Individual toggles still work in
  either mode.
- The palette has **three families**:
  - **Chart data** (AIS, radar, charts) -- warm earth palette. Warm
    tones read against blue water tiles.
  - **User annotations** (routes, waypoints, notes, regions) -- amber
    family, one hue with lightness variation. Shape carries the type,
    not hue.
  - **Alarms / status** -- strict traffic-light via `--sev-ok /
    --sev-warn / --sev-danger` tokens in `app.css :root`. Reference
    those, never hardcode red or green.
- **Own-boat magenta** is intentionally outside the palette. It
  means "this is you".
- Touch targets: 44px min on destructive actions under
  `@media (pointer: coarse)`. 36-40px is fine for informational chips.
- Night mode has 3 presets (Soft / Amber / Red). The `hue-rotate`
  filter on `article svg` applies only to the instrument pages --
  the `.leaflet-container svg` opt-out preserves AIS palette meaning
  after dark.

## Testing

- C# unit + bUnit in `OnaPlotter.Tests`. `FakeSettings` is the shared
  IAppSettings stub -- use it, don't re-declare the interface surface.
- Alarm rules get targeted tests (one file per rule with branch
  coverage for each early-return path).
- Playwright actions/fuzz/smoke in `OnaPlotter.UiTests`. None require
  a running SignalK server; they test UI-only flows.
- No Stryker yet. Consider it once coverage drifts under 80%.
- Every feature that round-trips through SignalK needs a test that
  pins the POST payload shape against the Freeboard-compatible
  format. See `WaypointApi.CreateAsync` -> `WaypointCourseApiTests`.

## When you're writing code

- `sealed` by default. There's no inheritance hierarchy to speak of.
- `async void` only on Blazor event handlers. Wrap the body in
  `try/catch (ObjectDisposedException)` because fast page switches
  tear down the SynchronizationContext mid-render.
- Every `module.InvokeVoidAsync` call site catches `JSDisconnectedException`.
- Don't introduce new raw `localStorage.*` JS calls. Everything goes
  through IKeyValueStore on the C# side.
- When in doubt, defensively null-check. Sailors run the app on
  flaky wifi with half-configured plugins; any exception that reaches
  Blazor's error UI is a user-visible failure.

## Razor binding gotcha

`Param="var"` on a **string**-typed parameter is a literal, not an
expression. Blazor doesn't scan enclosing C# for an identifier named
"var". Always prefix with `@` for string parameters:

```razor
<RouteEditPanel Name="@routeEditName" />     @* correct *@
<RouteEditPanel Name="routeEditName" />      @* passes the 14-char literal *@
```

Typed (`bool` / `int` / collection) parameters don't have this trap --
the type mismatch forces expression interpretation. This bit us on
`LayerFilter="LayerFilter"` and `Name="routeEditName"`; the fields
showed those strings as their default value.

## Currently open

- Polygon regions via freeform drawing (circles + server polygons
  work; no drawing UX yet)
- Mode-specific default presets (Race auto-enables laylines, etc.)
- Offline tile / MBTiles chart download
- Auto-routing around land / into the wind is gated on vector charts.
  We only have raster PNG tiles today; S-57 vector parsing plus a
  coastline rasteriser would need to land first. Big-enough feature
  that it should be planned deliberately, not scoped in here.
- Vessel-name display: REST snapshot now seeds names for already-known
  vessels on connect. Still need external-lookup fallback for vessels
  that have never sent AIS msg 5/24.
- Click-to-expand on the four corner HUD panels to surface more
  detail (VMG, signed AWA/TWA, HDG mag vs true, depth offset). Scoped
  but not implemented; requires a per-panel expanded-state model and
  CSS. Candidate: a single `expandedHud` string in Map.razor and a
  `hud-panel-expanded` class with extra rows.
- Light theme polish. Only the outer chrome (sidebar, top-row, page
  background) flips when theme=Light; most panels (chart-panel, HUD,
  alarm banners, dialogs) have hardcoded dark backgrounds so the
  result looks jarring. Needs a systemic pass to thread `--sk-surface`
  through every `rgba(15,17,22,...)` site.

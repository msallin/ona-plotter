# OnaPlotter

A touch-first chartplotter for sailors on a [SignalK](https://signalk.org/) boat.
One app for a 21" helm touchscreen, an iPad in the cockpit, or a phone in a
jacket pocket — same data, responsive layout, WCAG-sized tap targets.

Built on Blazor WebAssembly + Leaflet + the SignalK stream. Deploys as a SignalK
webapp on a Pi, or runs standalone from any browser.

---

## Part 1 — For sailors

Skip to [Part 2](#part-2--for-developers) if you're here to build or contribute.

### Why this and not another plotter

- **Freeboard-SK interop.** Routes, waypoints, notes, and regions round-trip
  through the SignalK `/resources/*` API with the exact shape Freeboard expects.
  Create something here, it shows up there, and vice-versa. Tests pin the
  payload shape so it can't drift.
- **Proper collision detection.** CPA / TCPA projection with COLREGS crossing
  classification, not a proximity beeper. Moored-vessel auto-mute, per-target
  snooze, pulsing danger ring, red/amber crossing lines.
  ([deep dive](docs/collision-detection.md))
- **Plugin-aware, not plugin-dependent.** Tide, buddy, anchor-alarm, Mayara
  radar — each lights up its own UI if installed, stays silent if not.
- **Touch-first.** Long-press context menu, bottom-sheet panels on phones,
  first-run coachmark, 44 px minimum tap targets, keyboard parity for
  everything.

### Install on a SignalK server

The common case: SignalK running on a Pi, OnaPlotter as a webapp next to it.

```powershell
pwsh ./deploy/deploy.ps1            # publish + scp to pi@openplotter.local
pwsh ./deploy/deploy.ps1 -SkipBuild # reuse the last publish output
```

The script wipes `obj/Release` + `bin/Release` (sidesteps stale-AOT), patches
`<base href>` to the webapp path, and SCPs into
`~/.signalk/node_modules/signalk-onaplotter/` on the target host. Restart
SignalK, open the webapps page, pick OnaPlotter.

Target host and user live at the top of the script. Non-PowerShell equivalent:

```bash
dotnet publish -c Release OnaPlotter/OnaPlotter.csproj
scp -r bin/Release/net10.0/publish/wwwroot/* \
    user@host:~/.signalk/node_modules/signalk-onaplotter/
```

### The Map page

The primary view. Features are grouped by what you're doing when you reach for
them.

#### While sailing

- **Four-corner HUD.** SOG/COG/position, wind (AWA + TWA with direction
  arrows), depth + tide countdown, heading compass. Click any corner to
  expand — DMS + VMG + XTE + drift, PT/STB wind labels + TWD + Beaufort,
  tide station + tidal current, COG + autopilot state. Enter / Space / Esc
  for keyboard users.
- **Own-vessel track**, speed-coloured, with a magenta chevron that stands
  out over blue water.
- **AIS targets.** Ship-type-coloured triangles with COG vector and a
  60-second fading trail. Names are seeded from the REST snapshot on connect
  and kept current via the delta stream. Small type glyphs
  (diamond / net / dot / plus) so deuteranopes can still tell sail from
  fishing from commercial.
- **CPA danger pulse.** Targets whose CPA drops into the danger band get a
  pulsing red ring plus a two-line label (vessel name, CPA / TCPA). Red
  crossing lines project both boats to their predicted meeting point.
- **Radar ARPA targets** (via [Mayara](https://github.com/MarineYachtRadar/mayara-server))
  render as outline triangles in the same collision pipeline.
- **Tap a vessel** for SOG/COG/HDG/bearing/distance/CPA + deep-links to
  MarineTraffic and VesselFinder.
- **Tide card** folds into the depth panel when a tide plugin is feeding
  `environment.tide.*` — height, flood/ebb arrow, countdown to HW/LW.
- **Anchor watch.** Manual drop, or server-driven via
  [signalk-anchoralarm-plugin](https://github.com/sbender9/signalk-anchoralarm-plugin).
- **Cruise / Race mode** (Settings). Race surfaces a performance card —
  target boat speed from your polar, optimal TWA for the current tack.
  Individual toggles still work in either mode; mode only picks defaults.

#### Planning and routing

- **Add button** (control bar) or long-press → create menu: waypoint, note,
  circle region, polygon region, or *Route with wind*. The button anchors
  at map centre; long-press anchors at the touch point. Keyboard parity for
  every item.
- **Routes + waypoints** (SignalK-managed): load, toggle visibility, edit
  on the map. Edit mode supports tap-to-add, drag-to-move with a dashed
  ghost + live Δ-distance, a numbered waypoint list with per-row remove,
  and undo. Empty name saves as `Route yyyyMMdd`.
- **Notes.** Title + body pin, folded-page icon, click to read or delete.
- **Regions.**
  - **Circle** with title + description + radius preset
    (100 m / 250 m / 500 m / 1 nm / 2 nm).
  - **Polygon** — freeform draw. Numbered vertices, draggable with ghost
    feedback, per-row remove, live area readout (m² → ha → km²). Save
    enables at 3 vertices. Server-supplied polygons render the same way.
- **Laylines** to the active waypoint, **persistent measurement tool**
  (multi-segment ruler, running total), **N↑ / C↑ / H↑** orientation cycle.
- **GPX import/export** for routes and waypoints.

#### Safety and alarms

- **[Collision detection](docs/collision-detection.md)** with COLREGS
  classification, per-target snooze (persisted), moored-vessel auto-mute,
  pulsing danger ring, red/amber crossing lines.
- **Life-safety beacons.** SART / MOB / EPIRB trigger a non-snoozeable
  alarm and render a pulsing red bullseye.
- **Anchor-tide alarm.** When the tide plugin feeds predictions, the
  anchor watch projects bottom depth to next LW; the alarm fires early
  enough to reset before grounding.
- **Server-side notifications.** Anything any plugin emits on
  `notifications.*` (signalk-anchoralarm-plugin, signalk-mob-notifier,
  custom depth alerts, ...) shows up in the same banner stack with
  appropriate severity, so plugin alerts don't get lost.
- **Stacked alarm banner** (up to three), ordered by
  (severity, time-to-event, priority) so `SHALLOW` at TTI=0 beats pending
  CPA. Snooze 10 min per vessel; snoozed chips show a live countdown.
  A **Log** button opens the last 20 alarms with dismissal reason.
- **MOB marker** — one-tap drop with a pulsing red pin.
- **Vessel list** in the Layers panel, sorted by TCPA (most pressing first).

#### Nice to have

- Server-side **24h track history** (handles both `LineString` and
  `MultiLineString` shapes).
- **Rain radar overlay** (RainViewer) and OpenSeaMap seamark overlay.
- **Chart overzoom** — tile layers use `maxNativeZoom` + `maxZoom 22`,
  so pinching past a chart's native max scales the last tile up instead of
  going blank. The Layers panel shows an always-visible "active stack"
  chip list for order.
- **Smooth-pan tile retention** — `keepBuffer: 4` + `updateWhenIdle: false`
  so panning back into recently-viewed tiles doesn't re-fetch.
- **Legend button** (leftmost on the control bar) opens a modal key to
  every symbol on the chart.
- **Race timer** — 5-minute countdown, "GO!" at zero.

### Other pages

- **Dashboard** — speed, course, position, depth, wind, tide, polar
  performance at a glance.
- **Gauges** — circular instruments with colour zones; responsive grid.
- **SailSteer** — compass-rose view combining wind, heading, COG, laylines
  and waypoint bearing.
- **Wind Rose** — TWD history with a 15 m / 30 m / 1 h / 3 h window picker.
- **Raw Stream** — live SignalK delta viewer with per-path filtering.
- **Paths** — live SignalK path inventory showing which paths your boat
  publishes, the latest value, and the source plugin. Useful for diagnosing
  "why is this gauge empty?" without leaving the app.
- **History** — replay of the rolling track with timespan picker,
  play / pause / step / speed controls and a scrub bar.
- **Settings** — Theme (System/Light/Dark), Night Mode (Soft/Amber/Red),
  sailing mode, alarm thresholds (depth, CPA, guard-zone lookahead, wind
  shift), polar-file upload with a live polar diagram. Polar format is a
  CSV with TWS headers across the top and TWA values down the first
  column; comma/semicolon/tab separators auto-detect, decimals must use
  `.`.

### Keyboard shortcuts (Map page)

| Key   | Action                                   |
|-------|------------------------------------------|
| `F`   | Toggle follow-boat                       |
| `O`   | Cycle orientation (N↑ / C↑ / H↑)         |
| `N`   | Toggle night mode                        |
| `A`   | Toggle anchor watch                      |
| `M`   | Drop MOB marker                          |
| `T`   | Fit track in viewport                    |
| `L`   | Toggle laylines                          |
| `D`   | Toggle measurement (distance) mode       |
| `R`   | Start/stop race timer                    |
| `?`   | Show the shortcut card                   |
| `Esc` | Dismiss overlays / exit measure / cancel |

Double-click the map to toggle a bearing/distance line from own-boat to
the click point. Long-press or right-click for the context menu. Same
actions live on the **Add** button in the bottom control bar.

### Plugin detection

Each optional plugin is probed the same way: hit its endpoint, cache the
result, hide dependent UI if absent.

| Plugin | What lights up |
|---|---|
| [buddylist](https://github.com/sbender9/signalk-buddylist-plugin) | Buddies section in Layers + star prefix on AIS labels |
| [signalk-tides](https://github.com/openwatersio/signalk-tides) (or any `environment.tide.*` publisher) | Tide card in HUD + Dashboard |
| [anchoralarm](https://github.com/sbender9/signalk-anchoralarm-plugin) | Anchor watch UI with live radius + drag detection |
| [Mayara](https://github.com/MarineYachtRadar/mayara-server) | Radar ARPA targets alongside AIS |

---

## Part 2 — For developers

### Local dev

```bash
dotnet workload install wasm-tools
dotnet run --project OnaPlotter/OnaPlotter.csproj
```

Open `http://localhost:5282/`. Point at a SignalK server via
`OnaPlotter/wwwroot/appsettings.json` — set `ServerUrl` to `"auto"` to
use the page origin when the app is hosted as a webapp.

To point your dev session at a real SignalK box (colleague's boat,
OpenPlotter on the bench) without editing the committed default,
create a gitignored overlay at
`OnaPlotter/wwwroot/appsettings.Development.json`:

```json
{
  "SignalK": {
    "ServerUrl": "https://openplotter.local"
  }
}
```

`ASPNETCORE_ENVIRONMENT=Development` (set by the launch profile)
picks it up automatically. The radar overlay reaches Mayara through
the SK server's built-in proxy at the same origin, so no separate
host/port config is needed.

### Tests

```bash
# C# unit + bUnit (~810 tests).
# .NET 10 SDK dropped VSTest dispatch, so `dotnet test` is NOT the path --
# TUnit runs as an executable via `dotnet run`.
dotnet run --project OnaPlotter.Tests

# JS math tests
node --test OnaPlotter/wwwroot/js/geoMath.test.js

# Playwright smoke + fuzz (requires a running app or deployed instance)
cd OnaPlotter.UiTests
npm install
npm run install:deps
BASE_URL=http://localhost:5282/ npm test
```

See [`OnaPlotter.UiTests/README.md`](OnaPlotter.UiTests/README.md) for the
fuzzer's `FUZZ_SEED` / `FUZZ_CLICKS` knobs and how to turn a caught failure
into a named regression test.

### Architecture

```
Browser (Blazor WASM)                          SignalK server
┌────────────────────────────────────┐        ┌──────────────────────┐
│  Components/                       │        │                      │
│  ├─ Pages/Map, Gauges, SailSteer…  │        │                      │
│  ├─ Map/ (Map sub-components)      │        │                      │
│  ├─ Gauges/CircularGauge           │        │                      │
│  └─ Layout/MainLayout + NavMenu    │        │                      │
│                                    │  WS    │                      │
│  SignalkClient ◄─────────────────► │───────►│  /signalk/v1/stream  │
│    ├─ NavigationData (own vessel)  │        │                      │
│    ├─ TrackBuffer (30 min buffer)  │        │                      │
│    └─ AisStore (other vessels)     │        │                      │
│                                    │  HTTP  │                      │
│  Services/Api/*Api ◄──────────────►│───────►│  /signalk/v1|v2/api  │
│    Chart, Route, Waypoint, Note,   │        │                      │
│    Region, Course, Autopilot,      │        │                      │
│    Path, Track, Weather (OM),      │        │                      │
│    BuddyList (optional)            │        │                      │
│                                    │        │                      │
│  Services/                         │        │                      │
│    AlarmManager + IAlarmRule       │        │                      │
│    PolarService, AppSettings       │        │                      │
│                                    │        │                      │
│  Utilities/                        │        │                      │
│    Cpa, Colregs, Format            │        │                      │
│                                    │        │                      │
│  wwwroot/js/leafletInterop.js      │        │                      │
│  wwwroot/js/platform/audioAlert.js │        │                      │
└────────────────────────────────────┘        └──────────────────────┘
```

- **`SignalkClient`** owns the WebSocket, parses deltas, fans them out to
  `NavigationData` (own-vessel fields), `TrackBuffer` (rolling track), and
  `AisStore` (other vessels). Subscriptions are split into per-context
  tiers (self-fast / self-fast-notifications / self-slow / AIS /
  server-notifications) registered as a single `SubscriptionTier` table
  near the top of the file; each tier has its own period and policy.
  Adding a new path = add a tier entry, not a new subscribe call.
- **`Components/Map/*`** are focused children of `Pages/Map.razor`:
  `MapHud` for the four-corner instruments + route / anchor / autopilot,
  `MapControls` for the bottom button bar, `RouteEditPanel` +
  `PolygonEditPanel` for editing overlays, `LayersPanel` hosting per-
  resource `*Section.razor` rows, `MapShortcutsOverlay` for keyboard
  help, `LegendOverlay` for the symbol-key modal.
- **`Services/Api/*`** are thin HTTP clients, one per concern. Return
  typed data or throw `HttpRequestException`; caller decides whether to
  toast or rethrow.
- **`Services/AlarmManager`** owns the alarm stack. Every registered
  `IAlarmRule` under `Services/Alarms/` (SHALLOW, CPA, WIND SHIFT, SART,
  ANCHOR-TIDE, ANCHOR-DRAG, DEADMAN, WAYPOINT-APPROACH, plus
  SERVER-NOTIFICATIONS which surfaces upstream `notifications.*` deltas)
  gets a shot at each eval tick; up to three concurrent alarms surface in
  the banner.
- **`Utilities/Cpa.cs`** is where substantive navigation math lives
  (CPA/TCPA projection). `wwwroot/js/geoMath.js` mirrors the smaller
  helpers with its own Node test suite.

### Behaviours worth knowing about

These stay honest when conditions get weird.

- **Connection health.** The top-row chip is the single source of truth:
  **Live** / **Stale** / **Offline**. The WebSocket reconnects with
  exponential backoff (1 s → 30 s cap). If nothing arrives for 5 s while
  the socket is open, the chip flips to **Stale** and a soft audio alarm
  plays. On full disconnect the alarm repeats every 3 s until recovery.
- **Settings that survive a reload.** Theme + Night preset, map
  orientation, follow toggle, laylines, sailing mode, alarm thresholds,
  per-target snoozes, enabled chart/route layers, first-run flags. All
  via `IKeyValueStore` with versioned keys (`.v1` suffix).
- **Freeboard-SK interop.** All resources use SignalK v2 `/resources/*`
  with a `feature` wrapper + `properties` block and GeoJSON `[lon, lat]`
  order. Tests pin the payloads so this contract can't drift silently.
- **Robustness touches.** All resource fetches use a `SafeLoad` wrapper
  (toast on failure, return empty instead of pinning the error banner).
  `async void` handlers catch `ObjectDisposedException`. Every
  `module.InvokeVoidAsync` catches `JSDisconnectedException`.
  `MooredVesselTracker` evicts vessels that drop off AIS. Long-press
  needs 700 ms of stillness within an 8 px envelope.
- **Branded boot screen.** Before the WASM bundle finishes downloading
  the user sees a centred favicon-derived SVG, the OnaPlotter wordmark,
  a CSS spinner (suppressed under `prefers-reduced-motion: reduce`),
  and a small build stamp pinned bottom-right (git short hash + UTC
  build time, populated by `js/version.g.js` from the `StampBuildInfo`
  MSBuild target). Lives inside `<div id="app">` so Blazor swaps it
  out wholesale on first render.
- **Phone-aware sidebar.** First run on a `<= 600px` viewport auto-
  collapses the rail to the icon track via
  `IAppSettings.ApplyMobileFirstRunDefaultsAsync`. Tracks an explicit
  / unset distinction so a manual chevron toggle later isn't
  overwritten. Mobile `(max-width: 600px)` also trims the nav icons,
  labels, and link padding while keeping tap targets above the iOS
  44 px floor.

### Contributing

Read [`agents.md`](agents.md) before opening a PR. Short version:

- `sealed` by default, no hidden inheritance hierarchy.
- JS is a renderer; classification stays in C#.
- Every SignalK resource round-trip needs a test pinning the POST shape.
- Alarms: one rule per file under `Services/Alarms/`, DI-registered.
- UI colours come from tokens (`--sev-*`, `--ann-*`, `--panel-*`) — no
  hardcoded red / green / amber.

### Roadmap

Tracked as GitHub issues, not in-tree (they rot that way). Big rocks on
deck: auto-routing around land (vector-chart-dependent), tile caching
across page reloads, external AIS name lookup for never-named MMSIs,
mode-specific default presets.

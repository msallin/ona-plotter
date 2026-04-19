# OnaPlotter

A touch-first chartplotter for sailors on a [SignalK](https://signalk.org/)
boat. Works on a 21" helm touchscreen, an iPad in the cockpit, or a phone in
a jacket pocket; same app, same data, responsive layout with WCAG-sized tap
targets under `@media (pointer: coarse)`.

Under the hood: Blazor WebAssembly, Leaflet for the chart, SignalK for every
byte of live data. Deploys as a SignalK webapp on a Pi, or runs standalone
from any browser.

## Why this and not another chartplotter

- **Freeboard-SK interop.** Routes, waypoints, notes, and regions all
  round-trip through the SignalK `/resources/*` API with the exact shape
  Freeboard expects. Create a waypoint here, open Freeboard on the same
  server, it's there. Same the other way. Tests pin the payload shape so it
  doesn't drift.
- **Proper collision detection.** CPA / TCPA projection with COLREGS
  crossing classification, not a proximity beeper. Moored-vessel auto-mute,
  per-target snooze, red/amber crossing lines. Full write-up in
  [docs/collision-detection.md](docs/collision-detection.md).
- **Weather routing built in.** Isochrone expansion over Open-Meteo wind +
  your polar CSV, right-click → *Route with wind*. No plugin required on
  the server. Full write-up in [docs/weather-routing.md](docs/weather-routing.md).
- **Plugin-aware, not plugin-dependent.** Tide plugin installed → Tide card
  lights up. Buddy plugin → buddy list. Anchor alarm plugin → anchor watch.
  None of them? The app degrades silently instead of showing broken UI.
- **Touch-first, not desktop-ported.** Long-press context menu, bottom-sheet
  panels on phones, first-run coachmark, 44-pixel minimum tap targets
  enforced across the app.

## The Map page

The Map is the primary view. Features are grouped by what you're doing when
you reach for them.

### While sailing (situational awareness)

- **HUD in the four corners.** SOG/COG/position (top-left), apparent + true
  wind with direction arrows (top-right), depth + tide countdown with
  traffic-light colouring and the alarm threshold visible (bottom-left),
  heading compass with arrow needle (bottom-right).
- **Own-vessel track**, speed-coloured; magenta arrow so the boat stays
  visible over blue water.
- **AIS targets.** Ship-type-coloured triangles with COG vector, 60-second
  fading trail, permanent name label. Names are seeded from the SignalK
  REST snapshot on connect (so vessels that have already broadcast static
  data show their name instead of a bare MMSI on first paint), then kept
  current via delta stream. Small type-glyph overlay (diamond / net / dot /
  plus) so deuteranopes can still tell sail from fishing from commercial.
- **CPA danger pulse.** Targets whose CPA drops into the danger band get a
  pulsing red ring on the chart plus a two-line label (vessel name on top,
  CPA / TCPA below) so a glance answers "which boat is on a collision
  track". Red crossing lines project both own-boat and target to their
  predicted CPA.
- **Radar ARPA targets** from [Mayara](https://github.com/MarineYachtRadar/mayara-server)
  render as outline triangles next to AIS with the same collision pipeline.
  Zero client config; if the plugin's present, targets show up.
- **Tap a vessel** → SOG/COG/HDG/bearing/distance/CPA popup with deep-links
  to MarineTraffic and VesselFinder.
- **Tide card** (when a plugin is feeding `environment.tide.*`): current
  height, flood/ebb arrow, countdown to next HW or LW with station name.
  Folded into the bottom-left depth panel so "water under boat now" and
  "where it's heading" sit together. Card hides entirely on installs
  without a tide plugin.
- **Anchor watch.** Manual drop from current position, or server-driven via
  [signalk-anchoralarm-plugin](https://github.com/sbender9/signalk-anchoralarm-plugin)
  with live radius and drag alarm.
- **Sailing mode** (Cruise / Race) in Settings. Light preset that surfaces
  mode-specific overlays (Race HUD with target boat speed and optimal TWA
  from your polar) without locking out features. Individual toggles still
  work in either mode.

### Planning and routing

- **Add button** in the control bar (or long-press the chart) opens a
  create menu: Waypoint, Note, Region, or *Route with wind*. Tap-anchored
  at the map centre when you use the button, long-press-anchored at the
  touch point. Keyboard-only users have parity with touch.
- **Routes + waypoints** (SignalK-managed): load, toggle visibility, edit on
  the map. Edit mode: tap to add WP, drag to move (with a dashed ghost
  line from the original position and a live Δ-distance label), a numbered
  waypoint list floats top-right with per-row remove, undo pops the last
  add. Empty route name saves as `Route yyyyMMdd`. Stop Navigation chip
  appears when a route is active.
- **Notes.** Drop a title+body pin anywhere on the chart; rendered as a
  slate-blue folded-page icon, click to read or delete.
- **Regions.** Translucent circle with title/description and a radius
  preset (100m / 250m / 500m / 1nm / 2nm). Polygon regions created
  elsewhere render too.
- **Weather routing** (isochrone expansion over Open-Meteo wind plus your
  polar, with the tidal-current vector folded in when the tide plugin
  feeds it). Details: [docs/weather-routing.md](docs/weather-routing.md).
- **Laylines** to the active waypoint, **persistent measurement tool**
  (multi-segment ruler, distance totals), **N↑ / C↑ / H↑** orientation
  cycle.
- **GPX import/export** for routes and waypoints.

### Safety and alarms

- **[Collision detection](docs/collision-detection.md).** CPA/TCPA
  projection with crossing-situation classification (COLREGS), per-target
  snooze (persisted across reload), moored-vessel auto-mute, pulsing
  danger ring on targets, red/amber crossing lines on the map.
- **Life-safety beacons.** SART / MOB / EPIRB AIS transmissions trigger a
  non-snoozeable alarm and render a pulsing red bullseye on the chart.
- **Anchor-tide alarm.** When the tide plugin is feeding predictions, the
  anchor watch projects the current swing-radius bottom to next LW; if
  predicted depth hits zero within the anchor circle the alarm fires early
  enough to reset before grounding.
- **Stacked alarm banner.** Up to three concurrent alarms, stack ordered
  by (severity, time-to-event, priority) so SHALLOW at TTI=0 beats pending
  CPA regardless of rule priority. **Snooze 10 m** silences a specific
  vessel; snoozed-target chips with countdown appear below the banner,
  tap to un-silence. **Log** button opens the last 20 alarms with
  dismissal reason (user / auto-cleared).
- **MOB marker** with pulsing red pin, one-tap drop.
- **Vessel list** in the Layers panel, sorted by TCPA (most pressing threat
  first), tap to centre + popup.

### Nice to have

- Server-side **24h track history** (handles both LineString and
  MultiLineString shapes from the SignalK track endpoint).
- **Rain-radar overlay** (RainViewer, overzooms cleanly past its native
  z=12 ceiling so pinching in blurs rather than errors out). OpenSeaMap
  overlay auto-enabled on first run.
- **Chart overzoom.** Every chart tile layer uses `maxNativeZoom` +
  `maxZoom 22`, so pinching past a chart's published max zoom scales the
  last valid tile up instead of going blank. The Layers panel shows an
  always-visible "active stack" chip list so it's clear which charts are
  drawn and in what order.
- **Legend button** leftmost on the control bar opens a modal key to
  every symbol on the chart (own boat, AIS shapes, CPA danger, SART,
  routes, waypoints, notes, regions, guard zone).
- **Race timer** with 5-minute countdown, "GO!" at zero.
- Context menu via **long-press (700 ms)** or right-click. First-run
  welcome card and touch coachmark gate on versioned KV keys.

## Other pages
- **Dashboard** — speed, course, position, depth, wind, tide and polar
  performance at a glance. Tide section surfaces when a SignalK tide plugin
  is feeding the bus.
- **Gauges** — circular instruments with colour zones; responsive grid (2-up on
  phones, auto on desktop).
- **SailSteer** — compass-rose view combining wind, heading, COG, laylines, and
  waypoint bearing; compass rotates so the boat always points up.
- **Wind Rose** — TWD history over time for spotting wind shifts, with a
  15m / 30m / 1h / 3h window picker so short-window racing and long-window
  cruising both get a useful view.
- **Raw Stream** — live SignalK delta viewer with per-path filtering, paused
  when you scroll away from the bottom.
- **History** — playback of the buffered track with step / scrub / play controls.
- **Settings** — Theme (System/Light/Dark), Night Mode (Soft/Amber/Red preset),
  alarm thresholds (depth, CPA, guard-zone lookahead+warning factor, wind
  shift + lookback), polar-file upload with a live polar diagram.

## Behaviours worth knowing about

These are the bits you don't normally notice, which is the point: they keep
the app honest when conditions get weird.

### Connection health
The top-row connection chip is the single source of truth; it shows **Live**,
**Stale**, or **Offline**. The SignalK WebSocket reconnects automatically with
exponential back-off (1 s → 30 s cap). Every message updates
`_lastMessageTicks`; if nothing arrives for 5 s while the socket is open the
chip flips to **Stale** and a soft audio alarm plays. On full disconnect the
alarm repeats every 3 s until the socket reconnects.

### Settings that survive a reload
- Theme (System/Light/Dark), Night Mode + preset (Soft/Amber/Red)
- Map orientation, Follow-boat toggle, Laylines visibility
- Sailing mode (Cruise / Race)
- Alarm thresholds (depth, guard zone CPA, guard zone lookahead, wind shift)
- Per-target alarm snoozes (so a 10-minute silence on one vessel survives
  a page reload)
- The exact set of enabled chart overlays and routes
- First-run welcome + touch coachmark dismissal flags

All of this lives in `localStorage` via `IKeyValueStore`, with versioned
keys (`.v1` suffix) so schema changes can migrate cleanly. Polar data is
stored separately under its own key.

### Collision detection
A proper CPA/TCPA model rather than a proximity beeper. Configurable guard-zone
radius and lookahead window, moored-vessel auto-mute, per-target snooze, and
red/amber crossing-situation lines drawn on the map. The whole pipeline,
including the single-file C# port of the math, is documented here:
**[docs/collision-detection.md](docs/collision-detection.md)**.

### Alarm banner
Up to three concurrent alarms stack at the top of the screen, ordered by
severity (Danger before Warn) then by rule priority (SHALLOW, CPA, WIND
SHIFT). Only the top banner pulses; lower entries are steady so a busy
viewport doesn't become a disco. Audio is orchestrated by `audioAlert.js`
with danger (1 s) and warn (3 s) cadences, driven off the top-of-stack
identity so a Warn→Danger escalation on the same target re-arms to the
faster cadence. The banner's **Snooze 10 m** button silences a specific
vessel so other threats still trip the alarm; snoozed targets show up as
small chips below the banner with a live countdown, tap to un-silence.
A **Log** button in the top row opens a drawer of the last 20 alarms
with their dismissal reason (user-dismissed / user-snoozed / auto-cleared)
for post-mortem.

### Freeboard-SK interop
Routes, waypoints, notes, and regions all round-trip through the SignalK v2
`/resources/*` API with the shape Freeboard-SK expects (`feature` wrapper with
`properties`, GeoJSON geometry in `[lon, lat]` order, etc.). A waypoint or note
created in OnaPlotter appears in Freeboard and vice-versa, on the same server,
with no extra configuration. The test suite pins the exact POST payloads so
this contract doesn't drift silently.

### Optional plugin detection
Each optional plugin is probed the same way: hit its endpoint once, cache the
result, hide dependent UI if it's absent.
- [sbender9/signalk-buddylist-plugin](https://github.com/sbender9/signalk-buddylist-plugin)
  feeds the Buddies section in the Layers panel and the buddy-star on AIS
  triangles.
- [openwatersio/signalk-tides](https://github.com/openwatersio/signalk-tides)
  (or any plugin publishing `environment.tide.*`) feeds the Tide HUD card and
  the Tide section on the Dashboard.
- [sbender9/signalk-anchoralarm-plugin](https://github.com/sbender9/signalk-anchoralarm-plugin)
  drives the anchor watch UI with live radius + drag detection.
- [Mayara](https://github.com/MarineYachtRadar/mayara-server) radar ARPA
  targets show up alongside AIS when the plugin is present.

### Keyboard shortcuts

On the Map page:

| Key   | Action                                       |
|-------|----------------------------------------------|
| `F`   | Toggle follow-boat                           |
| `O`   | Cycle orientation (N↑ / C↑ / H↑)             |
| `N`   | Toggle night mode                            |
| `A`   | Toggle anchor watch                          |
| `M`   | Drop MOB marker                              |
| `T`   | Fit track in viewport                        |
| `L`   | Toggle laylines                              |
| `D`   | Toggle measurement (distance) mode           |
| `R`   | Start/stop race timer                        |
| `?`   | Show the shortcut card                       |
| `Esc` | Dismiss the shortcut card / exit measure     |

Double-click the map to toggle a bearing/distance line from own-boat to the
click point. Long-press or right-click for the context menu (Create
Waypoint / Add Note / Add Region / Navigate Here / Route with wind / Stop
Navigation). Same actions are reachable from the **Add** button in the
bottom control bar for keyboard-only users.

### Small robustness touches
- All SignalK resource fetches (`ChartApi`, `RouteApi`, `WaypointApi`) use a
  `SafeLoad` wrapper: a 404 or network blip surfaces a toast and returns an
  empty list instead of pinning the Blazor error banner.
- Every `async void` event handler wraps its body in `try/catch
  (ObjectDisposedException)` so quick page switches during data bursts don't
  tear down the Blazor SynchronizationContext.
- All `module.InvokeVoidAsync` call sites catch `JSDisconnectedException`.
- `MooredVesselTracker` evicts vessels that drop off AIS instead of leaking
  one dictionary entry per harbour tug forever.
- Long-press on the map only fires after 700 ms of stillness within an 8-px
  envelope, cancelled by Leaflet's own `movestart`/`dragstart`/`zoomstart`.

## Architecture

```
Browser (Blazor WASM)                          SignalK server
┌────────────────────────────────────┐        ┌──────────────────────┐
│  Components/                       │        │                      │
│  ├─ Pages/Map, Gauges, SailSteer…  │        │                      │
│  ├─ Map/ (extracted Map sub-comps) │        │                      │
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
│    Cpa, IsochroneRouter, Format    │        │                      │
│                                    │        │                      │
│  wwwroot/js/leafletInterop.js      │        │                      │
│  wwwroot/js/audioAlert.js          │        │                      │
└────────────────────────────────────┘        └──────────────────────┘
```

- **`SignalkClient`** owns the WebSocket, parses SignalK deltas, and fans them
  out to `NavigationData` (own vessel fields), `TrackBuffer` (rolling track),
  and `AisStore` (other vessels).
- **`Components/Map/*`** are focused children of `Pages/Map.razor`: `MapHud`
  for the four-corner instrument panels plus route / anchor / autopilot,
  `MapControls` for the bottom button bar, `RouteEditPanel` for the editing
  overlay, `LayersPanel` hosting per-resource `*Section.razor` rows
  (Charts / Routes / Waypoints / Notes / Regions / Vessels / Buddies /
  Weather / TrackHistory), `MapShortcutsOverlay` for keyboard help, and
  `LegendOverlay` for the symbol-key modal.
- **`Services/Api/*`** are thin HTTP clients, one per concern. They return
  typed data or throw `HttpRequestException`; the caller decides whether to
  surface a toast or rethrow.
- **`Services/AlarmManager`** owns the alarm stack: every registered
  `IAlarmRule` (SHALLOW, CPA, WIND SHIFT today) gets a crack at each eval
  tick; up to three concurrent alarms surface in the banner.
- **`Utilities/Cpa.cs`** and **`Utilities/IsochroneRouter.cs`** are the two
  places that do substantive navigation math in C# (CPA/TCPA projection for
  collisions; isochrone expansion + sector pruning for weather routing).
  `wwwroot/js/geoMath.js` mirrors the smaller math helpers with its own
  Node test suite.

## Install on a SignalK server

The common case: you've got a SignalK server on a Pi or similar, and you
want OnaPlotter as a webapp alongside it.

```powershell
pwsh ./deploy/deploy.ps1            # publish + scp to pi@openplotter.local
pwsh ./deploy/deploy.ps1 -SkipBuild # reuse the previous publish output
```

The script wipes `obj/Release` and `bin/Release` before publishing to
sidestep stale-AOT issues, patches `<base href>` to the webapp path, and
SCPs the bundle into `~/.signalk/node_modules/signalk-onaplotter/` on the
target host. Restart SignalK, go to the webapps page, open OnaPlotter.

The target host and user are set at the top of the script. For a
non-PowerShell environment, the equivalent is: `dotnet publish -c Release
OnaPlotter/OnaPlotter.csproj` → `scp -r bin/Release/net10.0/publish/wwwroot/*
user@host:~/.signalk/node_modules/signalk-onaplotter/`.

## Run locally for development

```bash
dotnet workload install wasm-tools
dotnet run --project OnaPlotter/OnaPlotter.csproj
```

The app starts on `http://localhost:5282/`. Point it at a SignalK server
by editing `OnaPlotter/wwwroot/appsettings.json`; set `ServerUrl` to
`"auto"` to use the page origin when the app is hosted as a webapp.

### Tests

```bash
# C# unit + bUnit tests (~370 at time of writing).
# .NET 10 SDK dropped VSTest dispatch, so `dotnet test` is NOT the
# path -- TUnit runs as an executable via `dotnet run`.
dotnet run --project OnaPlotter.Tests

# JS math tests
node --test OnaPlotter/wwwroot/js/geoMath.test.js

# Playwright smoke + fuzz (requires a running app or deployed instance)
cd OnaPlotter.UiTests
npm install
npm run install:deps
BASE_URL=http://localhost:5282/ npm test
```

See `OnaPlotter.UiTests/README.md` for the fuzzer's `FUZZ_SEED` /
`FUZZ_CLICKS` knobs and how to turn a caught failure into a named
regression test.

## Future plans

- [ ] Polygon regions via freeform drawing (circles and server-supplied
      polygons already work)
- [ ] Offline tile / MBTiles chart download for the current viewport
- [ ] Auto-routing around land, which is gated on vector charts; we
      only have raster PNG tiles today, so this needs S-57 vector
      parsing + a coastline rasteriser before it's worth scoping.
- [ ] Mode-specific default presets (Race auto-enables laylines, etc.)
- [ ] Click-to-expand on the four corner HUD panels for more detail
      (VMG, signed AWA/TWA, HDG magnetic vs true, depth offset)
- [ ] Light-theme polish pass -- most panels still have hardcoded dark
      backgrounds, so picking Light flips the chrome but not the body.

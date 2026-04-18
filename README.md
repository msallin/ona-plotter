# OnaPlotter

Blazor WebAssembly chartplotter that connects to a [SignalK](https://signalk.org/)
server for real-time maritime navigation. Runs as a SignalK webapp on a Pi,
or standalone on any device with a browser.

Works on a 21" helm touchscreen, an iPad in the cockpit, or a phone in a jacket
pocket. All layout is responsive and touch-tuned (WCAG-sized tap targets under
`@media (pointer: coarse)`).

## At-a-glance features

### Map page
- Leaflet map with HUD instruments in the four corners: SOG/COG/position (top-left),
  apparent+true wind with direction arrows (top-right), depth with traffic-light
  colouring (bottom-left), heading compass with arrow needle (bottom-right).
- Speed-coloured own-vessel track, own-vessel magenta arrow so it stays visible
  over blue water.
- AIS targets: ship-type-coloured triangles with COG vector, 60-second fading
  trail, permanent name label (resolved from SignalK or falling back to MMSI).
- Radar ARPA targets from
  [Mayara](https://github.com/MarineYachtRadar/mayara-server) render as
  outline triangles alongside AIS, share the same guard-zone alarm / CPA
  crossing lines / COLREGS classification pipeline. No configuration needed
  on the client: we subscribe to `radars.*.targets.*` and the rest just
  works when a radar plugin is present.
- Click any vessel for a popup with SOG/COG/HDG/BRG/distance/CPA plus deep links
  to MarineTraffic and VesselFinder.
- [Collision detection](docs/collision-detection.md): guard-zone ring, crossing
  lines, configurable CPA/TCPA alarm, per-target snooze, auto-mute of moored
  vessels. See the linked doc for the full behaviour.
- [Weather routing](docs/weather-routing.md): isochrone route from own
  position to any map point using Open-Meteo's wind forecast and your
  uploaded polars. Right-click → *Route with wind*.
- Vessel list in the Layers panel, sorted by TCPA (most pressing threat first),
  tap to centre + popup.
- SignalK-managed routes and waypoints: load, toggle visibility, edit on the
  map (tap to add WP, drag to move, undo, save), Stop Navigation chip when a
  route is active.
- Server track (last 24h), tidal current arrow, OpenWeatherMap/RainViewer
  weather overlay, OpenSeaMap overlay (auto-enabled on first run).
- Tide heights + next HW/LW from any SignalK tide plugin (e.g.
  [openwatersio/signalk-tides](https://github.com/openwatersio/signalk-tides)):
  small Tide card in the bottom-left with current height, flood/ebb arrow,
  countdown to the next extreme and station name. Card hides entirely on
  installs without a tide plugin -- no empty state.
- Anchor watch: manual drop from current position, or server-driven via
  [signalk-anchoralarm-plugin](https://github.com/sbender9/signalk-anchoralarm-plugin)
  with live radius and drag alarm.
- Laylines, MOB marker, bearing/distance measurement (double-click), race timer,
  N-Up / C-Up / H-Up orientation.
- GPX import/export.
- Geolocated notes: long-press → *Add Note*, drop a title+body pin at any chart
  point. Rendered as a muted slate-blue folded-page icon (distinct from
  waypoints and routes); click to read or delete. Round-trips the SignalK
  `resources/notes` API so freeboard-sk and other clients see the same notes.
- Regions: long-press → *Add Region* to drop a translucent circle with title
  and description (100m / 250m / 500m / 1nm / 2nm presets). Circles are
  emitted as 32-vertex Polygon approximations on the wire so freeboard-sk
  and any GeoJSON-aware consumer see the same shape. Polygon regions
  created elsewhere render too; Layers panel has a Show/Hide toggle and a
  Focus-to-bounds button per region.
- Keyboard shortcuts (F, O, N, A, M, T, L, R, ?, Esc); long-press or right-click
  for the context menu (*Create Waypoint*, *Add Note*, *Add Region*,
  *Navigate Here*, *Route with wind*, *Stop Navigation*).

### Other pages
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

## Less-visible behaviours (the "quiet" features)

### Connection health
The top-row connection chip is the single source of truth; it shows **Live**,
**Stale**, or **Offline**. The SignalK WebSocket reconnects automatically with
exponential back-off (1 s → 30 s cap). Every message updates
`_lastMessageTicks`; if nothing arrives for 5 s while the socket is open the
chip flips to **Stale** and a soft audio alarm plays. On full disconnect the
alarm repeats every 3 s until the socket reconnects.

### Settings that survive a reload
- Theme (System/Light/Dark), Night Mode
- Map orientation, Follow-boat toggle, Laylines visibility
- Alarm thresholds (depth, guard zone CPA, guard zone lookahead, wind shift)
- The exact set of enabled chart overlays and routes

All of this lives in `localStorage` via `IKeyValueStore`; Polar data is stored
separately under its own key.

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

### Robustness the user shouldn't notice
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
- **`Components/Map/*`** are focused children of `Pages/Map.razor`: `MapHud` for
  the four-corner instrument panels plus route/anchor/autopilot, `MapControls`
  for the bottom button bar, `RouteEditPanel` for the editing overlay,
  `LayersPanel` for charts/routes/waypoints/vessels/track/weather, and
  `MapShortcutsOverlay` for the keyboard help card.
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

## Quick start

```bash
dotnet workload install wasm-tools
dotnet run --project OnaPlotter/OnaPlotter.csproj
```

The app starts on `http://localhost:5282/`. Configure the SignalK server URL
in `OnaPlotter/wwwroot/appsettings.json`; set it to `"auto"` to use the page
origin when the app is hosted as a SignalK webapp.

## Tests

```bash
# C# unit tests (~270 at time of writing)
dotnet run --project OnaPlotter.Tests

# JS math tests
node --test OnaPlotter/wwwroot/js/geoMath.test.js

# Playwright smoke + fuzz (requires a running app or deployed instance)
cd OnaPlotter.UiTests
npm install
npm run install:deps
BASE_URL=http://localhost:5282/ npm test
```

See `OnaPlotter.UiTests/README.md` for the fuzzer's `FUZZ_SEED`/`FUZZ_CLICKS`
knobs and how to turn a caught failure into a named regression test.

## Deploy as a SignalK webapp

```powershell
pwsh ./deploy/deploy.ps1            # publish + scp to pi@openplotter.local
pwsh ./deploy/deploy.ps1 -SkipBuild # reuse the previous publish output
```

The script wipes `obj/Release` and `bin/Release` before publishing to sidestep
stale-AOT issues, patches `<base href>` to the webapp path, and SCPs the
bundle into `~/.signalk/node_modules/signalk-onaplotter/` on the target.
Restart SignalK to pick it up.

## Future plans

- [ ] COLREGS crossing-category labels (head-on / port / stbd / overtaking)
- [ ] Offline tile download for the current viewport
- [ ] Polygon regions via freeform drawing (circles and server-supplied
      polygons already work)
- [ ] Tide-aware weather routing: add the tidal-current vector to the
      polar-derived boat speed inside `IsochroneRouter`

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
- Vessel list in the Layers panel, sorted by TCPA (most pressing threat first),
  tap to centre + popup.
- SignalK-managed routes and waypoints: load, toggle visibility, edit on the
  map (tap to add WP, drag to move, undo, save), Stop Navigation chip when a
  route is active.
- Server track (last 24h), tidal current arrow, OpenWeatherMap/RainViewer
  weather overlay, OpenSeaMap overlay (auto-enabled on first run).
- Anchor watch: manual drop from current position, or server-driven via
  [signalk-anchoralarm-plugin](https://github.com/sbender9/signalk-anchoralarm-plugin)
  with live radius and drag alarm.
- Laylines, MOB marker, bearing/distance measurement (double-click), race timer,
  N-Up / C-Up / H-Up orientation.
- GPX import/export.
- Keyboard shortcuts (F, O, N, A, M, T, L, R, ?, Esc); long-press or right-click
  for the context menu (*Create Waypoint*, *Navigate Here*, *Stop Navigation*).

### Other pages
- **Dashboard** — speed, course, position, depth, wind at-a-glance.
- **Gauges** — circular instruments with colour zones; responsive grid (2-up on
  phones, auto on desktop).
- **SailSteer** — compass-rose view combining wind, heading, COG, laylines, and
  waypoint bearing; compass rotates so the boat always points up.
- **Wind Rose** — TWD history over time for spotting wind shifts.
- **Raw Stream** — live SignalK delta viewer with per-path filtering, paused
  when you scroll away from the bottom.
- **History** — playback of the buffered track with step / scrub / play controls.
- **Settings** — Theme (System/Light/Dark), Night Mode, alarm thresholds,
  polar-file upload.

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
A single top banner surfaces the most-urgent alarm. Priorities: SHALLOW (depth
under threshold) > CPA (vessel projected inside guard zone) > WIND SHIFT (TWD
shifted more than threshold over 5 min). Audio is orchestrated by `audioAlert.js`
with danger (1 s) and warn (3 s) cadences. The banner's **Snooze 10 m** button
silences a specific vessel so other threats still trip the alarm.

### Optional plugin detection
`BuddyListApi` probes for
[sbender9/signalk-buddylist-plugin](https://github.com/sbender9/signalk-buddylist-plugin)
(`GET /signalk/v2/api/resources/buddies`) once per session and caches the
result. UI that depends on it (coming next) stays hidden when the plugin isn't
installed, so the app degrades cleanly against any SignalK deployment.

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
│    Chart, Route, Waypoint, Course, │        │                      │
│    Autopilot, Path, Track,         │        │                      │
│    BuddyList (optional)            │        │                      │
│                                    │        │                      │
│  Utilities/                        │        │                      │
│    Cpa, Format, Icons, JsonExt.    │        │                      │
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
- **`Utilities/Cpa.cs`** is the only place that does navigation math in C#;
  `wwwroot/js/geoMath.js` has the JS mirror with its own test suite.

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
# C# unit tests (178+ at time of writing)
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

- [ ] Buddy-list UI integration (the detection plumbing is already in place)
- [ ] COLREGS crossing-category labels (head-on / port / stbd / overtaking)
- [ ] Grib-based weather routing
- [ ] Offline tile download for the current viewport

# OnaPlotter

A touch-first chartplotter for sailors on a [SignalK](https://signalk.org/)
boat. One app for a 21" helm touchscreen, an iPad in the cockpit, or a phone
in a jacket pocket -- same data, responsive layout, WCAG-sized tap targets.

Built on Blazor WebAssembly + Leaflet + the SignalK stream. Deploys as a
SignalK webapp on a Pi, or runs standalone from any browser.

## Why this and not another plotter

- **Freeboard-SK interop.** Routes, waypoints, notes, and regions round-trip
  through `/resources/*` with the exact shape Freeboard expects. Create
  here, see there. Tests pin the payload so it can't drift.
- **Real collision detection.** CPA / TCPA projection with COLREGS crossing
  classification, not a proximity beeper. Moored-vessel auto-mute,
  per-target snooze, pulsing danger ring, red/amber crossing lines.
  ([deep dive](docs/collision-detection.md))
- **Plugin-aware, not plugin-locked.** Tide, buddy list, anchor alarm,
  Mayara radar -- each lights up its own UI when installed; missing
  plugins surface a clear "plugin vX.Y+ required" toast rather than a
  silent fallback.
- **Touch-first.** Long-press context menu, bottom-sheet panels on phones,
  44 px minimum tap targets, full keyboard parity, branded boot screen.

## Features

### Map (the primary view)

- **Four-corner HUD.** SOG/COG/position, wind (AWA + TWA, direction
  arrows, Beaufort), depth + tide countdown, heading compass. Tap a
  corner to expand (DMS, VMG, XTE, drift, tidal current, autopilot).
- **Own-vessel track**, speed-coloured, magenta chevron.
- **AIS targets** with COG vector, 60 s fading trail, ship-type colour
  + small type glyph (diamond / net / dot / plus) so deuteranopes can
  still tell sail from fishing from commercial. Tap a vessel for
  SOG/COG/HDG/bearing/distance/CPA + MarineTraffic / VesselFinder
  deep-links.
- **CPA danger pulse.** Red ring + crossing lines for targets in the
  danger band; two-line vessel + CPA/TCPA label.
- **Anchor watch** via [signalk-anchoralarm-plugin](https://github.com/sbender9/signalk-anchoralarm-plugin)
  v2.0.0+. Two-step drop + set-radius with on-map preview as you pick
  the chip; tide-aware grounding alarm when a tide plugin is feeding.
- **MOB** -- one-tap drop with a pulsing red bullseye; non-snoozeable.
  Same treatment for SART / EPIRB.
- **Route + waypoint editing.** Tap-to-add, drag-to-move with dashed
  ghost + live Δ-distance, numbered waypoint list, undo. Reverse,
  save-as-copy, GPX import/export.
- **Regions.** Circle (radius preset 100 m / 250 m / 500 m / 1 nm /
  2 nm) and polygon (numbered vertices, draggable, area readout in
  m² / ha / km²).
- **Notes** as folded-page pins.
- **Tide card** when `environment.tide.*` is published.
- **Charts**: any tile source SignalK exposes (Navionics PNG, S-57
  raster, OpenSeaMap), plus OSM base, OpenSeaMap seamarks overlay,
  RainViewer rain radar. Chart overzoom keeps detail past native max
  zoom; quick-pick chips for the top stack.
- **Tools**: laylines to active waypoint, persistent measurement
  ruler (multi-segment), N↑ / C↑ / H↑ orientation cycle, race timer,
  legend modal.
- **Cruise / Race mode** preset (Settings) flips defaults and surfaces
  a performance card -- target boat speed from your polar, optimal TWA.
  Mode picks defaults; every individual toggle still works in either.

### Other pages

- **Dashboard** -- speed, course, position, depth, wind, tide, polar
  performance at a glance.
- **Gauges** -- circular instruments with colour zones, responsive grid.
- **SailSteer** -- compass-rose view combining wind, heading, COG,
  laylines, waypoint bearing.
- **Wind Rose** -- TWD history with a 15 m / 30 m / 1 h / 3 h window.
- **History** -- replay the full track for any window with play / pause
  / step / speed and a scrub bar.
- **Stats** -- passage stats (distance, time, max/avg SOG, ...).
- **Paths** -- live SignalK path inventory: which paths your boat
  publishes, latest values, source plugin. For diagnosing "why is this
  gauge empty?" without leaving the app.
- **Raw Stream** -- live delta viewer with per-path filtering.
- **Settings** -- Theme (System / Light / Dark), Night Mode (Soft /
  Amber / Red), sailing mode, alarm thresholds (depth, CPA, guard-zone
  lookahead, wind shift), polar-file upload with live polar diagram.

### Alarms

- Stacked banner (up to three), ordered by `(severity, time-to-event,
  priority)` -- so SHALLOW at TTI=0 beats pending CPA.
- Snooze 10 min per vessel; snoozed chips show a live countdown.
- Server-side notifications: anything any plugin emits on
  `notifications.*` joins the same banner stack with appropriate
  severity, so plugin alerts don't get lost.
- **Log** button opens the last 20 alarms with dismissal reason.
- Configurable thresholds in Settings; rules: SHALLOW, CPA, WIND SHIFT,
  SART, ANCHOR-TIDE, ANCHOR-DRAG, DEADMAN, WAYPOINT-APPROACH.

### Connection health

The top-row chip is the single source of truth: **Live** / **Stale** /
**Offline**. WebSocket reconnects with exponential backoff (1 s -> 30 s
cap). If nothing arrives for 5 s while the socket is open, the chip
flips to **Stale** and a soft audio alarm plays; on full disconnect
the alarm repeats every 3 s until recovery.

## Plugins that light up features

Each is probed once and cached. Dependent UI hides when the plugin is
absent.

| Plugin | Lights up |
|---|---|
| [signalk-anchoralarm-plugin](https://github.com/sbender9/signalk-anchoralarm-plugin) v2.0.0+ | Anchor watch, drag detection, incomplete-anchor alarm |
| [signalk-tides-api](https://github.com/sbender9/signalk-tides-api) (or any `environment.tide.*` publisher) | Tide card in HUD + Dashboard, anchor-tide grounding alarm |
| [signalk-buddylist](https://github.com/sbender9/signalk-buddylist-plugin) | Buddies section in Layers + star prefix on AIS labels |
| [Mayara](https://github.com/MarineYachtRadar/mayara-server) | Radar ARPA targets alongside AIS |
| [signalk-flags](https://github.com/SignalK/signalk-flags) | Country flag in AIS popups |

## Install on a SignalK server

```powershell
pwsh ./deploy/deploy.ps1            # publish + scp to pi@openplotter.local
pwsh ./deploy/deploy.ps1 -SkipBuild # reuse the last publish output
```

The script wipes `obj/Release` + `bin/Release` (sidesteps stale-AOT),
patches `<base href>` to the webapp path, and SCPs into
`~/.signalk/node_modules/signalk-onaplotter/` on the target host.
Restart SignalK; OnaPlotter shows up on the Webapps page. Target host
and user live at the top of the script.

Non-PowerShell:

```bash
dotnet publish -c Release OnaPlotter/OnaPlotter.csproj
scp -r OnaPlotter/bin/Release/net10.0/publish/wwwroot/* \
    user@host:~/.signalk/node_modules/signalk-onaplotter/
```

## Keyboard shortcuts (Map page)

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

Double-click the map for a bearing/distance line from own-boat to the
click point. Long-press or right-click for the context menu. Same
actions live on the **Add** button in the bottom control bar.

## Contributing

See [`AGENTS.md`](AGENTS.md) for architecture, design decisions, the
six test surfaces, and SignalK setup for development.

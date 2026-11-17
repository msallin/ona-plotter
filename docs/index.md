---
title: OnaPlotter
---

# OnaPlotter

A touch-first chartplotter webapp for [SignalK](https://signalk.org/).
One app for a helm touchscreen with keyboard and mouse, a tablet in the
cockpit, or a phone in a jacket pocket: same data, responsive layout,
large tap targets.

## Install

Install **OnaPlotter** from the App Store in the SignalK server admin UI,
or from the server's directory:

```sh
npm install signalk-onaplotter
```

Restart the server and open `http://<your-server>:3000/signalk-onaplotter/`.
It also appears under *Webapps* in the admin UI.

## Try it in the browser

**[Open OnaPlotter](app/)** — the latest build, straight from this site.

To connect it to your boat, open **Settings**, turn on **Standalone mode**
and enter your SignalK server's URL. Two requirements on the server side:

- **HTTPS.** This page is served over HTTPS, so browsers only allow it to
  talk to an `https://` / `wss://` SignalK server, not a plain
  `http://boat.local:3000`. Installing from the App Store above has no
  such limit.
- **CORS.** Allow the origin `https://msallin.github.io` in the SignalK
  server's security settings (*Allowed CORS origins*).

## Features

- **Charts** from any tile source your SignalK server exposes, plus OSM,
  OpenSeaMap seamarks and rain radar. Overzoom past native max zoom.
- **AIS** with COG vectors, trails and CPA/TCPA collision alarms with
  COLREGS crossing classification. See [collision detection](collision-detection.md).
- **Anchor watch** with on-map radius preview and tide-aware grounding alarm.
- **MOB** one-tap drop; SART/EPIRB handled the same way.
- **Routes, waypoints, notes and regions**, stored as standard SignalK
  resources so other SignalK apps see them too. GPX import/export.
- **Instruments**: dashboard, gauges, SailSteer, wind rose, track history
  replay, passage stats, live SignalK path inventory.
- **Alarms** for depth, CPA, wind shift, anchor drag, deadman and
  waypoint approach, stacked by severity and time-to-event, merged with
  server-side SignalK notifications.
- **Night modes** (soft, amber, red) and a Cruise/Race preset.

## Works better with

Each of these lights up extra UI when installed; OnaPlotter works without
them.

| Plugin | Lights up |
|---|---|
| [signalk-anchoralarm-plugin](https://github.com/sbender9/signalk-anchoralarm-plugin) v2.0.0+ | Anchor watch, drag detection |
| [signalk-tides-api](https://github.com/sbender9/signalk-tides-api) | Tide card, anchor-tide grounding alarm |
| [signalk-buddylist-plugin](https://github.com/sbender9/signalk-buddylist-plugin) | Buddy boats |
| [Mayara](https://github.com/MarineYachtRadar/mayara-server) | Radar targets alongside AIS |
| [signalk-flags](https://github.com/SignalK/signalk-flags) | Country flags in AIS popups |

## Keyboard shortcuts (Map page)

| Key | Action |
|---|---|
| `F` | Toggle follow-boat |
| `O` | Cycle orientation (N↑ / C↑ / H↑) |
| `N` | Toggle night mode |
| `A` | Toggle anchor watch |
| `M` | Drop MOB marker |
| `T` | Fit track in viewport |
| `L` | Toggle laylines |
| `D` | Toggle measurement mode |
| `R` | Start/stop race timer |
| `?` | Show the shortcut card |
| `Esc` | Dismiss overlays / exit measure / cancel |

## Source and issues

[github.com/msallin/ona-plotter](https://github.com/msallin/ona-plotter) · MIT licence

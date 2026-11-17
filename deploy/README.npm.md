# OnaPlotter

A touch-first chartplotter webapp for [SignalK](https://signalk.org/).
One app for a helm touchscreen with keyboard and mouse, a tablet in the
cockpit, or a phone in a jacket pocket: same data, responsive layout,
large tap targets.

## Features

- **Charts** from any tile source your SignalK server exposes, plus OSM,
  OpenSeaMap seamarks and rain radar.
- **AIS** with COG vectors, trails and CPA/TCPA collision alarms with
  COLREGS crossing classification.
- **Anchor watch**, **MOB** and SART/EPIRB handling.
- **Routes, waypoints, notes and regions**, stored as standard SignalK
  resources so other SignalK apps see them too. GPX import/export.
- **Instruments**: dashboard, gauges, SailSteer, wind rose, track history
  replay, passage stats.
- **Alarms** for depth, CPA, wind shift, anchor drag, deadman and
  waypoint approach, merged with server-side SignalK notifications.
- **Night modes** (soft, amber, red).

## Install

Install **OnaPlotter** from the App Store in the SignalK server admin UI,
or from the server's directory:

```sh
npm install signalk-onaplotter
```

Restart the server and open `http://<your-server>:3000/signalk-onaplotter/`.
It also appears under *Webapps* in the admin UI.

## Works better with

Each of these lights up extra UI when installed; OnaPlotter works without
them.

- [signalk-anchoralarm-plugin](https://github.com/sbender9/signalk-anchoralarm-plugin) v2.0.0+: anchor watch
- [signalk-tides-api](https://github.com/joabakk/signalk-tides-api): tide card and anchor-tide alarm
- [signalk-buddylist-plugin](https://github.com/sbender9/signalk-buddylist-plugin): buddy boats
- [signalk-parquet](https://github.com/motamman/signalk-parquet): History and Stats pages, server-side track, Wind Rose history
- [Mayara](https://github.com/MarineYachtRadar/mayara-server) via its [signalk plugin](https://github.com/MarineYachtRadar/mayara-server-signalk-plugin): radar targets
- [signalk-flags](https://github.com/panaaj/signalk-flags): country flags in AIS popups

## Links

- Documentation: https://msallin.github.io/ona-plotter/
- Source and issues: https://github.com/msallin/ona-plotter

## License

MIT

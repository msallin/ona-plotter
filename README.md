# OnaPlotter

Blazor WebAssembly chartplotter that connects to a [SignalK](https://signalk.org/) server for real-time maritime navigation.

## Features

- **Dashboard** — speed, course, position, depth, and wind at a glance
- **Chartplotter** — Leaflet map with HUD instruments, speed-colored track, AIS targets with CPA/TCPA
- **Anchor watch** — manual or server-driven via [signalk-anchoralarm-plugin](https://github.com/sbender9/signalk-anchoralarm-plugin)
- **Active route info** — DTW, BRG, VMG, TTG from SignalK course data
- **Connection monitoring** — acoustic alarm on disconnect, stale-data warning after 5 s silence
- **Gauges & Wind Rose** — circular instruments and wind direction history
- **Night mode**, MOB marker, bearing/distance tool, fullscreen

## Quick start

```bash
dotnet workload install wasm-tools
dotnet run --project OnaPlotter/OnaPlotter.csproj
```

Configure the SignalK server URL in `OnaPlotter/wwwroot/appsettings.json`.

## Future plans

- [ ] Route planning UI (create/edit waypoints on the map)
- [ ] Tides & currents overlay
- [ ] Configurable alarm thresholds (depth, CPA, anchor radius)
- [ ] Persist user settings (night mode, enabled layers) to local storage
- [ ] Autopilot integration (display AP state, send course commands)
- [ ] Weather overlay (GRIB / open-meteo)
- [ ] Multi-instrument dashboard customization (drag & resize cards)
- [ ] Offline chart caching (service worker / tile DB)
- [ ] Mobile PWA (installable, orientation lock)

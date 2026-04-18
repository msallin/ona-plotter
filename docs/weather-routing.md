# Weather routing

OnaPlotter computes an isochrone-based weather route from the current
boat position to any point on the chart. Long-press or right-click → *Route
with wind* → an amber dashed line appears showing the fastest path given
the forecast wind and the boat's polar.

## What you see

```
            wind forecast from Open-Meteo
                    ▲
                    │
     own boat       │            destination
        ●──────────tack─────────○  (amber dot, white ring)
                    │
                    ▼
       └── dashed amber route, black halo underneath
```

- **Amber dashed line** — the computed fastest route.
- **Black halo** — a thicker translucent stroke behind the amber so
  the line reads against any background (sand, chart labels, overlays).
- **Small amber dot** — the start (own position at the time you asked).
- **Ringed amber dot** — the destination.
- **Toast** — summary appears at the top: `Weather route: 12.3 nm, ETA
  14:52 (+2h37)`.

The route is a one-shot overlay; it doesn't drive the autopilot or set a
SignalK course destination. Use it to decide, then *Navigate Here* on the
waypoint you actually want.

## Tuning knobs

The defaults are tuned for coastal sailing (15-30 nm, light-to-moderate
wind). You can tweak them by editing `IsochroneRouter.Options` at the
call site in `Map.razor.RouteWithWindHere`:

| Option                  | Default | What it controls                                     |
|-------------------------|---------|------------------------------------------------------|
| `StepMinutes`           | 10      | Time between isochrones. Shorter = finer resolution, more CPU. |
| `BearingDegStep`        | 10      | Candidate bearings per expansion (36 at 10°). Finer = better tack angle. |
| `AngularSectorDeg`      | 5       | Sector bin for pruning. Smaller = more candidates survive, more CPU. |
| `MaxSteps`              | 72      | Horizon cap. 72 × 10 min = 12 h.                     |
| `ReachNauticalMiles`    | 0.4     | How close to declare arrival.                        |
| `MinBoatSpeedKn`        | 0.2     | Drops no-go bearings (polar returns under this).     |

## How the math works (`OnaPlotter/Utilities/IsochroneRouter.cs`)

Classical isochrone expansion. At each time step:

1. For every point on the current reachable frontier, look up the local
   wind.
2. For every candidate bearing (0-350° in 10° steps), compute the True
   Wind Angle and ask the boat polar for target speed. No-go bearings
   (polar returns `null`) and near-zero speeds are filtered out.
3. Advance the point by `speed × dt` along the bearing. All new points
   form the next reachable set.
4. **Sector pruning**: bin the new points by their bearing from the
   start in 5° sectors; keep only the furthest point per sector. This
   is what stops the candidate set growing exponentially; the envelope
   of farthest reaches is preserved.
5. Before each expansion, check whether any frontier point can reach
   the destination directly within a single `dt`. If yes, sail the final
   leg straight in and we're done. This handles destinations that fall
   between isochrone boundaries (otherwise the algorithm overshoots and
   never terminates).

A local equirectangular projection is used. Accurate to a fraction of a
percent within the default 12-hour horizon.

## Forecast data

[Open-Meteo](https://open-meteo.com/) global forecast API:

- Free, no API key, CORS-friendly (the app can call it direct from the
  browser with no SignalK-server plugin in the middle).
- `wind_speed_10m` in knots, `wind_direction_10m` in degrees FROM, at
  hourly resolution.
- We pre-fetch 24 hours at the *starting point* only; the router
  applies that same forecast everywhere along the route.

This last bit is a simplification: real wind varies spatially. For a 20
nm hop the approximation is fine (the wind changes slowly compared to
how long we're in any region); for a multi-day crossing you'd want to
sample a grid and interpolate. The `WindAt` delegate in `RouteWithWindHere`
is the seam where you'd add that.

## Limitations

- **Single-point forecast** — spatial variation is ignored. Good for
  ≤30 nm hops, rough for longer routes.
- **Currents** — not modelled. The router assumes pure wind-driven
  sailing. Against-current adds an apparent downwind bias we don't
  capture.
- **No-go zone** — whatever your polar says. If your CSV stops at TWA
  40°, beat angles below that return no speed and the router finds a
  tacking path. If your polar allows unreasonably tight angles (e.g.
  extrapolated), the router will take them.
- **Tidal gates / obstacles** — not known to the router. It will
  cheerfully sail through land if the chart says so. Check the route
  visually before using it.
- **No refresh** — the line is static. If the wind shifts an hour
  later, re-run from the new position.

## Where things live

| Concern                    | File                                                  |
|----------------------------|-------------------------------------------------------|
| Isochrone algorithm        | `OnaPlotter/Utilities/IsochroneRouter.cs`             |
| Weather fetcher            | `OnaPlotter/Services/Api/WeatherForecastApi.cs`       |
| Forecast model             | `OnaPlotter/Models/WindForecast.cs`                   |
| Route result               | `OnaPlotter/Models/WeatherRoute.cs`                   |
| Polar lookup               | `OnaPlotter/Services/PolarService.cs`                 |
| UI entry point             | `Map.razor.RouteWithWindHere` + context-menu item     |
| Map rendering              | `wwwroot/js/leafletInterop.js:setWeatherRoute`        |
| Tests                      | `IsochroneRouterTests.cs`, `WeatherForecastApiTests.cs` |

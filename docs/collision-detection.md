# Collision detection

OnaPlotter runs a proper Closest-Point-of-Approach (CPA) model rather than a
proximity beeper. This doc covers what the captain sees, the math behind it,
the tuning knobs, and the edge cases it handles.

## What you see on the map

```
                      target (red, in guard zone)
                          ▼
                          ▲  ← 60 s fading trail
       ┌─────────── "0.34 nm · T-8m"  ← midpoint label
       │                  ▲
       │                  │  dashed red from target to its CPA point
       ▼                  ▼
   own-CPA ●          target-CPA ●
       ▲
       │  dashed red from own boat to its CPA point
       │
  ┌────●────┐  ← own boat (magenta arrow)
  │amber    │
  │ guard   │  ← translucent ring at the configured CPA radius
  │ zone    │
  └─────────┘
```

**Own boat** — magenta arrow pointing along heading. A dashed pink vector ahead
shows a 5-minute projection at current SOG/COG.

**Guard-zone ring** — amber circle around own boat at the configured CPA
radius. Hidden when the radius is zero (alarm disabled).

**AIS vessels** — triangles coloured by ship type. A 60-second fading trail
shows recent positions. Vessels in the guard zone turn red; vessels in the
"advisory" band (up to 2× the radius / 2× the lookahead) turn amber.

**Crossing-situation lines** — drawn only when a target's projected CPA falls
inside 2× the guard zone:

1. A dashed line from **own boat** to where own boat will be at the CPA
   timestamp (*own CPA point*).
2. A dashed line from the **target** to where it will be at the same
   timestamp (*target CPA point*).
3. A midpoint label showing the CPA distance in nautical miles and TCPA as
   minutes.

Red inside the guard zone (alarm active), amber between 1× and 2× (advisory,
no alarm).

## The alarm

The top banner surfaces the most pressing threat. For CPA it reads:

> CPA  *BOAT NAME*: CPA 0.34nm in 8min

Two buttons:

- **Click anywhere in the banner** — dismisses the current alarm. If the
  threat is still active on the next tick, the banner comes back.
- **Snooze 10 m** — mutes **this specific MMSI** for 10 minutes. Another
  dangerous vessel will still trigger the alarm. The snooze is in-memory only,
  cleared by a page reload — a new watch is a fresh start.

Audio is orchestrated by `wwwroot/js/audioAlert.js` and uses different pulse
rates for `danger` (1 s, used by CPA and SHALLOW) and `warn` (3 s, used by
WIND SHIFT). A single looped tone so it's obvious to a sleeping captain.

## Tuning

Settings → Alarms:

| Setting               | Default    | What it does                                  |
|-----------------------|------------|-----------------------------------------------|
| Guard Zone (CPA)      | 0.5 nm     | Alarm fires if projected CPA is below this.   |
| Guard Zone Lookahead  | 10 min     | Only vessels whose CPA happens within this window trigger. |
| Depth Alarm           | 3 m        | Overrides CPA in the banner priority order.   |
| Wind Shift Alarm      | 15° / 5 min| TWD change over 5 min, advisory-level (warn). |

All persisted to `localStorage` via `IAppSettings`; they take effect live —
no reload needed. The JS side is kept in sync via `setGuardZone(radiusNm,
lookaheadMin)`.

## How the math works (`OnaPlotter/Utilities/Cpa.cs`)

For short-range crossings — which is every scenario we care about — a local
equirectangular projection is accurate to a fraction of a percent. Polar
regions are the exception (see the *Limitations* section below).

Given two vessels with positions in degrees and (COG, SOG) in (radians from
north, m/s), the math is:

1. Pick a local origin at the midpoint of the two vessels, project both
   positions to metres using `metresPerDegLon = 111_320 · cos(midLat)`.
2. Build 2-D velocity vectors from (COG, SOG):
   `v = (sin(COG) · SOG, cos(COG) · SOG)`.
3. Relative position `dp = own - target`; relative velocity `dv = vₒ − vₜ`.
4. Minimise `|dp + dv·t|²` analytically:
   `t* = −(dp · dv) / (dv · dv)`.
5. If `t* < 0`, the CPA has already happened — vessels are diverging, return
   `null`.
6. Otherwise plug `t*` back in:
   `cpa = |dp + dv·t*|` (metres → nm by dividing by 1852),
   `tcpa = t*` (seconds → minutes by dividing by 60).

Two edge cases:

- **Both stationary** — return `null` (no approach is happening).
- **Parallel courses at same speed** — `|dv|² ≈ 0`; return the current
  separation with `TCPA = 0` (they're already at their minimum distance).

The SOG stationary threshold is 0.1 m/s (~0.2 kn).

## Auto-muting moored vessels

A harbour tug drifting 20 m on its lines should not keep waking the captain.
`MooredVesselTracker` watches every AIS vessel:

- SOG stays below 0.26 m/s (~0.5 kn) for 120 s straight → vessel is *moored*
  and skipped by the CPA loop.
- SOG climbs back above the threshold → clock resets, vessel becomes a
  candidate again immediately.
- Vessel drops off AIS range → `Cleanup(activeContexts)` evicts its entry so
  the dictionary doesn't grow without bound.

The tracker is a plain C# service with a unit test suite; see
`OnaPlotter.Tests/MooredVesselTrackerTests.cs`.

## Performance

The alarm pipeline is debounced to 1 Hz even if SignalK deltas arrive at 3+
Hz. Work per tick:

- `AisStore.GetVessels()` — a cached array rebuilt only when the backing
  dictionary version changed.
- For each vessel: early-out if position / COG / SOG is null, then the moored
  check (O(1) dictionary lookup), then the snooze check (same), then
  `Cpa.Compute` (constant work). First match raises the alarm and exits.

On a busy coastline with 40 AIS targets that's ~40 constant-time checks per
second — negligible even on a Pi.

## What's drawn in JavaScript

The Leaflet side (`wwwroot/js/leafletInterop.js`) owns the visual crossing
lines so they can update on every position fix without Blazor re-renders.
C# and JS each have their own copy of the CPA math:

- **C# `Utilities/Cpa.cs`** — used by `MainLayout.CheckAlarms` to drive the
  banner, and by `Map.razor.BuildVesselList` to sort the vessel list.
- **JS `geoMath.js:computeCpa`** — used by `updateAisTargets` to colour
  targets and draw crossing lines. Covered by Node's `node:test` suite in
  `geoMath.test.js`.

Both implementations are ported from the same derivation and exercised by
parallel test suites. The JS copy cannot call into Blazor during the AIS
push, which happens at 3 Hz on a fast delta feed.

## Limitations

- **Polar regions** — equirectangular projection assumes `cos(latitude)` is
  well away from zero. Above ~75° latitude the east-west distance scaling
  starts to drift; CPA distances become unreliable though the "converging
  yes/no" answer stays correct. If you're sailing the NW Passage, file an
  issue.
- **Constant-velocity assumption** — both vessels are assumed to hold current
  course and speed. In reality a vessel manoeuvring defeats the forecast.
  This is the same assumption every AIS/ARPA system makes, including
  commercial ECDIS.
- **No radar ARPA** — only AIS today. Mayara radar target integration is
  queued as the next step; the alarm loop is source-agnostic so ARPA targets
  will use the same pipeline when they arrive.
- **No buddy-list integration yet** — the `BuddyListApi` detects
  `sbender9/signalk-buddylist-plugin`, but the UI doesn't yet mark buddies
  specially or exempt them from CPA alarms. On the roadmap.

## Where things live

| Concern                        | File                                                      |
|--------------------------------|-----------------------------------------------------------|
| CPA math (C#)                  | `OnaPlotter/Utilities/Cpa.cs`                             |
| CPA math (JS)                  | `OnaPlotter/wwwroot/js/geoMath.js` (`computeCpa`)         |
| Guard-zone radius / lookahead  | `OnaPlotter/Services/AppSettingsService.cs`               |
| Moored auto-mute               | `OnaPlotter/Services/MooredVesselTracker.cs`              |
| Alarm pipeline (C#)            | `OnaPlotter/Components/Layout/MainLayout.razor` (`CheckAlarms`, `SetAlarm`, `SnoozeActiveAlarm`) |
| Audio (JS)                     | `OnaPlotter/wwwroot/js/audioAlert.js`                     |
| Guard-zone ring + CPA lines    | `OnaPlotter/wwwroot/js/leafletInterop.js` (`drawGuardZone`, `updateCpaLine`) |
| Settings UI                    | `OnaPlotter/Components/Pages/Settings.razor`              |
| Tests                          | `OnaPlotter.Tests/CpaTests.cs`, `MooredVesselTrackerTests.cs`, `geoMath.test.js` |

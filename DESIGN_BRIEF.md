# Theatrical Nuke — Design Brief

> **Product:** Theatrical Nuke
> **Artifact:** Carbon plugin (`.cs`, server-side only)
> **Status:** **Draft v0.2 — decisions in, probes pending.** Q1–Q12 answered; Q13–Q18 open (§13).
> Storyboard is intentionally incomplete past the terminal phase.
> **Last updated:** 2026-10-17
>
> **Read with:** `CARBON_PLUGIN_PLAYBOOK.MD` (process and build order), `CarbonMod.MD` (API surface),
> `Exceptions.md` (mandatory failure standard — not optional for this plugin), `Rust.MD` (engine and
> networking limits), `DontJustRollWithIt.MD` (evidence tags and the ask-before-inventing rule).

---

## 0. How to use this document

This is the product source of truth. It describes **what the plugin is and does**, not how each line
is written. When the implementation and this document disagree, one of them is wrong — say which,
then amend the document in the same change as the code.

- Requirements carry IDs (`FR-xx`) and a MoSCoW priority. Acceptance in §15 references them.
- Anything uncertain is marked `[ASSUMED]` or `[UNKNOWN]` and appears again in §13. Nothing in this
  document may be silently invented; per `DontJustRollWithIt.MD`, an unknown becomes a question, not
  a guess.
- Numbers in the config sketch (§7.1) are **starting points for tuning on a live server**, not
  proven values.
- Every change gets a line in §16.

---

## 1. Vision

A player finds a rare **Nuclear Football**, throws it, and paints a target with pink/purple smoke.
The whole server is told which grid is doomed. Sirens crawl across the map. Far out at sea, on the
opposite side of the island, a submarine-launched missile breaches the surface, lights its boosters
and climbs — and for the next minute every player watches a countdown they cannot stop.

The point is **theatre with a real consequence**: a slow, legible, server-wide event where the
narrative beats (warning → sirens → launch → camera → bus separation → six incoming tracks → one
real detonation) matter more than the damage number. It should be the thing players talk about after
the wipe, and it should never crash, lag-spike or half-complete.

---

## 2. Goals and non-goals

### 2.1 Goals

| # | Goal | Why |
|---|---|---|
| G1 | A single, legible, **server-wide event** from throw to detonation. | The value is shared experience, not personal DPS. |
| G2 | Every beat is **visible and audible** to every connected player, regardless of distance. | A nuke nobody sees is just an explosion. |
| G3 | The sequence is **deterministic and restartable** — same input, same arc, same timings. | Reproducible for testing, tuning and support. |
| G4 | **Administrable**: config UI, debug mode, debug overlay, abort, cooldowns, permissions. | It will be misused and it will need tuning. |
| G5 | **Zero unhandled exceptions**; any failure is contained, logged to the plugin's own file, and degrades visibly. | `Exceptions.md` is the definition of done. |
| G6 | Survives reload/unload mid-flight without orphaning entities or leaving players in a broken camera. | Servers get reloaded at the worst time. |

### 2.2 Non-goals (v1)

- **No client mods, no custom assets streamed to clients.** IL2CPP + EAC make this impossible; we
  compose vanilla prefabs, effects and skins (`Rust.MD` §10–§11).
- **No real physics.** Trajectories are scripted curves, not rigidbody simulation. "110 % speed at
  102 % apogee to the same impact point" is theatre, not orbital mechanics — the brief says so
  explicitly so nobody "fixes" it later.
- **No radiation, EMP or fallout simulation** in v1 (`[UNKNOWN]` whether the engine's damage types
  make this cheap; not required for the experience).
- **Not a generic nuke framework** for other plugins. Internal APIs may be clean, but no public
  contract in v1.
- **Not a PvP balance statement.** Lethality is server-owner policy, expressed in config.

---

## 3. Success criteria

Measurable, checked at the end of each milestone:

1. A player with permission can throw the football and complete the full sequence with **no errors
   in console and no error records in the plugin exception log**.
2. A spectator **600 m from the impact** sees every phase (breach, boost, bus separation, MIRV
   spread, detonation) without pop-in, and hears the sequence.
3. A spectator at the **opposite corner of the map** still hears the sirens and receives the call
   notification (`FR-12`, `FR-14`).
4. During a strike with 50 players online, server **tick time stays within the owner's budget**
   (target: no frame over 2× baseline; measured, not assumed).
5. An admin can run the whole thing **repeatedly in debug mode** (no damage) in under two minutes per
   run, using the overlay to verify target, launch point and all three arcs (`FR-40`–`FR-42`).
6. `c.reload TheatricalNuke` **mid-flight** leaves no orphaned missile, bus, rocket, camera or UI
   entity, and no player stuck in a dead CCTV feed.
7. Every injected fault (missing prefab, missing permission, exception in a phase step) is caught,
   logged once with context, and leaves the server playable.

---

## 4. Cast of objects (glossary)

| Term | Meaning in this brief |
|---|---|
| **Football** | The rare item a player obtains; implemented as a **supply signal** item (`supply.signal`) with a renamed display and optional skin. |
| **Call** | The act of the thrown signal landing and beginning to smoke; this is the moment the strike becomes committed. |
| **Target point** | World position where the smoke is emitted = where the real MIRV detonates. |
| **Launch point** | Computed water position on the opposite side of the map from the target. |
| **Booster** | The submarine-launched missile body (vanilla `nuclear_silo_missile` prefab) from breach to bus separation. |
| **Bus** | The post-boost vehicle spawned at ~95 % apogee, carrying the MIRVs; visible only as a small glinting object. |
| **MIRV** | One of six reentry vehicles: 5 decoys + 1 live. Vanilla `rocket_mlrs` prefab. |
| **Phase** | One of `Idle → Armed → Warning → Breach → Boost → Midcourse → BusFlight → Reentry → Impact → Cleanup`. |
| **Strike** | One complete instance of the sequence, owning its entities, timers and state. |
| **FP overlay** | First-person debug drawing in the admin's 3D view (lines/spheres/text). `[ASSUMED]` "FP" = first-person; confirm in §13 Q7. |

---

## 5. The experience — storyboard

### 5.1 Beat sheet

Times are **after the call** (`t=0`), at `TimeScale = 1.0`. Everything after the call is configurable
(§7.1); the beat *order* is fixed.

| # | t | Beat | Player perceives | Server does |
|---|---|---|---|---|
| 1 | pre | **Acquire** | Finds/earns a Nuclear Football. | Grants the item (loot, admin command or event — §13 Q5). |
| 2 | − | **Throw** | Throws it like a supply signal. | Validates permission, cooldown, concurrency; refuses early if disallowed (`FR-06`). |
| 3 | 0 | **Call** | Signal lands, pink/purple smoke column; **all players** get a notification naming the grid. | Records target, computes launch point, starts the strike state machine. Vanilla supply-drop behaviour suppressed (`Q1`). |
| 4 | 15 s | **Sirens** | Air-raid sirens rise across the map, from several directions; they swell and fade as you move between anchors. | Starts a pulse loop per anchor, broadcast to every client (`FR-14`/`FR-15`). |
| 5 | ~30 s | **Breach** | Far out at sea: a plume, then the missile punches through the surface; water-exit effect; motors ignite. | Spawns booster underwater, propels it clear of the water, switches to boost. |
| 6 | +2 s | **Boost** | Missile climbs, four booster plumes, visible from most of the map; a marker begins to crawl across the map. CCTV feed opens showing the engines from the side. | Kinematic climb along the boost arc; camera parented to the booster; marker parented to the missile; per-frame network updates. |
| 7 | 80 % apogee | **Booster cutoff** | Plumes stop; the missile keeps coasting. | Stops the four nozzle effects (`FR-22`). |
| 8 | 95 % apogee | **Bus separation** | A brief flash; the view cuts to a forward-facing camera inside the bus. The booster is gone. | Spawns bus at the tip, re-parents/re-spawns camera, kills the booster, continues on the bus arc. |
| 9 | apogee | **Midcourse** | A long silent fall; the map turns below. | Bus follows its own arc (102 % apogee, 110 % speed, same impact point). |
| 10 | post-apogee | **Reentry** | Six tracks appear on the map and in the sky; the bus releases them within 0.2 s. | Fires 6 `rocket_mlrs` at 0.2 s spread; 5 toward random points within 50 m of target, 1 exact; a marker on each. |
| 11 | impact | **Detonation** | Flash → fireball → shockwave → mushroom; decoys crack around it. | Applies damage in rings, plays the layered FX, destroys/kills entities in radius over several frames. |
| 12 | +N s | **Aftermath** | Smoke column persists; scorched ground; chat recap. | Cleanup, statistics, cooldown release, log summary. |

### 5.2 Phase state machine

```
Idle
 └─(throw accepted)→ Armed
        └─(signal lands / smoke starts)→ Warning ──15s──► Breach
                                                          └─(clears water)→ Boost
                                                                 └─(80% apogee)→ Coast (effects off)
                                                                        └─(95% apogee)→ BusSeparation
                                                                                └─(apogee)→ Reentry
                                                                                        └─(6 MIRVs away)→ Impact
                                                                                                └─(FX+damage done)→ Cleanup
                                                                                                        └→ Idle
```

Rules:

- Transitions are **one-way** and idempotent; a phase step never runs twice for the same strike.
- **Any phase can jump to `Cleanup`** (abort, unload, timeout, unrecoverable error). Cleanup is the
  only phase allowed to run after a failure.
- A hard **strike deadline** (config, e.g. 180 s) forces `Cleanup` even if a transition never fires —
  the sequence must never hang.

### 5.3 Trajectory model

All arcs are **parametric curves** evaluated against a clock, not physics:

```
u        = clamp01(elapsed / duration)
horizontal = Lerp(start.xz, end.xz, u)          // straight line, no great-circle correction
vertical   = Lerp(start.y, end.y, u) + 4 * H * u * (1 - u)
             where H = apogee - (start.y + end.y) / 2
```

- `u = 0.5` is the apogee for a level start/end; the curve guarantees the configured `ApogeeY`.
- Orientation is `LookRotation(velocity)` with a per-phase pitch offset so the model reads correctly.
- **Boost**: fixed duration, `ApogeeY` from config (default 1000).
- **Bus**: re-solved to the *same* impact point with `apogee × 1.02` and `duration ÷ 1.10`.
- MIRVs are **not** on this curve; they are real `MLRSRocket` projectiles aimed at their own points.

This is deliberately non-physical. It is deterministic, tunable and cheap — and it is what makes
"camera at 95 % of apogee" a well-defined moment.

---

## 6. Functional requirements

Priorities: **M** must (v1), **S** should (v1 if cheap), **C** could (post-v1), **W** won't (v1).

### 6.1 Acquisition and the football

| ID | Pri | Requirement |
|---|---|---|
| FR-01 | M | The football is a **supply signal item** (`supply.signal`) with a configurable display name and optional skin. |
| FR-02 | M | Acquisition is configurable: at minimum an admin grant command; ideally a loot/airdrop chance. `[UNKNOWN]` exact loot-table mechanism — §13 Q5. |
| FR-03 | M | Throwing is permission-gated (`theatricalnuke.call`) and cooldown-gated (per player and global). |
| FR-04 | M | A refused throw (no permission, cooldown, strike already running, invalid location) returns the item **and** tells the player why. |
| FR-05 | S | The football is consumed on a valid call, not on the throw animation. |
| FR-06 | M | Validation happens **before** any entity is spawned or any global message is sent. |

### 6.2 Call and warning

| ID | Pri | Requirement |
|---|---|---|
| FR-10 | M | On landing, the smoke is **pink/purple** and its position is the target point. |
| FR-11 | M | Vanilla supply-signal behaviour (cargo plane + drop) is suppressed. `Q1` |
| FR-12 | M | Every connected player receives a notification stating a nuclear strike has been called and the **grid reference** of the target. |
| FR-13 | M | The notification is delivered through a configurable channel (chat, GUI banner, or both). |
| FR-14 | M | At `t = Sirens.DelaySeconds` (default 15), sirens start at **configured world anchors**. Each anchor **re-emits** the sound on a pulse interval for the whole siren duration, so the sound is spatial (≈1900 m audible range per anchor) and a player who joins or moves into range later hears the next pulse. |
| FR-15 | M | Anchors are configurable and spaced so their ranges overlap; `PulseIntervalSeconds` is shorter than the sound's length so the siren is continuous. `Q2` |
| FR-16 | S | A second notification fires at breach and at MIRV release. |
| FR-17 | M | The caller may **not throw from inside a safe zone**; the target itself may be anywhere, including a safe zone (Q10). |

### 6.3 Launch

| ID | Pri | Requirement |
|---|---|---|
| FR-20 | M | The launch point is **in water**, on the **opposite side of the map** from the target, at a configurable fraction of the map radius and a minimum distance from shore. |
| FR-21 | M | The booster spawns **underwater** and is driven vertically until it is fully clear of the surface, then the boost phase begins. |
| FR-22 | M | A configurable water-exit effect/audio cue plays at the moment of breach. `Q3` (asset) |
| FR-23 | M | The launch point is validated (water, inside map bounds, not in a safe zone); if invalid, the strike aborts **before** any global message. |

### 6.4 Boost

| ID | Pri | Requirement |
|---|---|---|
| FR-25 | M | The booster uses `assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab` (`[UNKNOWN]` entity class — probe R1). |
| FR-26 | M | Four fire/smoke effects are attached at the booster nozzles for the duration of the boost. `Q4` (assets) |
| FR-27 | M | Apogee is configurable (default `Y = 1000`) and the boost arc targets the call position. |
| FR-28 | M | The booster is visible and audible to all players (broadcast effects at key beats). |
| FR-29 | M | A **map marker tracks the missile** through boost, midcourse and reentry, visible to every player (Q6). Implemented with a plugin-spawned `genericradiusmarker` — vanilla MLRS markers are owner-only (§9.1). |

### 6.5 Midcourse and bus

| ID | Pri | Requirement |
|---|---|---|
| FR-30 | M | At **80 % of apogee** the four booster effects stop. |
| FR-31 | M | At **95 % of apogee** the bus is spawned inside the missile tip and continues on its own arc. |
| FR-32 | M | The bus arc hits the **same impact point** with `apogee × 1.02` and `speed × 1.10`. |
| FR-33 | M | The booster is removed at hand-off. |
| FR-34 | M | The hand-off is **atomic from the player's perspective**: no frame where no missile exists and no frame where two exist at the same place. |
| FR-35 | S | A short separation flash/effect masks the swap. `Q4` |

### 6.6 Reentry and MIRV

| ID | Pri | Requirement |
|---|---|---|
| FR-40 | M | After apogee, the bus releases **6 MIRVs within 0.2 s** of each other. |
| FR-41 | M | Each MIRV uses `assets/content/vehicles/mlrs/rocket_mlrs.prefab` and is aimed at its own point. |
| FR-42 | M | 5 decoys target uniformly random points within **50 m** of the target; 1 live MIRV targets the exact point. |
| FR-43 | M | Decoys behave exactly like normal MLRS rockets (impact, explode, damage the ground structures they hit). |
| FR-44 | M | Each MIRV carries a plugin-spawned marker so **all six tracks are visible to all players** (Q6 = A). Vanilla `MapMarkerMLRSRocket` cannot do this: it networks only to the rocket's `OwnerID` (§9.1). |
| FR-45 | S | The live MIRV is visually indistinguishable from the decoys until detonation. |

### 6.7 Detonation

| ID | Pri | Requirement |
|---|---|---|
| FR-50 | M | The live warhead produces a layered nuclear effect: flash → fireball → shockwave → stem → mushroom canopy → dust ring. `Q7` |
| FR-51 | M | Damage is applied in **rings** (lethal / severe / light), configurable per ring, with structure and player damage tunable separately. |
| FR-52 | M | Entity destruction is **time-sliced across frames** so a city-sized hit does not stall a tick. |
| FR-53 | M | The explosion and its audio are delivered to all clients (broadcast). |
| FR-54 | S | Crater/scorch decal, screen shake, and a delayed low-frequency boom proportional to distance. |
| FR-55 | C | Radiation/fallout zone (`[UNKNOWN]` engine support — not v1). |

### 6.8 Camera system

| ID | Pri | Requirement |
|---|---|---|
| FR-60 | M | A CCTV camera is attached to the **outside of the booster**, angled down at the engines, viewable by players on a computer station. |
| FR-61 | M | At bus separation the booster camera is destroyed and a new camera is attached **inside the bus**, facing forward, at a configurable local offset/rotation. |
| FR-62 | M | The swap is seamless: a viewer never sees a black or default feed during the hand-off (`FR-34`). |
| FR-63 | M | If the camera entity dies or the viewer disconnects, no player is left in a broken UI state. |
| FR-64 | M | No permission gate: **any player can watch** (Q8). The camera is a normal networked entity; a viewer selects it from a computer station (see FR-65). Config can disable the camera system entirely. |
| FR-65 | M | The view is available from **any** computer station, not only one on the missile (Q8, Q14). |

### 6.9 Admin and debug

| ID | Pri | Requirement |
|---|---|---|
| FR-70 | M | Config UI (Carbon CUI) exposing every §7.1 key that is safe to change live. |
| FR-71 | M | Debug mode: verbose logging to console and the exception log, extra state transitions logged, no damage. |
| FR-72 | M | FP debug overlay: when enabled **and** the caller is an admin, draw target, launch point, and the boost / midcourse / terminal arcs in the 3D world. |
| FR-73 | M | A command to **clear** the debug drawing immediately (`Q9`: `ddraw.clear` support unverified — fallback is to stop the refresh loop and let drawings expire). |
| FR-74 | M | `abort` command: cancels the active strike, runs `Cleanup`, restores any affected players. |
| FR-75 | S | `test` command: run the sequence with damage disabled from a chosen point. |

### 6.10 Lifecycle, persistence and recovery

| ID | Pri | Requirement |
|---|---|---|
| FR-80 | M | A strike in flight does **not** survive a plugin reload as a live strike; it is cleaned up and reported. |
| FR-81 | M | On load, scan for entities owned/tagged by this plugin and remove leftovers (crash/reload recovery). |
| FR-82 | M | `Unload` kills all strike-owned entities, timers, cameras, UI and subscriptions, each isolated (`Safe.Unload`). |
| FR-83 | M | Cooldowns persist across reloads (data file), with a version field. |

---

## 7. Configuration

### 7.1 Config sketch

Starting values only — tune on the live server. Asset paths supplied by the operator on 2026-10-17 are
filled in; the rest are marked `ASSET-TBD`.

```json
{
  "General": {
    "Enabled": true,
    "Debug": false,
    "DebugOverlay": false,
    "TimeScale": 1.0,
    "MaxConcurrentStrikes": 1,
    "StrikeDeadlineSeconds": 180
  },
  "Football": {
    "ItemShortname": "supply.signal",
    "DisplayName": "Nuclear Football",
    "SkinId": 0,
    "Permission": "theatricalnuke.call",
    "GlobalCooldownSeconds": 3600,
    "PerPlayerCooldownSeconds": 86400,
    "AirdropChance": 0.0,
    "RequireOutsideSafeZoneToThrow": true,
    "AllowTargetInsideSafeZone": true
  },
  "Sirens": {
    "Enabled": true,
    "DelaySeconds": 15,
    "DurationSeconds": 45,
    "PulseIntervalSeconds": 8,
    "AudibleRangeMeters": 1900,
    "Effect": "assets/bundled/prefabs/modding/sound/air raid/airraidsound.prefab",
    "Anchors": [
      { "x": 0.0, "y": 0.0, "z": 0.0 }
    ]
  },
  "Launch": {
    "OppositeSideFraction": 0.85,
    "MinDistanceFromShoreMeters": 300,
    "SpawnDepthMeters": 40.0,
    "RiseSpeedMetersPerSecond": 30.0
  },
  "Trajectory": {
    "ApogeeY": 1000.0,
    "BoostEndAltitudeFraction": 0.80,
    "BusSpawnAltitudeFraction": 0.95,
    "BusApogeeMultiplier": 1.02,
    "BusSpeedMultiplier": 1.10,
    "BoostDurationSeconds": 32.0,
    "BusDurationSeconds": 26.0
  },
  "Visuals": {
    "MissilePrefab": "assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab",
    "BusPrefab": "assets/bundled/prefabs/modding/events/twitch/nuclear_warhead.prefab",
    "BoosterNozzleEffect": "assets/prefabs/npc/m2bradley/oilfireball2.prefab",
    "BoosterNozzleCount": 4,
    "WaterExitEffect": "assets/content/effects/explosions/underwater/explosion_underwater_surface_large.prefab",
    "SeparationEffect": "ASSET-TBD",
    "TargetSmokeEffect": "ASSET-TBD"
  },
  "Marker": {
    "Enabled": true,
    "Prefab": "assets/prefabs/tools/map/genericradiusmarker.prefab",
    "MissileRadiusMeters": 25.0,
    "MirvRadiusMeters": 8.0,
    "Color": "#FF33D9",
    "Alpha": 0.55
  },
  "Camera": {
    "Enabled": true,
    "BoosterOffset": { "x": 2.0, "y": 0.0, "z": 0.0 },
    "BoosterPitch": 60.0,
    "BusOffset": { "x": 0.0, "y": 0.0, "z": 0.5 },
    "BusPitch": 0.0
  },
  "Mirv": {
    "Count": 6,
    "DecoyCount": 5,
    "LaunchSpreadSeconds": 0.2,
    "DecoyRadiusMeters": 50.0,
    "RocketPrefab": "assets/content/vehicles/mlrs/rocket_mlrs.prefab",
    "SpeedMetersPerSecond": 90.0
  },
  "Blast": {
    "LethalRadiusMeters": 60.0,
    "SevereRadiusMeters": 110.0,
    "LightRadiusMeters": 160.0,
    "StructureDamageScale": 1.0,
    "EntityKillRadiusMeters": 90.0,
    "EntitiesPerFrameBudget": 200,
    "EffectPrefabs": ["ASSET-TBD"]
  }
}
```

`Sirens.AudibleRangeMeters` is documentation and anchor-spacing guidance, not a plugin parameter —
falloff belongs to the sound asset. `PulseIntervalSeconds` must be ≤ the sound's own length (probe R6
measures it).

### 7.2 Config UI

- Carbon CUI panel, opened by `/nuke config` for admins (`authLevel 2` or a permission).
- Grouped by the sections above; each field labelled with its config key and default.
- **Live-editable**: timings, radii, debug toggles, camera on/off, marker on/off.
- **Not live-editable** (requires reload, marked in the UI): prefabs, item shortname, permission
  strings.
- Every change is written to the data file and echoed to console with the old → new value.
- The UI is read-only in debug-off mode for non-admins; never render it for players without the
  permission.

---

## 8. Debug tooling

### 8.1 Debug mode (config `General.Debug`)

- Verbose phase logging: every transition with `strikeId`, phase, `t`, and the computed next
  waypoint.
- **Damage disabled**; FX, cameras and messaging still run, so the show can be rehearsed.
- Emits to console **and** the plugin exception log at `INFO`/`DEBUG` severity, rate-limited.
- Never changes timing behaviour — debug must be the same show, louder.

### 8.2 FP debug overlay (config `General.DebugOverlay`)

Drawn **only** when the strike was triggered by an admin, and only to that admin's client:

| Element | Colour (proposal) | Meaning |
|---|---|---|
| Sphere + text | Red | Target point (from smoke). |
| Sphere + text | Cyan | Computed launch point. |
| Line | Yellow | Vertical breach/rise path. |
| Arc | Orange | Boost arc (sampled polyline). |
| Arc | Green | Bus arc. |
| Lines | Magenta | Each MIRV track from bus to its impact point, with the 50 m decoy disc. |
| Text | White | `phase / t / alt / speed` readout at the missile position. |

Implementation: `player.SendConsoleCommand("ddraw.line", …)` / `ddraw.arrow` / `ddraw.sphere` /
`ddraw.text` (verified pattern, §9.1), refreshed at a low rate (e.g. 2 Hz) so the drawing stays live
without spamming the client.

### 8.3 Clear command

`/nuke debug clear`
1. Stops the refresh loop.
2. Attempts `ddraw.clear` if the client supports it (`Q9`).
3. Reports how many primitives will simply expire and when.

Because `ddraw` primitives have a finite lifetime, "clear" is guaranteed even if no clear command
exists — the worst case is a few seconds of stale lines, which is acceptable and must be stated in
the command's reply.

---

## 9. Technical approach and feasibility

### 9.1 Verified building blocks

Checked against decompiled `Assembly-CSharp` and the prefab/entity index; **not yet compiled into
this plugin** (no .NET toolchain in the build environment — every snippet is source-verified, not
compiled).

| Need | Verified fact | Status |
|---|---|---|
| Siren asset path | **Corrected:** the folder is `air raid` (with a space). Run 1's underscore path was a transcription error on our side and produced `unknown effect`. | ✅ verified (R4 run 2: no pool error with the spaced path) |
| Map-wide effect delivery | `Effect.server.Run(name, pos, normal, connection, broadcast: true)` → `EffectNetwork.Send` → `BaseNetworkable.GlobalNetworkGroup.subscribers` = **every connected client**. **But** a string name must already be in the engine's string pool: an asset path not in the pool logs `unknown effect` and is dropped silently (R6 run 1). `Run` returns nothing, so failure is not detectable in code. | ⚠️ broadcast verified; **siren path failed** (D-2) |
| MLRS MIRVs | `rocket_mlrs.prefab` = `MLRSRocket : TimedExplosive, SamSite.ISamSiteTarget`, has `ServerProjectile`; `ProjectileImpact` explodes and broadcasts ground FX; `ServerInit` creates a map marker and broadcasts launch FX. | ✅ verified |
| Aiming an MIRV | `ServerProjectile.InitializeVelocity(Vector3)` is public; `speed`, `gravityModifier`, `initialVelocity` are public fields. | ✅ verified |
| Vanilla MLRS reference numbers | `MLRS.rocketSpeed`, `MLRS.rocketBaseGravity`, `targetAreaRadius = 30 m`, `minRange = 200 m`. | ✅ verified |
| Explosion control | `TimedExplosive.explosionRadius`, `damageTypes`, `explosionEffect`, `SetDamageScale(float)`. | ✅ verified |
| Football item | `grenade.supplysignal.deployed.prefab` = `SupplySignal : TimedExplosive`. | ✅ verified |
| Missile prefab exists | `assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab` is present in the client asset tree. | ✅ file exists |
| Missile prefab is spawnable | **Verified NOT spawnable as an entity.** Root components are `Transform` + `MeshCollider`; `CreateEntity` returns null ("prefab isn't an entity"). Needs a carrier entity (D-1). | ❌ probe R1 run 1 |
| Bus prefab spawnable | `nuclear_warhead.prefab` spawns as a plain `BaseEntity` (`Spawn()` OK). Visible geometry unconfirmed — no renderer on the root. | ✅ spawns; ⚠️ visuals (R8) |
| CCTV on a moving parent | `CCTV_RC` exists (`cctv.static`, `cctv_deployed`); parenting to a moving entity and keeping the feed alive is **unverified**. | ⚠️ probe R2 |
| CCTV discovery | A station only auto-gathers **static** cameras within `autoGatherRadius`; anything else must be added by identifier (`ForceAddBookmark`). Confirmed in `ComputerStation.GatherStaticCameras`. | ✅ verified |
| Siren via effect route | With the correct path there is **no** pool error, but the siren was **not audible at 1 m or 2000 m** (R4 run 2). The effect route does not produce audible sound for this asset. | ❌ R6 effect route rejected; R6b entity route is the test |
| Vanilla MLRS map marker is private | `MapMarkerMLRSRocket.ShouldNetworkTo` returns `player.userID == OwnerID` and `limitNetworking = true`: the marker is visible **only to the rocket's owner**, never to the server. Six vanilla markers cannot be shown to everyone. | ✅ verified — changes FR-29/FR-44 |
| Generic map marker | `genericradiusmarker.prefab` = `MapMarkerGenericRadius` with public `radius`, `color1`, `color2`, `alpha` and `SendUpdate()`; no `ShouldNetworkTo` override, so it is not owner-limited. | ✅ verified (visibility still needs R7) |
| `oilfireball2` is an entity | `oilfireball2.prefab` = `FireBall : BaseEntity` — `damagePerSecond = 2`, `radius = 0.5`, life 20–40 s, spreads fire. Spawns and parents (R9 run 1); neutering applied; look/spread not yet reported. | ⚠️ probe R9 |
| Debug drawing | `ddraw.line/arrow/sphere/text/box` via `SendConsoleCommand` (duration + colour). | ✅ verified pattern |
| Clearing debug drawing | `ddraw.clear` clears the lines correctly on the operator's client (run 2). | ✅ verified |
| Supply signal smoke | `SupplySignal.Explode` sets `Flags.On` and sends a network update; `smokeEffectPrefab` is referenced but not used in this file. Run 2's probe **killed** the signal, which removed the smoke. The R5 prefix must replicate the flag and network update without the plane. | ⚠️ R5 v2 |
| Cargo plane suppression | Run 2: intercept fired; no plane seen after several minutes. | ✅ verified (plane); smoke pending |
| CCTV identifier | `CCTV_RC` inherits from `PoweredRemoteControlEntity`; its identifier source is not in the decompile. Run 2: our camera reported `COBALT1`, and a station with that identifier opened a **large oil-rig camera**. Identifiers are probably not unique. | ❌ R2 v2 (duplicate survey) |
| Marker radius units | `genericradiusmarker` at `radius = 60` tinted the **whole map** pink (run 2). Radius is not metres on the client. | ❌ R7 sweep |
| FireBall plume neutering | Setting `spreadSubEntity = null` causes a `NullReferenceException` in `FireBall.TryToSpread` (run 2). Setting `generation = 1000` short-circuits the spread test instead. | ✅ fix in v0.3, to verify |
| Bus visible | `nuclear_warhead.prefab` renders (run 2). | ✅ verified |
| Vanilla MLRS map marker is private |

### 9.2 Proposed architecture

One namespace, small components, each phase a step function driven by a single scheduler:

```
TheatricalNuke (CarbonPlugin)
├── StrikeController        // owns the state machine, one per strike; no static state
├── Trajectory              // pure math: BuildArc(start, end, apogee, duration) -> sample(t)
├── MissileRig              // booster/bus spawn, parenting, network updates, effects
├── CameraRig               // CCTV spawn/attach/swap/teardown
├── MirvLauncher            // 6 rockets, spread, decoy sampling
├── Blast                   // ring damage, time-sliced entity kill, layered FX
├── StrikeMessaging         // notifications, sirens, chat/GUI
├── StrikeDebug             // ddraw overlay, clear
└── StrikeStore             // cooldowns + versioned data file
```

Rules:

- **No static mutable state.** Reload safety depends on it (`FR-80`–`FR-82`).
- The strike owns every entity it spawns and kills them in `Cleanup` — including on abort and unload.
- All entry points go through `Safe.*` with the plugin's `ExceptionLog` (`Exceptions.md` §9).
- Effects and messages are emitted from **one** place per beat so the timeline is auditable.

### 9.3 Risks

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | **Confirmed:** missile prefab is not an entity; `CreateEntity` returns null. | Blocks spawning the visual directly. | Decision D-1 (M0_PROBES Part E): invisible carrier `BaseEntity` with the missile prefab as a child; verify the child renders. |
| R2 | CCTV cannot follow a fast-moving parent smoothly, or requires power/IO. | Camera requirement fails. | Probe with a spawned `cctv.static` parented to a moving dummy; measure. Fallback: drive the camera as a separate entity following the rig. |
| R3 | `ddraw.clear` does not exist. | Debug lines linger. | Accept expiry-based clear (§8.3). |
| R4 | **New:** `Effect.server.Run` cannot play the air-raid asset (not in the effect string pool). Client attenuation still unknown. | Sirens silent — the Q2 mechanism as written does not work. | Decision D-2 (M0_PROBES Part E): test the asset as an entity (R6b), else audition pooled effects. |
| R5 | Large blast = thousands of entity kills in one tick. | Lag spike / disconnect. | Time-sliced kill budget (`Blast.EntitiesPerFrameBudget`). |
| R6 | `FireBall` plumes damage, spread fire, or detach from a moving parent. | Damage where none should be; plumes left behind. | Neuter each ball (damage 0, radius 0, no spread) and parent it; verify with R9. Fallback: pure FX prefab attached via `Effect.server.Run`. |
| R7 | Reload mid-flight. | Orphans, stuck cameras. | Recovery scan on load; `Cleanup` on unload; abort command. |
| R8 | Prefab path changes after a Rust update. | Silent no-show. | Validate all prefabs at load; refuse to arm and log loudly. |
| R9 | `nuclear_warhead.prefab` spawns but may have no visible geometry. | The bus is invisible. | Check visually (R8); fallback is a carrier entity with a visible child. |
| R11 | Supply-signal smoke is lost when the probe kills the signal (run 2). | The target is not painted pink, breaking FR-10. | R5 v2 keeps `Flags.On` and the network update, and skips only the plane. |
| R10 | Safe zones nullify the strike (vanilla rockets self-destruct on `TriggerSafeZone`, damage is blocked). | Q10's "real consequences" may not happen. | Verify in R10; decide between fizzle / deflection / bespoke handling (Q17). |

### 9.4 FX research pass (required before M5)

Q7 = C: the nuclear detonation will not be assembled from guesswork. The pass will:

1. Enumerate candidate vanilla prefabs by effect family — flash, fireball, shockwave, smoke column,
   mushroom canopy, dust ring, crater decal, low-frequency boom.
2. Audition each with `/probe fx <prefab>` on the target server at night and at day, from 100 m,
   600 m and 2 km.
3. Propose a layered composition with per-layer scale, delay and lifetime, plus a fallback if a layer
   fails to load.
4. Record the final set in §7.1 `Blast.EffectPrefabs` and §10.

No blast tuning happens before this pass; the config keeps `ASSET-TBD` until then.

---

## 10. Asset and effect register

Paths supplied by the operator on 2026-10-17 are marked ✅. Anything still `ASSET-TBD` is a
placeholder that must be replaced before release; the load-time validator refuses to arm the strike
if a required prefab is missing.

| Slot | Config key | Status | Notes |
|---|---|---|---|
| Missile body | `Visuals.MissilePrefab` | ✅ given | `assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab` |
| Bus | `Visuals.BusPrefab` | ✅ given | `assets/bundled/prefabs/modding/events/twitch/nuclear_warhead.prefab` — spawnability unconfirmed (R8) |
| Booster plumes ×4 | `Visuals.BoosterNozzleEffect` | ✅ given | `assets/prefabs/npc/m2bradley/oilfireball2.prefab` — a `FireBall` entity, needs neutering (R9) |
| Water-exit cue | `Visuals.WaterExitEffect` | ✅ given | `assets/content/effects/explosions/underwater/explosion_underwater_surface_large.prefab` |
| Separation flash | `Visuals.SeparationEffect` | ❌ TBD | Operator left this to us: pick during the FX pass (§9.4). |
| Air-raid siren | `Sirens.Effect` | ✅ given | `assets/bundled/prefabs/modding/sound/air raid/airraidsound.prefab` |
| Target smoke | `Visuals.TargetSmokeEffect` | ❌ TBD | Pink/purple smoke (Q13). |
| Map marker | `Marker.Prefab` | ✅ chosen | `assets/prefabs/tools/map/genericradiusmarker.prefab` |
| Nuke FX layers | `Blast.EffectPrefabs` | ⏳ FX pass | Q7 = C: research pass before choosing. |
| MIRV rocket | `Mirv.RocketPrefab` | ✅ given | `assets/content/vehicles/mlrs/rocket_mlrs.prefab` |

**Rule:** every prefab is validated at load (exists, loads, type is what we expect). A missing asset
disables the strike with a clear console message — it never fails silently at `t = 55 s`.

---

## 11. Balance, performance and server-health budgets

| Budget | Target | How enforced |
|---|---|---|
| Concurrent strikes | 1 (configurable) | Refuse the call, refund the item. |
| Entities spawned per strike | ≤ 15 (booster, bus, 6 MIRVs, cameras, markers) | Fixed by design; assert in debug. |
| Effects per second | ≤ 20 | Batched per beat, not per frame. |
| Entity kills per frame at impact | ≤ 200 | Time-sliced queue. |
| Tick impact | No frame > 2× baseline | Measure with `c.profile`/`mono-profiler` on the live server. |
| Strike wall-clock | ≤ 180 s | Deadline → `Cleanup`. |
| Cooldowns | Global 1 h / player 24 h (starting values) | Data file, survives reload. |

Damage philosophy: the **lethal ring kills players**; the severe ring kills soft structures and
heavily damages bases; the light ring is cosmetic + minor damage. Owners can set all three to zero
for a pure theatrical event, which is a supported configuration.

---

## 12. Failure behaviour

This plugin is a **multi-minute, multi-phase, entity-spawning process** — the highest-risk shape a
Rust plugin can have. It follows `Exceptions.md` without exception:

- **Fail closed at the call**: if validation, permission, config or prefab checks fail, nothing is
  spawned and no global message is sent.
- **Fail open mid-flight**: a non-critical failure (effect missing, marker failed, camera died) is
  logged, the beat is skipped, and the sequence continues.
- **Fail fast at load**: missing prefabs/config disable the plugin's strike path with a loud error,
  rather than failing at the worst possible moment.
- **Every boundary guarded**: hook, command, timer, async continuation, CUI callback, Harmony patch
  (if any), `Load()`, and every phase step.
- **Separate log**: `carbon/logs/TheatricalNuke/TheatricalNuke_exceptions-YYYY-MM-DD.log` (+
  `.jsonl`), fingerprint-deduped and rate-limited.
- **One line per beat** in the log at INFO, so a support ticket can be reconstructed from timings
  alone.
- **Global guard installed at load**, removed at unload (`PluginGuard`).

---

## 13. Decision register

Answers received 2026-10-17 are recorded here; the working answer sheet is `M0_PROBES.md` Part A.

### 13.1 Resolved

| # | Decision | Answer | Consequence |
|---|---|---|---|
| Q1 | Vanilla supply drop on throw | **Suppress** (A) | Harmony prefix on `SupplySignal.Explode`, verified by R5. |
| Q2 | Siren model | **Spatial anchors with overlap**, sound usable ≈1900 m from spawn | Anchors re-emit on a pulse interval so late joiners and players moving into range hear the next pulse (FR-14/15). |
| Q3 | Water-exit cue | `explosion_underwater_surface_large.prefab` | Fires at breach. |
| Q4 | Bus / plumes / separation | Bus `nuclear_warhead.prefab`; plumes `oilfireball2.prefab` ×4; separation **our choice** | Separation flash selected in the FX pass; `FireBall` needs neutering (R9). |
| Q5 | Acquisition | **Admin grant only for v1** (A) | Loot/airdrop deferred. |
| Q6 | Map presence | **All six MIRVs visible** (A) + a marker tracking the missile | Vanilla MLRS markers are owner-only, so markers are plugin-spawned `genericradiusmarker` instances (FR-29/44). |
| Q7 | Blast composition | **Research pass first** (C) | §9.4 gates M5. |
| Q8 | Who can watch | **Anybody** (B) | No camera permission; FR-64 changed. |
| Q10 | Target rules | **Anywhere is targetable; cannot throw from inside a safe zone** (A/D hybrid) | FR-17 added; safe-zone behaviour still needs R10/Q17. |
| Q11 | 80 %/95 % | **Altitude fraction** (A) | Trajectory triggers by Y. |
| Q12 | Booster at hand-off | **Killed instantly** (A) | Separation flash masks it. |

### 13.2 Remaining

| # | Question | Why it matters | Default if unanswered |
|---|---|---|---|
| Q13 | Pink/purple smoke: is there a coloured-smoke prefab? | FR-10's target marker is currently vanilla smoke colour. | Use the vanilla supply-signal smoke for v1. |
| Q14 | How does a player pick a camera that is nowhere near a station? | Stations only list static cameras nearby; a missile camera must be bookmarked by ID. | Plugin force-adds the bookmark and prints the ID in chat. |
| Q15 | Confirm "FP" = first-person debug overlay in the caller's view? | Cosmetic naming only. | Assume first-person. |
| Q16 | Confirm the marker approach: purple `genericradiusmarker` instead of a tiny MLRS rocket? | The rocket-marker idea cannot work server-wide (owner-only networking). | Purple radius marker on the missile and on each MIRV. |
| Q17 | What does "real consequences" mean for a safe zone hit? Vanilla rockets self-destruct on safe-zone triggers and damage is blocked. | Decides whether a strike on Outpost/Bandit actually does anything. | Strike fizzles inside the safe-zone volume; log it and warn the caller. |
| Q18 | Are the six MIRV markers allowed to be *ship-sized* circles, or should they read as small dots? | Visual taste. | Small (8 m) dots. |
| Q19 | Siren route (D-2): spawn the air-raid asset as an entity (R6b), or audition vanilla pooled effects? | Effect route is silent (R4 run 2). | R6b first. |
| Q22 | Does the R6b siren entity make sound at 1 m and at 1900 m? | Decides Q2 and FR-14/15. | Unknown until R6b. |
| Q23 | Is R7's map tint at radius 60 the correct size, or should the radius be in other units? | Decides marker sizes in §7.1. | Unknown until R7 sweep. |
| Q24 | Do the R2 survey results show duplicate identifiers on the server? | Decides whether we must assign our own identifiers. | Unknown until R2 survey. |
| Q25 | Smoke: after R5 v2, does the target show the smoke colour we need? | FR-10 and Q13. | Unknown until R5 v2. |
| Q20 | Missile carrier (D-1): accept an invisible carrier entity with the missile prefab as a child? | Architecture of M3. | Yes. |
| Q21 | Did `/probe bus` show anything 8 m in front (R8)? | Decides whether the bus needs a visible child. | Assume not visible until confirmed. |

### 13.3 Work items created by the answers

| # | Item | Gate |
|---|---|---|
| FX-1 | Nuclear FX research pass (§9.4) | Before M5 |
| FX-2 | Separation flash selection | During FX-1 |
| P-1 | Probe the bus prefab's spawnability (R8) | M0 |
| P-2 | Probe `FireBall` as a plume (R9) | M0 |
| P-3 | Probe marker visibility to a second player (R7) | M0 |
| P-4 | Probe siren reach and late-join behaviour (R6) | M0 |
| P-5 | Probe safe-zone behaviour of an MLRS rocket (R10) | M0 |

---

## 14. Roadmap

| Milestone | Deliverable | Gate |
|---|---|---|
| **M0 — Probes** | R1 missile spawn, R2 CCTV on a moving parent, R3 `ddraw.clear`, R4 broadcast sound reach, R5 supply-drop suppression, R6 siren reach/late-join, R7 marker visibility, R8 bus prefab, R9 `FireBall` plumes, R10 safe-zone behaviour. | All ten answered on the live server; findings recorded in §9.1 and §13. |
| **M1 — Skeleton** | Plugin loads, config UI, football item + permission + cooldown, call validation, exception log + guard wired. | Success criteria 1 and 7. |
| **M2 — Warning** | Smoke detection, grid notification, sirens, abort. | Criteria 3. |
| **M3 — Launch & boost** | Water spawn, breach, boost arc, four plumes, camera A. | Visual check at 600 m. |
| **M4 — Bus & MIRVs** | Hand-off, bus arc, camera B, 6 rockets, decoys. | Criteria 2 and 6. |
| **M5 — Detonation** | Layered FX, ring damage, time-sliced kills, aftermath. | Criterion 4. |
| **M6 — Debug & polish** | FP overlay, clear command, test mode, docs, release checklist. | Criterion 5. |

Each milestone ends with: the storyboard beat it implements, a live-server test, and this document
amended if reality disagreed.

---

## 15. Definition of done

- [ ] All **M** requirements in §6 implemented and demonstrated on the live server.
- [ ] Success criteria 1–7 measured, with numbers recorded in §16.
- [ ] All §10 assets resolved (no `ASSET-TBD` left in the shipped config).
- [ ] All §13 questions answered and closed.
- [ ] Fault injection: every phase aborted mid-flight; no orphan entities, no stuck players.
- [ ] `c.reload` mid-flight: clean recovery.
- [ ] Exception log reviewed after a full run: no unexpected records.
- [ ] README updated; this document updated to v1.0 with the change log.

---

## 16. Change log

| Version | Date | Change | By |
|---|---|---|---|
| 0.1 | 2026-10-17 | Initial framework: storyboard, FRs, config sketch, feasibility probes, open questions. | Build agent |
| 0.2 | 2026-10-17 | Probe run 2: siren path corrected (`air raid`); R3, R8, R5 plane confirmed; R2 identifier duplicates; R7 marker tinted the whole map; R9 plumes need the `generation` neutering. Operator answers recorded (Q1–Q12). Probe run 1: missile not an entity (R1), siren not playable via effects (R6), bus spawns (R8), supply intercept works (R5). Sirens become pulse-based anchors; marker strategy changed after verifying MLRS markers are owner-only; asset register filled; Q13–Q18 remain; FX research pass gated into M5. | Build agent |

---

## Appendix A — Reference values from vanilla (for tuning)

| Value | Source | Use |
|---|---|---|
| `MLRS.targetAreaRadius = 30 m` | `MLRS.cs` | Vanilla rocket dispersion; our decoy radius (50 m) is deliberately wider. |
| `MLRS.minRange = 200 m` | `MLRS.cs` | Minimum useful launch distance. |
| `MLRS.rocketSpeed`, `rocketBaseGravity` | `MLRS.cs` | Starting point for MIRV speed/gravity. |
| `TimedExplosive.explosionRadius = 10 m` (default) | `TimedExplosive.cs` | Baseline for a normal rocket; our rings are 60/110/160 m. |
| `cctv.static` → `CCTV_RC` | prefab index | Camera class. |
| `rocket_mlrs` → `MLRSRocket` | prefab index | MIRV class. |
| `grenade.supplysignal.deployed` → `SupplySignal` | prefab index | Football behaviour. |

## Appendix B — Document map for this product

| Question | Document |
|---|---|
| How do I build/ship this? | `CARBON_PLUGIN_PLAYBOOK.MD` |
| What is the API for X? | `CarbonMod.MD` |
| How must failures be handled? | `Exceptions.md` |
| What does the engine allow? | `Rust.MD` |
| May I assume this? | `DontJustRollWithIt.MD` |
| Is the source still current? | `RESEARCH_REVIEW.MD` |

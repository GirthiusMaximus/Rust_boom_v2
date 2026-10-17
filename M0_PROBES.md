# M0 — Decisions and Probe Kit

> **Status:** decisions Q1–Q12 recorded 2026-10-17. **Remaining: Q13–Q18 and probes R1–R10.**
> Nothing in M1 is blocked by the remaining questions; M3/M4 are blocked by R1, R2, R8 and R9.
>
> **Companion:** `DESIGN_BRIEF.md` §13 is the authoritative decision register. This file is the
> working kit: what is decided, what is still open, and how to test the ten things the code will
> depend on.

---

## Part A — Decisions

### A.1 Recorded

| # | Question | Answer |
|---|---|---|
| Q1 | Vanilla supply drop on throw? | **Suppress it.** The football only calls the strike. |
| Q2 | Siren model | **Spatial, anchor-based**, ~1900 m audible range, overlapping anchors. Latecomers hear it. |
| Q3 | Water-exit cue | `assets/content/effects/explosions/underwater/explosion_underwater_surface_large.prefab` |
| Q4 | Bus / plumes / separation | Bus `nuclear_warhead.prefab`; plumes `oilfireball2.prefab` ×4; separation flash — our choice |
| Q5 | Acquisition | **Admin grant only** for v1 |
| Q6 | Map presence | **All six MIRVs visible** + a marker on the missile itself |
| Q7 | Blast composition | **Research pass first** — scheduled as §9.4, gates M5 |
| Q8 | Who watches the camera | **Anybody** — no permission gate |
| Q10 | Target rules | **Target anywhere; cannot throw from inside a safe zone** |
| Q11 | 80 %/95 % | **Altitude (Y) fraction** |
| Q12 | Booster at hand-off | **Killed instantly**, masked by the separation flash |

**Q2 mechanics — what "spatial" means in Rust.** `Effect.server.Run(..., broadcast: true)` is
delivered to *every* connected client. The spatial part is client-side audio attenuation, not network
range: an effect at an anchor is loud near it and silent far away. To satisfy "a player who arrives
later still hears it", each anchor **re-emits** the sound on a pulse interval for the siren's
duration, so a late joiner hears the next pulse within `PulseIntervalSeconds`. Anchors should be
spaced at roughly `AudibleRange × 1.0` so their coverage overlaps. R6 measures the real range and the
sound's length.

**Q6 mechanics — a correction from the source.** You asked for a tiny MLRS rocket to get a marker on
the map. Verified in `MapMarkerMLRSRocket`:

```csharp
public override bool ShouldNetworkTo(BasePlayer player) => player.userID == base.OwnerID;
```

The marker is networked **only to the rocket's owner** (`limitNetworking = true`). Spawned by the
plugin, it would be visible to nobody; spawned for the caller, only to the caller. Six vanilla
markers therefore cannot be shown to the whole server.

Instead we spawn `assets/prefabs/tools/map/genericradiusmarker.prefab` (`MapMarkerGenericRadius`,
public `radius`/`color1`/`color2`/`alpha`, `SendUpdate()`, no owner restriction) and parent it to the
missile and to each MIRV. That gives everyone a moving purple mark, configurable size and colour, with
no rocket entity involved. **Q16 asks you to confirm this substitution.**

**Q7 status.** Recorded as "wait for an FX research pass". That pass is §9.4 of the brief: enumerate
candidate prefabs, audition them with `/probe fx` from 100 m / 600 m / 2 km, then fix the layer list
before any blast tuning. No action needed from you beyond being present for the audition.

---

### A.2 Remaining — answer these

#### Q13 — Pink/purple smoke

The vanilla supply-signal smoke is not pink. Do you have a coloured-smoke effect prefab?

- **A.** You supply the path → `________________________________`
- **B.** I find the closest vanilla coloured smoke and flag it as a placeholder. *(recommended)*
- **C.** Accept the vanilla smoke colour for v1.

**Answer:** ______

#### Q14 — Camera selection

A computer station only lists **static** cameras within a small radius; any other camera has to be
added as a bookmark by identifier. A camera on a missile is not near any station.

- **A.** Plugin force-adds the bookmark to stations and prints the camera ID in chat. *(recommended)*
- **B.** Print the ID and let players add it manually.
- **C.** Skip stations and open the feed as a UI overlay.

**Answer:** ______

#### Q15 — "FP" meaning

Is "FP Debug mode" first-person debug — the overlay drawn in the triggering admin's own view using
`ddraw`?

- **A.** Yes, first-person. *(assumed)*
- **B.** Something else → `________________________________`

**Answer:** ______

#### Q16 — Marker substitution

Confirm replacing the "tiny MLRS rocket" idea with a `genericradiusmarker` parented to the missile
and to each MIRV (the vanilla marker is owner-only — see A.1).

- **A.** Yes, purple radius markers. *(recommended)*
- **B.** Keep trying to use an MLRS rocket — accept that only one player can see its marker.

**Answer:** ______

#### Q17 — Safe-zone consequences

**Finding:** a vanilla `MLRSRocket` that touches a `TriggerSafeZone` calls `Kill()` without exploding,
and safe zones block explosion damage. So a strike aimed inside a safe zone currently does **nothing**
— it will not "render the safezone uninhabitable" without extra work.

What should happen?

- **A.** Accept the fizzle: the rocket dies, the zone survives, the caller is told. *(simplest, honest)*
- **B.** Deflect the target to the nearest point outside the safe zone, and tell the caller.
- **C.** Bespoke bypass: detonate manually at the target and apply damage that ignores safe-zone
  protection (needs research + a test; may still be blocked by `InSafeZone` checks).
- **D.** Something else → `________________________________`

**Answer:** ______

#### Q18 — MIRV marker size

Six markers appear in the last seconds. Should they read as small dots or as visible circles?

- **A.** 8 m dots. *(recommended)*
- **B.** Larger circles (25 m+) — easier to see, more cluttered.
- **C.** No MIRV markers; keep only the missile marker.

**Answer:** ______

---

## Part B — Assets

| ID | Asset | Path | Status |
|---|---|---|---|
| A1 | Air-raid siren | `assets/bundled/prefabs/modding/sound/air_raid/airraidsound.prefab` | ✅ given |
| A2 | Water-exit cue | `assets/content/effects/explosions/underwater/explosion_underwater_surface_large.prefab` | ✅ given |
| A3 | Bus | `assets/bundled/prefabs/modding/events/twitch/nuclear_warhead.prefab` | ✅ given — spawnability unverified |
| A4 | Booster plume | `assets/prefabs/npc/m2bradley/oilfireball2.prefab` | ✅ given — `FireBall` entity, needs neutering |
| A5 | Separation flash | — | ❌ ours to choose in the FX pass |
| A6 | Pink/purple smoke | — | ❌ Q13 |
| A7 | Nuclear FX layers | — | ⏳ FX pass (Q7) |
| A8 | Missile body | `assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab` | ✅ given |
| A9 | MIRV | `assets/content/vehicles/mlrs/rocket_mlrs.prefab` | ✅ given |
| A10 | Map marker | `assets/prefabs/tools/map/genericradiusmarker.prefab` | ✅ chosen (Q16 to confirm) |

---

## Part C — Probes

A probe is a short disposable test that answers something the documentation cannot. **Ten probes,
about 45 minutes.** They run from one admin-only plugin.

**Install once**

1. Copy `probes/TheatricalNukeProbe.cs` into `carbon/plugins/`.
2. Run `c.reload TheatricalNukeProbe`.
3. Confirm it appears in `c.plugins`.
4. In game, the commands are `/probe …` in chat, admin only.

If Carbon reports a compile error, send me the file and line — the probe has never been compiled
(there is no .NET toolchain in this environment). Delete the `R5` region if the error is about
`HarmonyLib`; every other probe works without it.

| # | Probe | Command | Time |
|---|---|---|---|
| R1 | Missile prefab | `/probe missile` | 3 min |
| R2 | CCTV on a moving parent | `/probe cctv` → bookmark → walk | 5 min |
| R3 | `ddraw.clear` | `/probe ddraw` then `ddraw.clear` in F1 | 2 min |
| R4 | Broadcast sound reach | `/probe fx <siren prefab> x y z` | 5 min |
| R5 | Supply-drop suppression | `/probe supply on` → throw signal | 5 min |
| R6 | Siren pulses + late join | `/probe siren [x y z]` | 6 min |
| R7 | Marker visible to everyone | `/probe marker` + a second player | 4 min |
| R8 | Bus prefab spawnability | `/probe bus` | 3 min |
| R9 | `FireBall` plumes | `/probe plume` | 5 min |
| R10 | Safe-zone behaviour | one MLRS rocket at a safe-zone edge | 6 min |

---

### R1 — Missile prefab *(3 min)*

**Unknown:** does `nuclear_silo_missile.prefab` have a `BaseEntity`, and can it be spawned?

**Steps:** stand in the open, `/probe missile`, watch console and the space 8 m ahead.

**Record:** `FindPrefab` result, the component list, whether `CreateEntity` succeeded, the entity
class, and whether you can see it. If visible: upright? right size? facing which way?

---

### R2 — CCTV on a moving parent *(5 min)*

**Unknown:** does a plugin-spawned `cctv.static` work when parented to something that moves, and how
does a player select it?

**Steps:** `/probe cctv` → note the identifier in chat → sit at a computer station → add the
identifier as a bookmark → watch; then **walk, jump and turn** → `/probe cctvstop`.

**Record:** accepted by the station? feed appeared? right way up? did it **follow** you? lag or black
frames? The console prints `ConsumptionAmount` and the `SetParent` result.

---

### R3 — `ddraw.clear` *(2 min)*

**Unknown:** can debug lines be cleared on demand?

**Steps:** `/probe ddraw` in chat, then immediately type `ddraw.clear` in the F1 console.

**Record:** did the lines vanish, or "unknown command"?

---

### R4 — Broadcast sound reach *(5 min)*

**Unknown:** how far does a broadcast effect actually carry?

**Steps:** `/probe fx assets/bundled/prefabs/modding/sound/air_raid/airraidsound.prefab` (plays 2 km
ahead of you). Repeat with coordinates at the far corner and stand at your original spot.

**Record:** audible at 2 km? at the map edge? how loud? Any delay?

---

### R5 — Supply-drop suppression *(5 min, test server only)*

**Unknown:** does a Harmony prefix on `SupplySignal.Explode` cleanly stop the cargo plane?

**Steps:** `/probe supply on` → throw a supply signal → observe. `/probe supply off` → throw another.

**Record:** plane present/absent in each step; console shows `[R5] SupplySignal.Explode intercepted`;
any errors.

---

### R6 — Siren pulses and late join *(6 min)*

**Unknown:** does the pulsing model actually sound continuous, and does someone who arrives late hear
it?

**Steps:**
1. `/probe siren` (plays 400 m ahead, pulses every 8 s for 40 s).
2. Listen and note how long each sound lasts, and whether the pulses overlap into one continuous siren.
3. Log out, log back in mid-sequence (or have a second player join then) — do they hear the next pulse?
4. Walk out of range and back in — does it return?

**Record:** audible distance, pulse length, whether 8 s is right, late-join result.

---

### R7 — Marker visibility *(4 min)*

**Unknown:** is a plugin-spawned `genericradiusmarker` visible to everyone?

**Steps:** `/probe marker` → look at the map yourself → ask a second player (who is nowhere near you)
to look at their map.

**Record:** do both of you see a purple 60 m circle at your feet? Does it move if you walk? (The probe
spawns it unparented; movement is tested in M1.)

---

### R8 — Bus prefab *(3 min)*

**Unknown:** is `nuclear_warhead.prefab` an entity or an FX-only prefab?

**Steps:** `/probe bus`.

**Record:** `BaseEntity component → …`; if it is an entity, does it spawn and what does it look like?
If it is FX-only, `CreateEntity` returns null and we need a carrier entity.

---

### R9 — `FireBall` plumes *(5 min)*

**Unknown:** can `oilfireball2` be used as a booster plume without damage, spreading, or falling off?

**Steps:** `/probe plume` (spawns 4 neutered fireballs parented to you). Walk around. Stand next to a
wooden structure and see whether it ignites. Check server performance briefly.

**Record:** do they follow you? do they look like booster flames? any fire spread or damage? any
console errors? `/probe plumestop` afterwards.

---

### R10 — Safe-zone behaviour *(6 min)*

**Unknown:** what happens when an MLRS rocket reaches a safe zone?

**Steps:** on a test server, spawn `rocket_mlrs` aimed at the edge of a safe zone (use an admin
command or ask me for a small probe extension), or simply fire a vanilla MLRS at the boundary.

**Record:** does the rocket explode, fizzle, or vanish? does the safe zone take damage? do players
inside take damage? This decides Q17.

---

## Part D — Report template

```text
## Environment (R0)
- Carbon:            <c.version>
- Rust build:        <startup banner>
- Map size / seed:   <...>
- Server:            test / live

## Remaining answers
Q13 (pink smoke):      A / B / C   path: ...
Q14 (camera select):   A / B / C
Q15 (FP):              A / B
Q16 (marker sub):      A / B
Q17 (safe zone):       A / B / C / D
Q18 (marker size):     A / B / C

## Probes
R1  missile:   FindPrefab=...  component=...  CreateEntity=...  visible=yes/no  notes=...
R2  cctv:      id=...  accepted=yes/no  followed=yes/no  lag=...  notes=...
R3  ddraw:     clear worked=yes/no  error=...
R4  sound:     2 km=...  map edge=...  loudness=...
R5  supply:    suppressed=yes/no  plane returned=yes/no  errors=...
R6  siren:     audible at=... m  pulse length=... s  continuous=yes/no  late join=yes/no
R7  marker:    seen by you=yes/no  seen by other=yes/no
R8  bus:       BaseEntity=...  spawned=yes/no  looks like=...
R9  plume:     followed=yes/no  fire spread=yes/no  damage=yes/no  look=...
R10 safe zone: exploded / fizzled / vanished; damage=yes/no
```

---

## What each answer unlocks

| Answer | Unlocks |
|---|---|
| Q13 | FR-10 target marker; asset register complete |
| Q14 | Camera UX in M4 (FR-65) |
| Q16 | Marker implementation in M3 (FR-29) |
| Q17 | Safe-zone logic in M1 validation + M5 blast |
| R1 | Whether M3 needs the wrapper rig |
| R2 | Whether the camera is parented or free-flying |
| R6 | Pulse interval and anchor spacing defaults |
| R7 | Whether markers work server-wide |
| R8 | Whether the bus is an entity or an FX |
| R9 | Whether plumes are `FireBall` entities or FX |
| R10 | The answer to Q17 |

---

## Part E — Results log

### Run 1 — 2026-10-17 (live test server, console pasted by operator)

| Probe | Result | Verdict |
|---|---|---|
| **R1** missile | `FindPrefab` OK. Root components: `Transform`, `MeshCollider` — **no `BaseEntity`**. `CreateEntity` returns null: *"CreateEntity called on a prefab that isn't an entity!"* | ❌ **Not spawnable as an entity.** The missile needs a carrier entity (brief §9.3 R1 fallback) — decision D-1 below. |
| **R2** CCTV | `CCTV_RC` spawned, `ConsumptionAmount` = 3, identifier **`COBALT1`**, `SetParent(player, worldPositionStays: true)` OK. | ⚠️ Spawn + parent work. **Follow test not yet reported** (add `COBALT1` at a station, walk/jump). |
| **R3** ddraw | Lines sent for 30 s. | ⚠️ **`ddraw.clear` result not reported.** |
| **R4** broadcast | Run with `fx siren` and `fx missile`: `"siren"` and `"missile"` are not asset paths. Engine: *"String ID is 0 – unknown effect"*. | ⚠️ **Invalid test — my usage error** (the command takes a full path). Re-run needed. |
| **R5** supply | `give` of one supply signal; `[R5] SupplySignal.Explode intercepted; vanilla cargo plane suppressed.` | ✅ **Harmony prefix fires.** Plane absence still to be confirmed by eye. |
| **R6** siren | Air-raid path passed to `Effect.server.Run` 5 times: *"StringPool.GetNumber – no number for string …airraidsound.prefab"*, *"String ID is 0 – unknown effect"*. | ❌ **Siren cannot be played through `Effect.server.Run`.** See D-2. The probe's "pulse sent" text is wrong — `Run` returns nothing, so the probe cannot detect the failure. |
| **R7** marker | `genericradiusmarker` spawned, net id 6796684. | ⚠️ **Visibility to a second player not reported.** |
| **R8** bus | `nuclear_warhead.prefab`: `FindPrefab` OK, root component `BaseEntity` only; `CreateEntity` and `Spawn()` OK, net id 6797210. | ✅ **Spawnable.** ⚠️ Whether anything is *visible* (child meshes?) not reported. |
| **R9** plumes | 4 × `FireBall` spawned and parented. | ⚠️ **Look/follow/fire-spread not reported.** |
| **R10** safe zone | Not run. | ⏳ |

Other log lines: `Curl error 7 … cdp.cloud.unity3d.com` is Unity telemetry failing — unrelated to the probe.
The `/probe spawn missile` and `/probe fx missile` lines were my usage error; the probe expects full paths.

**Cleanup still needed on the test server:** `/probe cctvstop` (CCTV), `/probe markerstop` (marker and the four plumes). Plumes self-expire after 300 s.

### Decisions this run creates

| # | Decision | Options | Recommendation |
|---|---|---|---|
| **D-1** | Missile body (R1) | (a) Invisible carrier `BaseEntity` with the missile prefab attached as a child (verify the child's visuals render); (b) spawn the missile prefab's *meshes* only and move them with our own transform updates. | (a) — entities are networked and parentable; (b) bypasses the network model. |
| **D-2** | Siren playback (R6) | (a) Spawn `airraidsound.prefab` as an **entity** and check it has an audio source (new probe R6b); (b) find a vanilla **effect** prefab already in the effect pool that plays a siren-like sound (audition); (c) both, in order. | (c): R6b first, since it is one command. |
| **D-3** | Probe honesty | Change the probe so it reports `FindPrefab` + `GetComponent` results before playing, and stops claiming "sent" for effects. | Yes — I'll make this change. |

### Next probe (R6b) — siren as entity

`/probe spawn assets/bundled/prefabs/modding/sound/air_raid/airraidsound.prefab`, then read the console for:
the components list (look for `AudioSource`), and whether `CreateEntity` returns an entity.
Listen 8 m away; then walk 200 m, 800 m, 1900 m away and note audibility.

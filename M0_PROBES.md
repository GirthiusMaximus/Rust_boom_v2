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
| A1 | Air-raid siren | `assets/bundled/prefabs/modding/sound/air raid/airraidsound.prefab` | ✅ given |
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

**Steps:** `/probe fx assets/bundled/prefabs/modding/sound/air raid/airraidsound.prefab` (plays 2 km
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

`/probe spawn assets/bundled/prefabs/modding/sound/air raid/airraidsound.prefab`, then read the console for:
the components list (look for `AudioSource`), and whether `CreateEntity` returns an entity.
Listen 8 m away; then walk 200 m, 800 m, 1900 m away and note audibility.

### Run 2 — 2026-10-17 (operator console and observations)

| Probe | Result | Verdict |
|---|---|---|
| **R1** | Not re-run (run 1 stands). | ❌ not an entity |
| **R2** | `CCTV_RC` spawned; identifier **COBALT1**; parented. Station with COBALT1 opened a **large oil-rig camera** and that camera entity sits under the operator. | ❌ identifiers are not unique. Next: R2 survey (lists every `CCTV_RC`, flags duplicates). |
| **R3** | Lines drawn; `ddraw.clear` cleared them. | ✅ verified |
| **R4** | Spaced path accepted by the pool (no error). At 1 m and 2000 m: **silent**. | ❌ effect route silent for this asset |
| **R5** | Signal intercepted, cargo plane not seen. The probe **killed** the signal, so it also disappeared early. | ✅ plane suppressed. ⚠️ smoke lost (see R5 v2) |
| **R6** | Run 1 used the wrong path (underscore). Not re-run as such; R4 covers the effect route. | ❌ see R4 |
| **R6b** | Not run. | ⏳ next |
| **R7** | Marker at radius 60 tinted the **entire map** pink. | ❌ radius is not metres. Next: sweep 5 / 25 / 60 |
| **R8** | Bus visible. | ✅ verified |
| **R9** | Plumes visible and follow the operator. Three `NullReferenceException`s in `FireBall.TryToSpread` (our `spreadSubEntity = null`). Plumes were stacked on the operator, so the count could not be judged. | ✅ follow. ❌ neutering bug fixed in v0.3 (`generation = 1000`); spacing added. |

**Operator clarification on R9:** plume damage is acceptable because the effect is mostly outside the playable area. The real risk is a player standing on the spawn point under the water, which is judged very unlikely. Recommendation: keep damage at 0 anyway. It costs nothing, and the brief can say "no damage" rather than "damage accepted".

### Probe protocol v0.3 (applies to ALL future sessions)

- Every probe is its own command: `/probe R3`, `/probe R6b`, and so on.
- Start: `/probe <ID> [args]`. End: `/probe <ID> end <observation>`. Abort: `/probe abort`.
- Console banners: `########## PROBE R6b START ##########` … `########## PROBE R6b END ##########`. Every line carries `[PROBE <ID>]`.
- One log file per run: `carbon/logs/TheatricalNukeProbe/<ID>_<yyyyMMdd-HHmmss>.log`, containing START, ARGS, STEP, RESULT, OBSERVE, CLEANUP lines and END.
- Caught exceptions also go to `carbon/logs/TheatricalNukeProbe/probe-errors.log`.
- Every probe has a timeout and runs its cleanup list in reverse on end, abort or timeout. Each cleanup line reports OK or FAIL.

### Next probes (run 3)

| ID | Command | Purpose |
|---|---|---|
| R2 | `/probe R2` | Survey all `CCTV_RC`, flag duplicate identifiers, spawn one camera, and check its identifier. |
| R5 | `/probe R5` | v0.3 prefix keeps smoke and skips the plane. Throw a signal. |
| R6b | `/probe R6b` | Spawn the siren entity at 1 m; report whether the `AudioSource` plays. Then walk to 200 / 800 / 1900 m. |
| R7 | `/probe R7` | Radius sweep 5 → 25 → 60, every 20 s. Report when the map tint appears. |
| R9 | `/probe R9 4` | Four plumes, 4 m apart, damage 0, spread disabled. |

Questions for you: see the chat reply (Q22–Q25).

### Build fix — v0.3 compile error (CS1069)

Carbon could not resolve `AudioSource` (it lives in `UnityEngine.AudioModule`, which is not in the plugin reference set). Fixed by finding `AudioSource` components by type name and reading their properties by reflection. No new assembly reference needed. Re-run R6b to get the audio dump.

### Operator answers (run 3)

- **Q1** (siren plugin debug output): operator is updating their working siren plugin to print more detail. Awaiting its output.
- **Q2** plume damage: **0** (decided).
- **Q3** football smoke life: operator answered "15 s". This may refer to the siren delay rather than the smoke. **Needs confirmation** (see chat).
- **Q4** leftover siren entity: none spawned by the operator. Operator has used `spawnat airraidsound` with success in the past, which suggests the entity route works.


### Run 4 — operator logs (probes/logs/run-4/, v0.3.0)

| Probe | Result | Verdict |
|---|---|---|
| **R2** | 98 `CCTV_RC` on the server. `COBALT1` is used by **32** of them (monument cameras). Our camera also got `COBALT1`, the prefab default. The station opened a monument camera. | ❌ default identifier is not unique. Fix: `rcIdentifier = "NUKECAM#ID"` before `Spawn()` (engine replaces `#ID` with the net id). Verify in run 5. |
| **R5** | Smoke shows on the target, no plane, lasts ~210 s. | ✅ plane suppressed, smoke works. Lifetime to be set to 15 s (Q3). |
| **R6b** | Entity spawned as a plain `BaseEntity`. **0 `AudioSource` components** on the prefab or live entity. Operator heard it and stopped at (-1448.57, 187.36, -1115.75). Spawn was at (-2039.04, 9.75, 870.24). | ✅ audible. **Hearing distance ≈ 2080 m** from the spawn point (one measurement). The sound is not an `AudioSource`, so R6c audits components. |
| **R7** | Radius 25 tinted the **whole map**. Radius 5 was "fairly large" but did not cover the map. | ❌ radius unit unknown. Sweep 0.5 / 1 / 2 / 3 / 5 in v0.4. |
| **R9** | Plumes spawned on top of the operator again. Not judgeable. | ❌ spawn 5 m in front, log positions before and after `SetParent`, compare static and follow modes. |

**Q3 (smoke life):** the operator's answer is **15 s**. Configured as `Visuals.SmokeSeconds`, not hard-coded. The vanilla value is 210 s.


### Missile body — carrier question (raised by operator, run 5 planning)

The silo missile (`nuclear_silo_missile.prefab`) is not an entity (R1, run 1). The design planned an
invisible carrier entity with the missile as a child. That needs a second check: **a server-side
child is not networked.** Only entities and the children of their prefabs reach clients. So the
carrier only helps if the **client-side prefab of a networked entity already contains the missile
model**.

**Probe R1b** tests this in one run:
1. Audits the missile and `nuclear_warhead` entity: child names, mesh names, materials. If the warhead
   shares the silo mesh, that is the answer.
2. Spawns a bare networked carrier (`rocket_crane_lift_trigger.prefab`, BaseEntity only, per the index).
3. Instantiates a server-side copy of the missile under the carrier.
4. Operator reports whether the body is visible 14 m away. **Expected: not visible.**

If R1b confirms invisibility, the options are:
- **A.** Use a networked entity whose prefab already shows a missile-like model (audit result decides).
- **B.** Use a visible vanilla rocket entity (`rocket_basic`, `rocket_hv`, `rocket_fire`) as the body. Not MLRS, but still a generic rocket, so the theatre is weaker.
- **C.** Ship a client content bundle with the missile. Rejected by the server-only rule (no client mods). Needs an explicit decision to relax it.


### Run 5 — 2026-10-18 (operator logs: probes v0.5.0, attached as TheatricalNukeProbe2.zip)

Logs are in `/home/user/probe_logs_run5/` (copy to `probes/logs/run-5/` on commit).

| Probe | Result | Verdict |
|---|---|---|
| R1 | Missile root = Transform + MeshCollider, no BaseEntity; CreateEntity null. | Confirmed (run 1). |
| R1b ×2 | Carrier `rocket_crane_lift_trigger` spawned (net ids 7968251, 7968711). Server-side missile copy under it. Operator saw **nothing**. Cleanup OK. | **Invisible as expected.** Carrier idea needs option A, B or C (below). |
| R1b audit | Missile prefab: one Transform, one MeshCollider, no MeshFilter or renderer. `nuclear_warhead`: root BaseEntity; child MeshCollider. Server prefabs do not expose visual meshes. | Audit cannot show the warhead shape. Check in game. |
| R2 | 98 CCTV on server; prefab identifier COBALT1. Our camera got `NUKECAM7969184`, valid. Feed shows the correct camera. Operator: "consider shorter bookmark name". | **Confirmed.** Name length question open (Q-B). |
| R5 | Intercept at (-2048.71, 5.11, 871.15). Smoke 15 s. No cargo plane. | **Confirmed.** |
| R6b | Siren entity spawned; operator heard it. Moved 2000+ m out and back. Log wording unclear on whether audio stopped at range. | Partly answered. Falloff still open (Q-C). |
| R6c | Audit: prefab and live both Transform + BaseEntity only. Operator heard audio at 20 m. | Confirmed: sound comes from a component the server cannot see. |
| R7 ×2 | Run 1 ended by a server restart ("Restarting"). Run 2: radius 0.5 is about one grid cell. | Sizes not tuned yet. Second-player test deferred (Q2). |
| R9 static | 4 plumes spaced 2 m, 5 m north of operator, fixed. | **Confirmed.** |
| R9 follow ×2 | After `SetParent`, local position was zeroed, so the plumes sat on the operator and merged. | **Bug.** v0.6.0 sets the local offset after parenting, then sends an update. Untested. |

**Operator answers (2026-10-18):** Q1 map size 6000 (unit to confirm, see Part A). Q2 second-player test for R7 is deferred. Marker sizes need tuning.

### Probe protocol v0.6 (applies to ALL future sessions)

- **One session log per session.** `/probe start` opens `carbon/logs/TheatricalNukeProbe/session_<timestamp>.log`. Every probe in the session writes into this file. `/probe end` closes it and writes a summary. Probes refuse to run without an open session.
- **Start and end banners per probe** are written into the session file, with the R number.
- **`/probe <ID> end` with no words** runs cleanup, then opens an in-game observation box (CUI). Type the observation (limit 1000 words, about 6000 characters), then press Submit. The box text becomes the OBSERVE line. `/probe <ID> end <words>` still works for chat-only sessions.
- **Verbose logging:** each probe writes an environment snapshot at start and end, a TICK line every 5 s (operator position and tracked entity state), DEBUG lines for each cleanup item, and full stack traces for errors.
- Each probe still cleans up after itself, in reverse order of creation.

### Open after run 5

- **Q-A (map size).** Answered: 6000 m × 6000 m.
- **Q-B (bookmark name).** Answered: `NUKE` + network ID (probe v0.6.1).
- **Q-C (siren falloff). Answered (2026-10-18):** the siren was audible again at about 2000 m from the spawn point after the operator walked out. Interpretation: audible at about 2000 m, consistent with the 2079 m measurement in run 4. Falloff curve still to be measured (see R6d below).
- **Q-D (missile body).** Choose option A, B or C (see the carrier section above), or ask for a further test.
- **Q-E (R7 sizes).** Which radii should the sweep use next? Options: `/probe R7 0.1 0.25 0.5 1`. The probe now takes any radius list.


### Build fix — v0.6.0 compile error (CS0103)

`CuiHelper` lives in `Oxide.Game.Rust.Cui` (Carbon `src/Carbon.Components/Carbon.Common/src/Oxide/CUI/CuiHelper.cs`). Added `using Oxide.Game.Rust.Cui;`. Version **0.6.1**. The server was still on v0.5.0 after the failed compile, so the operator needs `c.reload TheatricalNukeProbe` again.

### Missile body — proposed probe sequence (for approval)

Hypothesis from run 5: the silo missile root is **inactive** in its prefab (`active=False`, Transform + MeshCollider only), so it may be a child that a parent entity activates. The parent entity's prefab would then already carry the visual to clients.

1. **R1c — manifest search (read-only, no spawns).** Search `GameManifest.pathToGuid` (all bundled asset paths) for `nuclear`, `silo`, `missile`, `warhead`. For each hit, mark whether it is in `GameManifest.Current.entities` (networked). For networked hits, log the child hierarchy names. Output: a list of candidate entities that could carry the missile.
2. **R1d — candidate entity visuals.** Spawn the most likely networked parent (from R1c), at the same spot as R1b. Operator reports whether the missile body is visible on it. Cleanup kills it.
3. **R1e — child toggle replication.** Only if R1d shows the body: check whether toggling the missile child on the server changes what the operator sees. This tests whether a theatrical "launch" is possible at all.
4. Fallback to options A/B/C from Part E if R1c–R1e find nothing.

Run R1c first. It has no world effect and is the cheapest way to answer the question.


### Run 6 plan (probes v0.7.0)

Version **0.7.0**. Adds R1c, R1f, and `/probe R7 move`. Compile fix from 0.6.1 is included. Reload with `c.reload TheatricalNukeProbe`, then `/probe start`.

- **R1c (manifest search, no spawns).** Searches all bundled asset paths for `nuclear`, `silo`, `missile`, `warhead`, `icbm`, `nuke`. For each networked entity prefab it logs the child hierarchy. Reads `GameManifest.pathToGuid` and `GameManifest.Current.entities`, both from the decompile, so a compile error here means those names differ.
- **R1f (silo parent test).** Part 1 lists transforms in the scene named like the silo or missile, so we know whether the monument missile exists and where. Part 2 spawns the warhead bus as a networked parent and puts a server-side copy of the silo missile under it. Expected result: not visible (as in R1b). If R1c finds an entity that already carries the missile, that entity is the next test.
- **R7 move.** `/probe R7 move [radius] [m/s]`. One marker, default radius 0.1 and 25 m/s, travels west to east across the whole map along z = 0, one step per second. Report smoothness, lag and whether the map tints.
- **R6d (not built yet).** Falloff curve for the siren: walk out in 200 m steps and log where it stops and restarts.

Suggested order: R1c → R1f → R7 sweep → R7 move (radius 0.1, 25 m/s) → R9 follow → R6d.


### Run 6 — 2026-10-18 (operator session log `session_20261018-141014.txt`, probes v0.8.0)

Log is at `probes/logs/run-6/session_20261018-141014.txt` (copy on commit). Operator: Todd the Wraith, admin, server "Todd's Testing Ground", Procedural Map 6000 m.

| Probe | Result in log | Operator observation | Verdict |
|---|---|---|---|
| R1c | 18 671 assets, 2 408 networked entity paths; 118 keyword matches; 6 networked entity prefabs. Silo asset `nuclear_silo_missile.prefab` is an **asset, not an entity**. Entity candidates: `nuclear_warhead` (twitch), `nuke_combined`, `warhead_cable_single`, `door.hinged.bunker.door_nuclear_silo`, `homing_missile_launcher.entity`, `seekertest`. | "No observations." | **Done.** Candidate list below. |
| R1f | In-scene scan: 225 matches, first 40 listed. Silo monuments `silo_a`, `silo_c`, `silo_c (1)` exist at ~4 550 m, under `junkyard_1`. **No in-world `nuclear_silo_missile` is among the first 40 listed.** Networked bus `nuclear_warhead` spawned at (-2067.61, 6.45, 858.81). Server-side silo missile copy under it: `nuclear_silo_missile(Clone)`, `active=False`, Transform + MeshCollider only. | "I see the MIRV parts of the missle prior to where we will change them to." | **Silo missile body still not seen.** The operator did see the **warhead bus**: MIRV parts are visible on the bus. The bus is a visible, networked, missile-like body. |
| R7 sweep | `/probe R7` (default radii 0.01–0.25). Observation: "We should test the moving of the marker with 0.025 size." | 0.025 chosen for a moving test | Sweep done. Next: moving marker at 0.025. |
| R7 move | `/probe R7 move 0.1 25`. Path x −2980 to 2980 along z=0, expected 238 s. Observation: "test this again but sped up even more to simulate missile speed." | 25 m/s is too slow for a missile | Re-run faster (see run 7 plan). |
| R9 static | 4 plumes spaced 2 m, 5 m north of operator, fixed. | **Confirmed.** |
| R9 follow ×2 | After `SetParent`, local position was zeroed, so the plumes sat on the operator and merged. | **Bug.** v0.6.0 sets the local offset after parenting, then sends an update. Untested. |

**Operator answers (2026-10-18):** Q1 map size 6000 (unit to confirm, see Part A). Q2 second-player test for R7 is deferred. Marker sizes need tuning.

### Probe protocol v0.6 (applies to ALL future sessions)

- **One session log per session.** `/probe start` opens `carbon/logs/TheatricalNukeProbe/session_<timestamp>.log`. Every probe in the session writes into this file. `/probe end` closes it and writes a summary. Probes refuse to run without an open session.
- **Start and end banners per probe** are written into the session file, with the R number.
- **`/probe <ID> end` with no words** runs cleanup, then opens an in-game observation box (CUI). Type the observation (limit 1000 words, about 6000 characters), then press Submit. The box text becomes the OBSERVE line. `/probe <ID> end <words>` still works for chat-only sessions.
- **Verbose logging:** each probe writes an environment snapshot at start and end, a TICK line every 5 s (operator position and tracked entity state), DEBUG lines for each cleanup item, and full stack traces for errors.
- Each probe still cleans up after itself, in reverse order of creation.

### Open after run 5

- **Q-A (map size).** Answered: 6000 m × 6000 m.
- **Q-B (bookmark name).** Answered: `NUKE` + network ID (probe v0.6.1).
- **Q-C (siren falloff). Answered (2026-10-18):** the siren was audible again at about 2000 m from the spawn point after the operator walked out. Interpretation: audible at about 2000 m, consistent with the 2079 m measurement in run 4. Falloff curve still to be measured (see R6d below).
- **Q-D (missile body).** Choose option A, B or C (see the carrier section above), or ask for a further test.
- **Q-E (R7 sizes).** Which radii should the sweep use next? Options: `/probe R7 0.1 0.25 0.5 1`. The probe now takes any radius list.


### Build fix — v0.6.0 compile error (CS0103)

`CuiHelper` lives in `Oxide.Game.Rust.Cui` (Carbon `src/Carbon.Components/Carbon.Common/src/Oxide/CUI/CuiHelper.cs`). Added `using Oxide.Game.Rust.Cui;`. Version **0.6.1**. The server was still on v0.5.0 after the failed compile, so the operator needs `c.reload TheatricalNukeProbe` again.

### Missile body — proposed probe sequence (for approval)

Hypothesis from run 5: the silo missile root is **inactive** in its prefab (`active=False`, Transform + MeshCollider only), so it may be a child that a parent entity activates. The parent entity's prefab would then already carry the visual to clients.

1. **R1c — manifest search (read-only, no spawns).** Search `GameManifest.pathToGuid` (all bundled asset paths) for `nuclear`, `silo`, `missile`, `warhead`. For each hit, mark whether it is in `GameManifest.Current.entities` (networked). For networked hits, log the child hierarchy names. Output: a list of candidate entities that could carry the missile.
2. **R1d — candidate entity visuals.** Spawn the most likely networked parent (from R1c), at the same spot as R1b. Operator reports whether the missile body is visible on it. Cleanup kills it.
3. **R1e — child toggle replication.** Only if R1d shows the body: check whether toggling the missile child on the server changes what the operator sees. This tests whether a theatrical "launch" is possible at all.
4. Fallback to options A/B/C from Part E if R1c–R1e find nothing.

Run R1c first. It has no world effect and is the cheapest way to answer the question.


### Run 6 plan (probes v0.7.0)

Version **0.7.0**. Adds R1c, R1f, and `/probe R7 move`. Compile fix from 0.6.1 is included. Reload with `c.reload TheatricalNukeProbe`, then `/probe start`.

- **R1c (manifest search, no spawns).** Searches all bundled asset paths for `nuclear`, `silo`, `missile`, `warhead`, `icbm`, `nuke`. For each networked entity prefab it logs the child hierarchy. Reads `GameManifest.pathToGuid` and `GameManifest.Current.entities`, both from the decompile, so a compile error here means those names differ.
- **R1f (silo parent test).** Part 1 lists transforms in the scene named like the silo or missile, so we know whether the monument missile exists and where. Part 2 spawns the warhead bus as a networked parent and puts a server-side copy of the silo missile under it. Expected result: not visible (as in R1b). If R1c finds an entity that already carries the missile, that entity is the next test.
- **R7 move.** `/probe R7 move [radius] [m/s]`. One marker, default radius 0.1 and 25 m/s, travels west to east across the whole map along z = 0, one step per second. Report smoothness, lag and whether the map tints.
- **R6d (not built yet).** Falloff curve for the siren: walk out in 200 m steps and log where it stops and restarts.

Suggested order: R1c → R1f → R7 sweep → R7 move (radius 0.1, 25 m/s) → R9 follow → R6d.


### Run 6 — 2026-10-18 (operator session log `session_20261018-141014.txt`, probes v0.8.0)

Log is at `probes/logs/run-6/session_20261018-141014.txt` (copy on commit). Operator: Todd the Wraith, admin, server "Todd's Testing Ground", Procedural Map 6000 m.

| Probe | Result in log | Operator observation | Verdict |
|---|---|---|---|
| R1c | 18 671 assets, 2 408 networked entity paths; 118 keyword matches; 6 networked entity prefabs. Silo asset `nuclear_silo_missile.prefab` is an **asset, not an entity**. Entity candidates: `nuclear_warhead` (twitch), `nuke_combined`, `warhead_cable_single`, `door.hinged.bunker.door_nuclear_silo`, `homing_missile_launcher.entity`, `seekertest`. | "No observations." | **Done.** Candidate list below. |
| R1f | In-scene scan: 225 matches, first 40 listed. Silo monuments `silo_a`, `silo_c`, `silo_c (1)` exist at ~4 550 m, under `junkyard_1`. **No in-world `nuclear_silo_missile` is among the first 40 listed.** Networked bus `nuclear_warhead` spawned at (-2067.61, 6.45, 858.81). Server-side silo missile copy under it: `nuclear_silo_missile(Clone)`, `active=False`, Transform + MeshCollider only. | "I see the MIRV parts of the missle prior to where we will change them to." | **Silo missile body still not seen.** The operator did see the **warhead bus**: MIRV parts are visible on the bus. The bus is a visible, networked, missile-like body. |
| R7 sweep | Ran (0.01–0.25). Two observations, both about marker speed: 0.025 looks right at moving speed; asked for a faster test "to simulate missile speed". | see log | Speed test needed (R7 move, faster). |
| R7 move | Ran (0.1, 25 m/s default). Observation: asked for a faster run at 0.025. | see log | Re-run faster. |
| R9 static | 4 plumes, 2.0 m spacing, centre 5 m in front. Operator: "spawned directly north of the player, not attached, static." | static | **Done**, as intended. |
| R9 follow | **Probe bug: the follow test did not run.** The log shows `mode=static (not parented)` after `/probe R9 follow`, because the probe read the mode from argument 2 only. Operator: "Using follow the plumes do not follow me." | not a real test | **Fixed in v0.9.0** (mode accepted in any position). **Must be re-run.** |
| R6 | Siren through `Effect.server.Run`: log "unknown effect" warning. Operator: "Tried to spawn an effect without a EffectRecycle component ... appeared in the client." | | Consistent with run 2/4 (effect route silent). |
| R6b | Entity siren spawned; operator walked out. Operator's observation gives the position where they stopped hearing it. | (-1567.80, 41.31, -1017.06) | Falloff: R6d still to do. |
| R6c | Component audit; entity placed 20 m in front; removed on end. | "Unclear what aim of this test was." | Audit only; note the aim was not clear. |
| R2 | CCTV `NUKE8123533` valid, parented, followed the operator. Operator: "Camera still in correct location, use shortened number that is not the camera's network ID, something." | | Q-B (name length) open: needs a shorter, non-network-ID name. |

**Observation box and `/probe end` (v0.8.0): confirmed in use.** The box accepted text, Submit saved it, and the queue advanced. Session close after the last observation is still to be seen.

#### Missile body — options after R1c and R1f

Evidence:
1. The silo missile prefab is an asset with no `BaseEntity`. Its server-side copy under a networked parent has only a `MeshCollider` (R1f). No renderer reaches the server, so a copy cannot carry the body (R1b, R1f).
2. The operator saw **MIRV parts on the warhead bus** (R1f). The bus is therefore a networked entity whose client prefab has visible geometry. That is option A from the carrier section, and it now has evidence.
3. No in-world silo missile was listed in R1f's first 40 matches. The full list was not printed, so this is not yet proven.
4. Candidate networked entities with missile-like children (R1c): `nuke_combined` (child `nuclear_warhead` with `MeshRenderer` and LODs, plus a nested `BaseEntity` child `nuclear_warhead`), `nuclear_warhead` (the bus, visible), `homing_missile_launcher` (launcher, not a missile).

Options:
- **A1. Use the warhead bus as the body.** It already shows MIRV parts. Visible with no client content. Cost: the shape is a warhead, not the silo missile. Check: does the look fit the theatre? (operator question)
- **A2. Use `nuke_combined` as the body.** Its client prefab has a renderer child `nuclear_warhead` and a nested entity child. Needs its own visual probe (R1d-style) before use.
- **A3. Toggle parts of a networked entity (R1e).** If server-side `SetActive` on a child replicates, the bus can hide its MIRV parts and show a different part. If it does not replicate, A3 is closed and the choice falls to A1, A2 or B.
- **B. A visible vanilla rocket entity** (`rocket_basic`, `rocket_hv`, `rocket_fire`). Visible, but generic; the theatre is weaker.
- **C. Ship a client content bundle with the missile.** Rejected by the server-only rule. Needs an explicit decision to relax it.
- **D (not yet tested). Find an in-world silo missile.** Run a full list of scene transforms named `missile` near the silo monuments. If one exists and is visible, it is already in the world and needs no spawn.

#### Run 7 plan (probes v0.9.0)

- **R1e (child toggle on the warhead bus).** `/probe R1e [child name]`, default `nuclear_warhead`. Spawns the bus 14 m in front and lists every child with renderer and collider flags. At 10 s the chosen child is `SetActive(false)` and the bus is updated; at 25 s it is shown again. The operator reports at each step whether the view changes. Cleanup restores the child, then kills the bus.
- **R9 follow (re-run).** `/probe R9 2 follow`. Must be run because the run-6 follow test did not happen.
- **R7 move, faster.** `/probe R7 move 0.025 60`. The operator asked for the moving marker at 0.025 and for a speed closer to a missile. Speed 60 m/s is a proposal; confirm with the operator.
- **R6d (still not built).** Siren falloff, as before.
- **Missile options A1, A2, A3, D.** Decide after R1e results and the operator's answer on whether the MIRV bus fits the theatre.

Suggested order: R1e → R9 follow → R7 move 0.025 60 → R6d.

#### Initial missile on ascent and boost: requirement (operator, run 7)

The initial missile on ascent and boost **must use `assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab`**. No substitute body (warhead bus, rocket, or other entity) is acceptable for this shot. R1 must explore every avenue that could put this prefab in front of a client. Each avenue is its own probe command.

#### R1 avenues (probes v0.10.0)

| Avenue | Probe | Server handle | Status |
|---|---|---|---|
| Entity spawn of the silo prefab | R1 | CreateEntity | Failed: null, not an entity (runs 1 and 6). |
| Server child under a networked carrier | R1b, R1f | Yes | Failed: server child is not networked; invisible. |
| Effect route, one shot | **R1g** `go` | **None** | Untested. Client instantiates bundled prefabs for effects (the siren appeared on the client in run 6). |
| Effect route, stepped ascent | **R1h** `go` | **None** | Untested. Six copies 10 m apart, 1 s apart. Tests whether repeated spawns read as a rising missile. |
| Silo instance already in the world | **R1i** | Read-only | Untested. Lists every transform containing `missile`. |
| Server prefab hierarchy | **R1j** | Read-only | Confirms whether the server copy is a root-only stub (R1b and R1f suggest it is). |
| Networked entity with a missile-like child | **R1k** | Read-only | Untested. Scans all networked entity prefabs for missile, silo, icbm, warhead or rocket children. |
| BaseEntity injected into a copy | **R1m** | Yes (Track) | Untested. Not an API verified in Carbon or Rust source; this probe checks what the runtime does. |
| Carrier with the silo body as a client-side child | none | — | Closed by R1b and R1f unless R1k finds a carrier. |

**Limitation on R1g and R1h.** Effect spawns have no server handle. Cleanup cannot remove them. Each run leaves client objects until a server restart. Both probes need `go` and print a warning first.

**Known gap.** The server copy of the silo missile has no visual children (R1b, R1f: root and MeshCollider only). The client's visual children are unknown until a probe shows them. R1g and R1h are the only avenues that let the client build the full prefab.

**Run 7 order:** R1i → R1j → R1k (all read-only) → R1g `go` → R1h `go` → R1m. Stop after each one and read the result before the next.

#### Killable routes for the silo missile (probes v0.11.0, operator decision run 7)

Operator: the effect-route tests (R1g, R1h) are acceptable only if the spawned entities can be killed after the test. Two killable routes are now in the probe.

- **R1n. Carbon `ClientEntity`.** Source: Carbon.Common `src/Carbon/Components/ClientEntity.cs`, namespace `Carbon.Components`. This is a Carbon-native, client-only networked entity with a kill handle. The API is `Create(prefabName, position, rotation, proto, netId, group)`, `SpawnAll(IList<Connection>)`, `Position` plus `SendNetworkUpdate_Position()` for movement, `KillAll(mode)`, and `Dispose()`. Carbon's own docs describe it as a visual-only utility. The probe calls it by reflection, so the plugin compiles even if the type is not in the reference set. The route also tests movement: the body rises 40 m over 10 s, then is killed at 20 s. Open question: whether the client can build a prefab that is not a networked entity. The probe will show this. The string-pool lookup may also reject a non-entity path.
- **R1o. Effect with an entity parent.** Forum report (Oxide, 2015): an effect attached to an entity is destroyed with that entity. The probe finds the 7-argument `Effect.server.Run` overload that takes a `BaseEntity` by reflection, logs every `Run` overload it finds, attaches the silo missile to a networked warhead bus, and kills the bus at 15 s. The forum report is not verified against our build.

Not killable: R1g and R1h (plain `Effect.server.Run`, no handle). They remain available only with `go`. Superseded by R1n and R1o for any run that must clean up.

**Run 7 order (revised):** R1i → R1j → R1k (read-only) → R1n (killable, moves) → R1o (killable) → R1g `go` and R1h `go` only if R1n and R1o both fail → R1m.

## Runs 8 to 11: prefab-id and Harmony routes (2026-10-18)

Position used for runs 8 to 11: operator about (-2065 to -2068, 15.5 to 16.2, 458 to 464), yaw 0, bus 8 m in front at about 2 m.

| Probe | Version | Route | Result (operator) | Cleanup |
|---|---|---|---|---|
| R8 | v0.11.3 | Networked nuclear warhead bus, control | "The missile bus was visible." | OK |
| R1n | v0.11.3 | Carbon `ClientEntity`, silo prefab, rise 40 m | "The missile body was not visible to the operator." Client received prefab id 3918629484. | OK |
| R1q | v0.11.4 | Harmony prefix and postfix on `SendAsSnapshot`; prefab id swapped only during that snapshot | "I saw the bus ... nuclear_warhead.prefab ... this did not however change to intended." Swaps 1, restores 1. | OK |
| R1r | v0.11.5 | Prefab id set to silo before `Spawn`, no Harmony | Run 10: "did not observe a missle apparing." Run 11: "Missile bus was not visible. Missile body was not visible." | OK |
| R1s | v0.11.5 | Harmony on `SendAsSnapshot`, `ToStreamForNetwork`, `CanUseNetworkCache`; swap on serialization | Run 10: "did not observe an upright missle appear in any way." Run 11: "Missle bus was not visible. Missile body was not visible either." Counts: snapshot 1, stream 3, cache true 4; swaps 4, restores 4. | OK |
| R1p | v0.11.3 | Harmony discovery (read-only) | Prefab id field `BaseNetworkable.prefabID`; StringPool id for silo prefab 3918629484. | n/a |

**Findings**
- R8 shows the networked bus at this spot, so the control works.
- The silo prefab ID is sent to the client (R1n), but no body appears.
- Swapping the ID only during one snapshot (R1q) still shows the warhead bus. Setting the silo ID before spawn (R1r) and across all serialization paths (R1s) shows nothing. The client seems to build an empty entity from the silo prefab ID.
- The only visible silo missile found so far is the monument's own child object at (-2074.21, -19.95, 343.13) to (-2074.76, 16.34, 343.08). It is static and cannot be moved.
- No tested route has produced a movable, visible silo missile. The hard requirement for the initial missile (FR-25) is not met.

**Open decision (operator):** whether to close the Harmony and prefab-id route as negative, and how to treat FR-25 while no route works.

## Run 12: R1o and R1t (v0.11.7, operator at (-2066 to -2068, 16.2 to 16.4, 463 to 472))

| Probe | Route | Result | Cleanup |
|---|---|---|---|
| R1o | Effect with the silo prefab parented to a networked bus 14 m in front | Operator saw the parent bus spawn, did not see the silo missile. Observation text was truncated in the box: "I saw the missile bus spawn in front of me, however I did not see the". Bus killed at 15 s. | OK |
| R1t | `GameManager.server.CreateEntity` on the silo prefab path | Returned null. The silo prefab is not a networked entity prefab. Route closed. | OK |

**Open:** R1g `go` and R1h `go` (plain effect, no handle) are not killable and remain blocked by the killability rule. The text truncation in R1o needs a fix before its observation can be trusted.

**Run 12 operator observations (follow-up):** R1o: "I saw the bus but no missle." R1t: "nothing spawned" (consistent with the null return from CreateEntity).

**Observation box truncation (open bug):** R1o's observation was cut at 69 characters. Cause not yet confirmed. v0.11.8 adds `/probe more <text>` to append the rest of an observation (logged as OBSERVE-MORE), and `/probe boxtest` to find where the box cuts a numbered test string.

**Client console evidence (run 12 testing, reported by operator):** three times, "Tried to spawn an effect without a EffectRecycle component (assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab)". The client rejects the silo prefab as an effect because it has no `EffectRecycle` component. This explains the effect-route results (R1g, R1h, R1o). The effect route is closed for this prefab, and the rejection is on the client's asset, which the server cannot change.

**Client console (run 12 and 13, from operator):**
- 16:40:11 (during R1s): "CreateEntity called on a prefab that isn't an entity!" and "Couldn't create network entity!" for the silo prefab. The client cannot build the silo prefab as a network entity from the ID swap. This explains R1r and R1s showing no body.
- 16:46:56 (during R1o): "Tried to spawn an effect without a EffectRecycle component". The client refuses the silo prefab as an effect.
- 16:41:01 and 16:46:01: "Curl error 6: Could not resolve host: cdp.cloud.unity3d.com". Unity cloud diagnostics. The client has no internet access. Unrelated.

**Box capture test (BOXTEST, 17:04):** the box sent 79 characters (0001 to 0016) and cut off before 0017. `/probe more` delivered 114 characters intact (0017 to 0039). Run 9 earlier delivered a 137-character observation intact, so the box has no fixed length limit. The text is lost in the client field at focus loss, cause not yet known. v0.11.9 echoes the captured length after each observation, so the operator can send the rest with `/probe more`.

**BOXTEST, 2026-10-19 19:54 UTC (v0.11.8, session 195417):** a pasted 122-character lorem ipsum passage (16 words) reached the probe intact in one submit. No cut. The probe's test string was 0001 to 0040, so this was not the expected text. Earlier cuts (69 and 79 characters) came from typed input. The client `output_log.txt` has no entries near the 19:56:37 submit. The in-game console's Log File button opens `output_log.txt`.

## debug.lookingat survey, silo area (operator, 2026-10-24, console 03:12 to 03:15Z)

| Object (as shown) | Ent | Pos | Layer | Hierarchy | Note |
|---|---|---|---|---|---|
| nuclear_silo_missile | - | (-2073.35, 14.80, 344.41) | World | Props | Not an entity. |
| hatch_body | door.hinged.nms_hatch | (-2066.39, 20.85, 345.22) | Construction | assets/bundled/prefabs/static/door.hinged.nms_hatch.prefab, root / main_piston_top / hatch_body | Networked entity; silo hatch. |
| nuclear_silo_chute_platform_a (5) | - | (-2077.52, 7.79, 340.62) | World | Props | Not an entity. |
| nuclear_silo_chute_vent_d (2) | - | (-2078.32, -1.28, 339.82) | World | Props | Not an entity. |
| nuclear_silo_missile (second hit) | - | (-2074.61, 1.39, 341.03) | World | Props | Same object as the first hit (positions vary along its length). |

Notes:
- The silo missile is scene geometry under Props with no network entity. This matches the client refusals.
- The silo hatch is a networked BaseEntity. It has not been checked for a missile child. Candidate for R1k/R1u follow-up.
- The Log File button opened output_log.txt, as expected.
- Nothing here establishes a spawn route. R1u (`/probe r1u`) is still pending.

## R1u (operator, 2026-10-24, session 03:21Z, plugin v0.11.14): partial, observation lost

- Ran `/probe r1u` at 03:23:00Z. Log captured up to the observation box; the rest of the log is partial because the plugin was reloaded.
- Operator observation: **nothing seen** 8 m in front. No visible spawn.
- Tracked entities: 0. Cleanup items: 0. `CreateEntity` returned null again.
- `CreatePrefab` (two overloads) returned local GameObjects only: Transform and MeshCollider, destroyed at once. Not networked.
- The in-situ silo missile has no visual components, only a MeshCollider under `Missile_Chute/Props`. The server prefab root matches it. The server copy is collision-only.
- Sweep gap: `GameManager.Instantiate(string, Vector3, Quaternion)` was not invoked. Fixed in v0.11.15 (`e153df7`); R1u must be re-run to complete the sweep.
- Box failure (v0.11.14): buttons never reached the plugin; no submit or close entries. Fixed in v0.11.15 (buttons unprotected, keyboard capture removed, `/probe end` reopens a stuck box). Not yet verified in game.
- Reload during the run: the unload path records the run as MISSING, so the observation is lost. Use `/probe end` rather than reloading.

## R1u, complete run (session 10:10Z, plugin v0.11.15), supersedes the partial entry above

- Sweep now covers all four GameManager (string, Vector3, Quaternion) methods: CreatePrefab x2, CreateEntity, Instantiate.
- CreatePrefab (x2) and Instantiate returned local GameObjects only (Transform and MeshCollider), destroyed at once. Not networked.
- CreateEntity returned null.
- Tracked entities 0, cleanup OK, errors 0. Effect route skipped (client refuses the prefab).
- In-situ silo missile and server prefab root: MeshCollider only, no MeshFilter or Renderer.
- **Operator observation (chat, /probe obs): "No missle was seen."** No visible spawn.
- Box on v0.11.15: X closed the box (buttons respond now). The input field sent text at 10:10:59 (empty) and 10:11:06 ("tt", 2 characters), then the box closed without submitting. Submit not yet tested. The chat fallback `/probe obs` saved the observation.
- Open: the input field sends its text on end-edit, and it sent after 2 characters. The cause is not yet known. Same family as the 79-character cut.

## BOXTEST on v0.11.15 (session 10:14Z): box round trip works

- Box opened from `/probe boxtest` with no `/probe end`. Good.
- Operator typed a 59-character sentence. The input field sent it in one event at 10:14:35.007 and Submit saved it at 10:14:35.099. Observation saved, cleanup OK, 0 errors.
- Submit works. The earlier 2-character "tt" send did not repeat.
- Operator reports the box buttons were blank (no visible labels), so they used `/probe more` to submit. Rendering is not confirmed.
- `/probe stop` was typed after the run. It is not a command in the plugin, and the log shows no reply.
- The 199-character 0001 to 0040 string has still not been tested.

## Run order for plugin v0.11.17 (2026-10-24)

Reload the plugin only when no probe is running and no box is open. A reload records a running probe as MISSING and its observation is lost.

1. Check the banner says **v0.11.18**.
2. `/probe start`
3. `/probe boxtest`. The box opens at once. Type 0001 to 0040 (199 characters). Press Submit. Report any cut and send the log.
4. `/probe scales`. Walk to the row, about 80 m in front. Look at the six MLRS rockets (1×, 5×, 10×, 15×, 20×, 25×). Report which one looks like a realistic ICBM against the terrain and your height. Then `/probe scales end`. The observation is queued; no box opens.
5. `/probe flight`. Stand where you can see the booster and bus. The run ends by itself when the booster has left the play area and the bus has reached the ground. Watch for: the booster climbing, the bus separating, the booster descending slowly, and it going out of view. Compare its size with the 20× rocket in step 4 from the same distance. If it looks like 1×, scale replication (Q-H) has failed. Do not run `/probe end` after this one. The observation is queued and the probe ends itself.
6. `/probe flight cam`. Same as step 5. Also note the camera identifier in the log (`CAMERA booster cam ... identifier='NUKE…'`). Check whether a computer station shows the camera feed, and report what you see.
7. `/probe outstanding`. Submit each observation box in turn: scales, flight, flight cam. Each Submit opens the next.
8. `/probe end` closes the session.

To stop a running probe at any point: `/probe stop` (same as `/probe abort`).

What the logs will show:
- **SCALE** lines: each rocket's localScale and lossyScale, and its distance from you.
- **FLIGHT** lines (every second): booster and bus positions, the booster's scale, and the distance from each online player.
- **SEPARATION**, **FLOAT-OUT** and **IMPACT** lines: when each event happened and where.
- **CAMERA** line: the camera's identifier and where it was attached.
- **ERROR** lines: anything that threw. Send them with the log.

Still unverified (these results decide the next step):
- Scale replication to clients (Q-H): compare the flight booster with the scaled row.
- Visibility to all players regardless of location (Q-I): needs a second player at a distance.
- The box buttons (`isProtected` false): Submit and Add part are to be tested in step 3 and step 7.


## Exact probe list, plugin v0.11.19 (2026-10-24)

Reload only when no probe is running and no box is open. Check the banner says **v0.11.20**.

### A. 6000 m map (loaded now)

| # | Command | What to do | Observe |
|---|---|---|---|
| 1 | `/probe start` | Opens the session. | Banner. |
| 2 | `/probe boxtest` | Box opens at once. Type 0001 to 0040 (199 characters), then Submit. | Any cut. Submit and Add part work. |
| 3 | `/probe scales` | Walk to the row, about 80 m in front. Then `/probe scales end`. | Which of 1x, 5x, 10x, 15x, 20x, 25x looks like a realistic ICBM. |
| 4 | `/probe flight apogee=1000` | Run ends itself. | Climb, separation, slow descent, booster leaves the play area, bus lands. From what distance does the booster disappear? |
| 5 | `/probe flight cam apogee=1000` | Run ends itself. | As step 4. Note the camera identifier in the log. Check a computer station for the feed. |
| 6 | `/probe flight apogee=600` | Run ends itself. | As step 4, lower apogee. Record the disappearance distance. |
| 7 | `/probe flight apogee=2000` | Run ends itself. | As step 4, higher apogee. Record the disappearance distance. |
| 8 | `/probe outstanding` | Submit each box in turn (scales, flights, camera flight). | Each observation is saved. |
| 9 | `/probe end` | Closes the session. | Summary. |

### B. Smaller map (load it, then repeat)

| # | Command | What to do | Observe |
|---|---|---|---|
| 1 | Load the smaller map. Reload the plugin. Check the banner says v0.11.19. | | |
| 2 | `/probe start` | | Banner shows the new map size (`worldsize`). |
| 3 | `/probe scales` | If the row is refused as outside the play area, walk toward the centre and run it again. Then `/probe scales end`. | Same 1x to 25x row. Is the row inside the map? |
| 4 | `/probe flight apogee=1000` | Run ends itself. | Log shows the half-size (worldsize / 2) and the kill margin. The booster should leave the play area sooner than on 6000 m. |
| 5 | `/probe flight cam apogee=1000` | Run ends itself. | As step 4. |
| 6 | `/probe outstanding`, then `/probe end` | Submit each box. | |

Centring check (Q-K): in the log, your position should lie between -half and +half on both axes. If you stand near a map corner and your coordinates fall outside that range, the map is not centred on 0. Send the log.

Commands that can be used at any time: `/probe stop` (aborts the running probe).

# M0 — Questions and Probe Kit

> **Purpose.** Everything needed to (a) answer the open design questions one at a time and (b) run the
> five M0 probes on the live server.
>
> **Companion:** `DESIGN_BRIEF.md` §13 holds the same questions in short form. This file is the
> working copy: answer here, then the brief gets updated and the question is closed.
>
> **Status:** awaiting operator input. Nothing in M1 is blocked except by Part A.

**Contents**

- [Part A — Decisions (answer each one)](#part-a--decisions)
- [Part B — Assets I need from you](#part-b--assets)
- [Part C — Probes: what they are and how to run them](#part-c--probes)
- [Part D — Report template](#part-d--report-template)

---

## Part A — Decisions

Answer in any order; "you decide" is a valid answer and the default is used. Each question is
self-contained — you do not need to re-read the brief.

---

### Q1 — Should the vanilla supply drop still happen?

**Question.** When a player throws the Nuclear Football, should Rust's normal supply-signal behaviour
still run (cargo plane flies in, drops a supply crate)?

**Why it matters.** A cargo plane crossing the sky at the same moment as the nuke steals the
attention and adds a second, unrelated objective. It also gives players a reason to run *toward* the
target instead of away.

**Options.**

- **A. Suppress it.** The football only calls the strike. *(recommended)*
- **B. Let both happen.** Free loot at a nuclear strike; chaotic but thematic.
- **C. Suppress only for non-admins.**

**Answer:** ______________________________________________________________________

---

### Q2 — Which prefab is the air-raid siren?

**Question.** There is no confirmed vanilla "air raid siren" prefab. What should play, and where
should it play from?

**Why it matters.** `Effect.server.Run(..., broadcast: true)` delivers the effect to **every**
connected client (verified), but the client still plays it *at a world position* with normal 3D
falloff. Networked everywhere ≠ audible everywhere. How we place it decides whether the requirement
"audible to all players" is actually met.

**Options.**

- **A. You supply a siren prefab path**, and we place several anchors around the map. *(recommended)*
- **B. One anchor at map centre.** Simplest; likely inaudible at the edges.
- **C. Per-player local playback.** Play the sound at each player's own position, sent only to them
  (`sourceConnection`), so distance falloff cannot apply. Guarantees audibility; sounds "inside your
  head" rather than spatial.
- **D. A + C combined.** World anchors for atmosphere, plus a quiet per-player layer so nobody misses
  it. *(strong alternative)*

**Answer:** ______________________________________________________________________

**If A or D: paste the prefab path(s) here:** ______________________________________

---

### Q3 — Which effect is the water-exit cue?

**Question.** You mentioned a specific Rust effect that gives the audio cue of the missile clearing
the water. What is its prefab path?

**Why it matters.** It is the transition from "something is happening out at sea" to "the missile is
airborne", and it is the first moment players know where to look.

**Answer (prefab path):** ________________________________________________________

---

### Q4 — Bus, plumes and separation

**Question.** Four assets are needed for the missile body phases. Do you have specific prefabs, or
should I pick vanilla approximations and flag them as placeholders?

| Slot | What it must look like | Your prefab path |
|---|---|---|
| Bus | The post-boost vehicle, reads as "the tip of the missile" | |
| Booster plume ×4 | Fire/smoke at the four nozzles | |
| Separation flash | Covers the booster → bus swap | |
| Pink/purple smoke | The target marker column (see Q13) | |

**Why it matters.** These are the difference between "a prefab moving" and "a missile launch". I will
not invent paths — a wrong path fails silently at 60 seconds into the sequence.

**Answer:** fill the table, or "use placeholders" → ______________________________

---

### Q5 — How does a player obtain the football?

**Question.** "Rare to obtain" — by what mechanism?

**Options.**

- **A. Admin grant command only** (`/nuke give <player>`). *(recommended for v1)*
- **B. Airdrop chance** — configurable % chance for an airdrop to contain one.
- **C. Loot table** — added to elite crates / locked crates at a configurable weight.
- **D. Craft** — expensive recipe.

**Why it matters.** It decides whether the feature is an admin event tool or a player-driven economy
item. It also decides what has to be tested before release.

**Answer:** ______________________________________________________________________

---

### Q6 — Should the six MIRVs show on the map?

**Question.** Vanilla `MLRSRocket` creates a map marker in `ServerInit` — every rocket, automatically.
Six MIRVs means six incoming markers unless we suppress some.

**Why it matters.** Six tracks is great theatre and tells players "this is the end". It also reveals
that five of them are decoys, which is either the joke or the spoiler, depending on your taste.

**Options.**

- **A. All six visible.** *(recommended)*
- **B. Only the live one visible.**
- **C. None visible** — the whole thing arrives unannounced except for the sirens.

**Answer:** ______________________________________________________________________

---

### Q7 — How big is the nuclear explosion, and how is it built?

**Question.** A single `TimedExplosive.Explode()` is one prefab effect with one radius. A credible
nuclear detonation needs layers: flash → fireball → shockwave ring → stem → mushroom canopy → dust
ring, plus several damage rings.

**Options.**

- **A. Layered vanilla composition**, tuned visually, ~160 m light radius. *(recommended)*
- **B. One big vanilla explosion**, cheap and ugly.
- **C. Wait for an FX research pass** before deciding.

**Also tell me:** the lethal radius you want (kills players outright), and whether structures should
be destroyed or only damaged.

**Answer:** ______________________________________________________________________

---

### Q8 — Who can watch the missile camera?

**Question.** The CCTV feed — who is allowed to view it?

**Options.**

- **A. Anyone with a permission**, from any computer station on the map. *(recommended)*
- **B. Anyone, no permission** — maximum spectacle.
- **C. Only the player who called the strike.**
- **D. Only admins.**

**Why it matters.** It determines whether we need to auto-add camera bookmarks to stations, and
whether the camera entity must be discoverable by the station UI at all.

**Answer:** ______________________________________________________________________

---

### Q10 — Where is a strike *not* allowed?

**Question.** What happens if the smoke lands somewhere the nuke should not go?

**Options.**

- **A. Refuse safe zones and water; allow everywhere else in-bounds.** *(recommended)*
- **B. Also refuse monuments** (a list in config).
- **C. Allow anywhere** — the player's problem.
- **D. Always allow, but deflect the target to the nearest valid point.**

**Why it matters.** A nuke inside a safe zone or under the map is a support ticket, a wipe-day
argument, or both. This is also where "what if it lands in deep water" gets decided.

**Answer:** ______________________________________________________________________

---

### Q11 — "80 % / 95 % of apogee" — altitude or path?

**Question.** When the brief says the plumes stop at 80 % of apogee and the bus spawns at 95 %, is
that 80 % of the **height** (Y), or 80 % of the **distance travelled along the arc**?

**Options.**

- **A. Height.** `y ≥ apogee × 0.80`. *(recommended — matches "Y level 1000")*
- **B. Path distance.**

**Why it matters.** Path-based fractions fire much later on a shallow arc and change the whole
timeline. Altitude is also what a player can see.

**Answer:** ______________________________________________________________________

---

### Q12 — What happens to the booster at hand-off?

**Question.** When the bus spawns inside the tip, what happens to the original missile body?

**Options.**

- **A. Killed instantly in the same tick**, masked by the separation flash. *(recommended)*
- **B. Cut loose and fall**, so players see it tumble away.
- **C. Kept as a non-networked prop** until cleanup.

**Why it matters.** A visible falling booster is a nice detail but it is another entity to network,
kill and account for — and it doubles the chance of leaving an orphan on reload.

**Answer:** ______________________________________________________________________

---

### Q13 — Pink/purple smoke: which asset? *(new — found during research)*

**Question.** The vanilla supply signal produces its own smoke colour, which is not pink/purple.
Do you have a coloured-smoke effect prefab, or should I find one?

**Options.**

- **A. You supply the prefab path.** *(recommended)*
- **B. I find the closest vanilla coloured smoke** and flag it as a placeholder.
- **C. Accept the vanilla supply-signal smoke colour** for v1.

**Answer (prefab path):** ________________________________________________________

---

### Q14 — How does a player find the camera feed? *(new — found during research)*

**Question.** A computer station only auto-lists **static** cameras within a small radius
(`GatherStaticCameras`); any other camera must be added as a bookmark by identifier. A camera
attached to a missile is nowhere near a station, so how should players get the feed?

**Options.**

- **A. Plugin adds the bookmark automatically** to stations near the player/caller, and prints the
  camera ID in chat. *(recommended)*
- **B. Print the camera ID in chat** and let players add it manually.
- **C. Skip stations**: open the feed directly as a UI overlay.

**Why it matters.** If this is wrong, the camera exists and nobody can watch it — the single most
expensive kind of "working" feature.

**Answer:** ______________________________________________________________________

---

### Q15 — Confirm "FP" *(minor)*

**Question.** Is "FP Debug mode" first-person debug — i.e. the lines are drawn in the triggering
admin's own view, using the client's `ddraw` commands?

**Options.** **A. Yes, first-person.** *(assumed)* — **B. Something else** (describe).

**Answer:** ______________________________________________________________________

---

## Part B — Assets

Paste exact prefab paths. Anything left blank becomes a placeholder and is flagged in the brief's
asset register.

| ID | Asset | Path |
|---|---|---|
| A1 | Air-raid siren | |
| A2 | Water-exit cue | |
| A3 | Bus / post-boost vehicle | |
| A4–A7 | Booster plume ×4 | |
| A8 | Separation flash | |
| A9 | Pink/purple smoke | |
| A10 | Nuclear FX layers (flash / fireball / shockwave / stem / canopy / dust) | |

Two prefabs are already confirmed and need no answer:

- Missile: `assets/content/structures/nuclear_missile_silo/nuclear_silo_missile.prefab`
- MIRV: `assets/content/vehicles/mlrs/rocket_mlrs.prefab`

---

## Part C — Probes

A probe is a **short, disposable test on the live server that answers a question the documentation
cannot**. Each one either unlocks a design decision or kills a risky assumption before it becomes
code. Five in total; about 20 minutes.

They exist because four things in this design depend on engine behaviour that is *not* documented and
that I will not guess:

1. whether the missile prefab can actually be spawned,
2. whether a CCTV camera keeps working when it is attached to something that moves,
3. whether `ddraw.clear` exists on the client,
4. whether a broadcast effect is audible at the far side of the map,
5. whether a Harmony prefix can suppress the vanilla supply drop cleanly.

Run them in this order. R1 is the one that can invalidate the most work, so do it first.

---

### Before you start (R0 — environment)

Capture once, paste into Part D:

- `c.version` output
- the Rust build/protocol from the server startup banner
- map size and seed (from your startup command)
- `c.plugins` — count is enough
- whether this is a test server or the live one

**Use a test server for R5.** It patches every supply signal while enabled.

---

### R1 — Can the missile prefab be spawned? *(5 minutes)*

**The unknown.** `nuclear_silo_missile.prefab` exists in the client assets. It may be a decorative
mesh with no `BaseEntity` component, in which case `CreateEntity` returns `null` and the missile
cannot simply be spawned like a normal entity.

**Why it matters.** If it cannot be spawned, the whole visual needs a wrapper (a scene object
parented to an invisible entity), which changes the rig design before any of it is written.

**How.**

1. Copy `probes/TheatricalNukeProbe.cs` to `carbon/plugins/`.
2. `c.reload TheatricalNukeProbe` (or wait for auto-load). Confirm it appears in `c.plugins`.
3. Stand somewhere flat and open, in daylight.
4. `/probe missile`
5. Watch the console and look in front of you.

**What you should see.**

| Console line | Meaning |
|---|---|
| `FindPrefab -> NULL` | The server cannot load that path at all — path is wrong for this build. |
| `BaseEntity component -> NONE` | The prefab is decorative; it needs a wrapper. |
| `BaseEntity component -> <class>` | It is a real entity; note the class name. |
| `CreateEntity -> NULL` | Not spawnable as an entity despite having components. |
| `Spawn() -> OK` + a visible missile | Best case: it spawns and looks right. |

**Report:** the console lines, whether you can see it, and — if visible — whether it is upright, the
right size, and which way it faces.

---

### R2 — Does a CCTV camera work on a moving parent? *(5 minutes)*

**The unknown.** Whether a plugin-spawned `cctv.static` can be parented to a moving object and still
produce a usable, following feed — and how a player selects it.

**Why it matters.** The camera is a core beat ("view from the side of the missile"). If parenting
does not work, the camera must be driven as its own entity following the rig, which is a different
implementation.

**How.**

1. Stand in the open. `/probe cctv`
2. Note the **identifier** printed in chat (something like `CCTV_1234567`).
3. Sit at a computer station. Add a bookmark using that identifier.
4. Watch the feed. It should be looking back at you from 3 m away.
5. **Walk around, jump, turn.** The feed is parented to you, so the image should move with you.
6. `/probe cctvstop` when done.

**What to report.**

- Did the identifier get accepted by the station?
- Did the feed appear, and was it the right way up?
- Did it **follow** you when you moved, or stay fixed in the world?
- Any lag, stutter or black frames?
- Console lines: `IsStatic`, `ConsumptionAmount`, `SetParent(...) -> OK`.

**If the feed never appears**, note whether the camera looked unpowered — that changes how we spawn
it.

---

### R3 — Does `ddraw.clear` exist? *(2 minutes, no plugin)*

**The unknown.** Whether the client has a clear command for debug drawing, or whether drawings can
only be cleared by letting them expire.

**Why it matters.** Your requirement says "add a command to clear the debug lines". If there is no
clear, the command has to stop the refresh loop and tell the admin when the lines disappear.

**How.**

1. In game, press **F1** to open the console.
2. Paste: `ddraw.line 10 1 0 0 0 0 0 10`
   (duration 10 s, red, from origin to origin+10 up — this draws in the world, not at your feet, so
   use `/probe ddraw` instead if you want something visible near you).
3. Better: run `/probe ddraw` in chat, then immediately type `ddraw.clear` in F1.

**Report:** did the lines disappear immediately, or did the console say "unknown command"?

---

### R4 — Is a broadcast effect audible across the map? *(5 minutes)*

**The unknown.** `broadcast: true` sends the effect to every client (verified in source), but the
client plays it positionally. We need to know how far it actually carries.

**Why it matters.** It decides whether the siren design is "a few anchors" or "a per-player layer as
well" (Q2 option D).

**How.**

1. Stand at one corner of the map. Note your grid.
2. `/probe fx <siren-prefab> <x> <y> <z>` with coordinates at the **opposite** corner.
   - Don't know coordinates? Use the default: `/probe fx <prefab>` plays it 2 km in front of you.
3. Listen. Then repeat, standing at the far corner and playing it at your original position.
4. If you have a candidate siren prefab, use it; otherwise use a loud known effect such as
   `assets/bundled/prefabs/fx/impacts/additive/explosion.prefab` to test the *mechanism*.

**Report:** audible yes/no at each distance, roughly how loud, and whether the sound was delayed
(it should be immediate — the delay is a client audio feature, not the network).

---

### R5 — Can we suppress the vanilla supply drop? *(5 minutes, test server only)*

**The unknown.** Whether a Harmony prefix on `SupplySignal.Explode` cleanly prevents the cargo plane,
with no side effects.

**Why it matters.** It is Q1's implementation. `SupplySignal.Explode()` creates the cargo plane
directly (`CreateEntity(EntityToCreate)`), so there is no hook to cancel it *before* the plane exists.

**How.**

1. `/probe supply on`
2. Throw a supply signal somewhere visible.
3. Watch: **no cargo plane** should spawn, and the console should print
   `[R5] SupplySignal.Explode intercepted`.
4. `/probe supply off`
5. Throw another — the cargo plane should return.

**Report:** plane present/absent in each step, plus any console errors.

**If the probe plugin fails to compile**, the error will name the file and line. Send it and skip R5;
R1–R4 do not depend on it.

---

## Part D — Report template

Copy this into chat or a text file and fill it in. Partial answers are fine — send what you have.

```text
## Environment (R0)
- Carbon:            <c.version>
- Rust build:        <startup banner>
- Map size / seed:   <...>
- Server:            test / live

## Answers
Q1  (supply drop):        A / B / C
Q2  (siren prefab):       A / B / C / D   path: ...
Q2b (coverage):           anchors / per-player / both
Q3  (water-exit):         path: ...
Q4  (bus/plumes/sep):     paths: ...  or "placeholders"
Q5  (acquisition):        A / B / C / D
Q6  (map markers):        A / B / C
Q7  (blast scale):        A / B / C   lethal radius: ... m  structures: destroy / damage
Q8  (camera viewers):     A / B / C / D
Q10 (target limits):      A / B / C / D
Q11 (80/95 %):            altitude / path
Q12 (booster at swap):    A / B / C
Q13 (smoke colour):       A / B / C   path: ...
Q14 (camera bookmark):    A / B / C
Q15 (FP meaning):         A / B

## Probes
R1 missile:   FindPrefab=...  component=...  CreateEntity=...  visible=yes/no  notes=...
R2 cctv:      identifier=...  accepted=yes/no  followed=yes/no  lag=...  notes=...
R3 ddraw:     clear worked=yes/no  error=...
R4 siren:     at 2 km=...  at map corner=...  notes=...
R5 supply:    suppressed=yes/no  plane returned=yes/no  errors=...
```

---

## What happens with the answers

| Answer | Effect |
|---|---|
| Q1, Q2b, Q5, Q8, Q10, Q12, Q14 | Close the decision; `DESIGN_BRIEF.md` §7 config and §13 are updated. |
| Q2, Q3, Q4, Q13 | Fill the asset register; prefabs are validated at load. |
| Q6, Q7, Q11, Q15 | Tune defaults; no structural change. |
| R1 fails | Rig design switches to the wrapper approach before M3 starts. |
| R2 fails | Camera becomes a free-flying entity following the rig; FR-60/61 change. |
| R3 fails | `/nuke debug clear` becomes "stop refresh + report expiry" (§8.3 fallback). |
| R4 fails at map edge | Add the per-player audio layer (Q2 option D). |
| R5 fails | Suppression moves to intercepting the throw; Q1 answer may change. |

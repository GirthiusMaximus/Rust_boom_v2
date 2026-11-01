# Karuza's custom vehicles: research and whitebox feasibility

Date: 2026-11-01. Scope: how Karuza's Plugin Corner (karuza.dev) appears to make custom vehicles and entities work, and whether a whitebox (clean-room) version can be built here.

This is research from public pages only. Nothing was decompiled, and no paid plugin code was obtained or read.

## 1. Sources and what each says

| # | Source | Claim | Confidence |
|---|---|---|---|
| S1 | karuza.dev store, Rust Cars (fetched 2026-11-01) | Paid, closed-source vehicle packs. DuneTruckUnarmed has 98 prefabs and DuneTruckArmed has 104. Features: persistence, code locks, physical engine, fuel container, storage, radio, weapons. | Verified (vendor's own listing) |
| S2 | Reddit r/playrust, Karuza1 (2025-06-29, search snippet) | "The vehicles are registered in the game manifest with my custom implementations of BaseVehicle so as far as the game is concerned they are vehicles shipped with the game. They get their own prefab path (assets/custom/fighter.prefab for example). I also don't use any hooks." Works on Oxide and Carbon. | Vendor's statement, not verified in code |
| S3 | Reddit r/playrustadmin, Karuza1 (2026-05-05, search snippet) | "All the vehicles are registered in the game manifest so the game believes they were shipped with it." Spawn by prefab path. Works with shop, licence and NPC plugins. The same thread mentions an ICBM model made for an event. | Vendor's statement |
| S4 | game4freak.io plate-offset thread (2025-10-29) | Config keys use `assets/custom/warthogt.prefab`, `assets/custom/semitruck_blue.prefab`, `assets/custom/jeepaa.prefab`, and so on. | Third-party, shows the path convention |
| S5 | uMod Vehicle License (updates page) | Karuza vehicles are listed with a per-vehicle spawn height. The plugin notes that Karuza vehicles are "only spawnable if you *have* those plugins installed". | Third-party |
| S6 | WeOxide Vehicle Shop 1.0.2 (2026-06-04) | "This plugin will not spawn custom vehicles if you do not own any Karuza's vehicles." Requires the Karuza Vehicles plugin for custom vehicles. | Third-party |
| S7 | Karuza Vehicle Hammer (Wrecks, RogueDepot, per earlier notes) | Karuza vehicles are not serialised. Picking one up gives a deployable item that respawns a new instance. | Third-party, earlier session |
| S8 | Codefling "Spawn Everything" (2026-10-29) | Has a setting to add all Karuza custom vehicles, with the default off. | Third-party |
| S9 | RogueDepot "High-End Car Dealership" (2026-04-27) | Map prefab "Depends on RustEdit" and "Works with Karuza's Custom Vehicles". | Third-party |
| S10 | Rust.MD §10 (in this repo) | States that a stock client cannot instantiate unknown prefab IDs and that there is no supported bundle-delivery path. | Internal note, not a Facepunch source |
| S11 | Custom Entities (Nikedemos, `/tmp/ce`, read earlier) | Registers custom names in `preProcessed.prefabList`, `GameManifest.Current.entities` and `StringPool`. The network message sends `ClientsidePrefabID`, a vanilla name the client already builds. | Read in source |

## 2. What the public evidence supports

1. **Registration (supported by S2, S3, S4, S5).** Karuza's vehicles are added to the game manifest under `assets/custom/*.prefab`. Plugins then spawn them through the normal prefab-path API. That is why shop and licence plugins work without special hooks.
2. **Behaviour (supported by S2).** Each vehicle has a custom class that derives from `BaseVehicle`. The class is not reached through Oxide or Carbon hooks.
3. **Dependency on the plugin (supported by S5, S6).** Without the Karuza plugin loaded, its vehicles are not spawnable. The plugin is the thing that registers them at runtime.
4. **No serialisation (supported by S7).** Karuza vehicles are not written to the save file as their own entity type. Pickup converts them to an item. This is a design choice, not a platform limit.

## 3. What the public evidence does not explain (open question)

**How a vanilla client gets a model it does not ship with.** S2 and S3 say the vehicles appear on vanilla clients and do not need a client mod. Our own notes (S10) say a stock client cannot instantiate an unknown prefab ID. Both cannot be fully true unless one of the following holds:

- (a) The meshes already exist in the shipped client, under other names, and Karuza's prefabs re-use them. This would make the "custom" part only a re-packaging of existing assets. Not verified.
- (b) Karuza delivers an asset bundle through a channel we have not identified. Not verified. Carbon's client-side work is still in progress (see the earlier Carbon docs search), so this is not confirmed.
- (c) The vendor's claim, as stated, is incomplete. Not verified.

This is the single most important unknown for a whitebox. Registration and behaviour can be reproduced. Visibility for clients depends on this point, and the public record does not settle it.

## 4. Can a whitebox be built?

**Yes, partially.**

| Part | Whitebox feasible? | How |
|---|---|---|
| Manifest registration of `assets/custom/*.prefab` | Yes, server side | Use the public Custom Entities approach (`preProcessed.prefabList`, `GameManifest.Current.entities`, `StringPool`). Documented in S11. |
| Custom `BaseVehicle` subclass with driving, fuel, storage and seats | Yes, with effort | Write our own class. Do not copy Karuza's code. |
| Spawn by prefab path, persistence, pickup | Yes | Same as above. Pickup-to-item is a design choice. |
| Client-visible new meshes | **Not established** | Depends on section 3. Probe CE2 tests part of this (below). |
| Reproducing Karuza's exact models and 400+ vehicles | **No, and not wanted** | Their assets are paid and closed. |

**Legal and ethical boundary.** Karuza's packs are paid and closed-source. Decompiling their plugin DLLs, copying their prefabs, or lifting their models would breach their terms and copyright. A whitebox here means a clean-room design built from public behaviour, the public Custom Entities source, and our own assets. It does not mean reproducing their binaries.

## 5. Probe CE2 (added in v0.11.25, read-only)

`/probe CE2` scans the server's shipped manifest for `assets/custom/` names, `assets/bundled/prefabs/modding/`, `assets/content/vehicles/` and `assets/prefabs/vehicle/`. It does not register or spawn anything.

- If the manifest already lists `assets/custom/` names before any plugin runs, path (a) or a shipped bundle is likely, and the next step is to inspect those prefabs.
- If it lists none, the client must receive those assets some other way. That is option (b) or (c), and the next step is to find the channel.

Run CE2 on a server **without** the Karuza plugin loaded. Otherwise a plugin could be the source of the entries.

## 6. Recommended next steps

1. Operator runs `/probe CE2` on a clean server and reports the RESULT line and the STEP counts.
2. If CE2 finds `assets/custom/` names in the shipped manifest, inspect them (`GameManifest.guidToPath`, bundle contents) before any other work.
3. Do not commit any Karuza code or asset to this repository.
4. Decide with the operator whether a clean-room custom vehicle is in scope at all, given the open visibility question.

## 7. Limits of this research

- Reddit pages could not be fetched directly (HTTP 403). Reddit claims come from search snippets, which are partial.
- Karuza's own docs or Discord were not accessed.
- No Rust server or client was run here. Nothing is compiled here.

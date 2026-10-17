# Rust_boom_v2

Reference set for building C# Rust server plugins on **Carbon** (with a **HarmonyMod** fallback).

| Document | Use |
|---|---|
| `Senior_C.MD` | Senior C#: runtime caveats, idioms, testing, patterns, SOLID, async, concurrency, security, databases, architecture, distributed systems. |
| `CarbonMod.MD` | Carbon + HarmonyMod API reference: install, lifecycle, hooks, commands, permissions, config/data, timers, CUI/LUI, Bridge, extensions, patching, performance, logging. |
| `Rust.MD` | Client/server internals: authority model, tick loop, entities/net IDs/RPC/visibility, wire protocol, spawning/pooling, custom-asset limits. |
| `CARBON_PLUGIN_PLAYBOOK.MD` | Process: artifact choice, project setup, standards, skeleton, hooks, Harmony fallback, error handling, testing, release, wipe/incident runbooks. |
| `Exceptions.md` | Exception standard: boundary guards, global guard, separate per-plugin exception log, ready-to-use code for plugins/extensions/HarmonyMods, fault-injection tests. |
| `DontJustRollWithIt.MD` | Working agreement: evidence tags, when to stop and ask, environment profile, hand-off/feedback packets, decision log. |
| `RESEARCH_REVIEW.MD` | Source recency/accuracy audit and corrections. |

**Read order:** `DontJustRollWithIt.MD` (how we work) → `CARBON_PLUGIN_PLAYBOOK.MD` (the process) →
the reference that the task needs.

**Rule:** evidence or assumption — never a silent guess. See `DontJustRollWithIt.MD`.

## How the set fits together

Five layers, each answering a different question: **method → process → reference → standard →
audit**. Every topic has exactly one owning document; the others point at it instead of restating it,
so an edit lands in one place.

| Layer | Owner | Question it answers | Defers to |
|---|---|---|---|
| Method | `DontJustRollWithIt.MD` | How do we work when something is uncertain? | — |
| Process | `CARBON_PLUGIN_PLAYBOOK.MD` | What do I do next, in order, to ship this? | All reference and standard docs |
| Reference | `CarbonMod.MD` | What is true about the framework (Carbon/Harmony)? | `Rust.MD` for engine behaviour |
| Reference | `Rust.MD` | What is true about the engine and the wire? | `CarbonMod.MD` for API shape |
| Reference | `Senior_C.MD` | What is good C# on this runtime? | `Exceptions.md` for failure policy |
| Standard | `Exceptions.md` | What must every plugin do about failure? | — (it is the definition of done) |
| Audit | `RESEARCH_REVIEW.MD` | Why believe the rest, and what is still uncertain? | Every document's sources section |

The playbook is the only procedural document — follow it in order. Everything else is looked up by
section. `Exceptions.md` is the only standard: playbook §11, `CarbonMod.MD` §19 and `Senior_C.MD`
§1.9 are its process, API and engineering views respectively.

## Routing

| Situation | Start at | Then |
|---|---|---|
| New plugin | Playbook §1 (artifact) → §2 (setup) → §4 (skeleton) | `CarbonMod.MD` §3–§6; `Exceptions.md` §6, §9–§10 |
| No Carbon hook exists | Playbook §6 | `CarbonMod.MD` §5, §13–§14 |
| Hook/command/timer misbehaves | Playbook §11 → `CarbonMod.MD` §19 | `Exceptions.md` §3 (what Carbon catches), §6, §8 |
| Designing exception handling | `Exceptions.md` §5–§9 | `Senior_C.MD` §1.9; Playbook §11 |
| API signature in doubt | `CarbonMod.MD` (source-checked) | `Rust.MD` for engine behaviour; `RESEARCH_REVIEW.MD` §4 for risk |
| Performance work | Playbook §13 | `CarbonMod.MD` §15; `Rust.MD` §13 |
| Security review | `Senior_C.MD` §7 | `CarbonMod.MD` §16; `Exceptions.md` §8.5 |
| Release or incident | Playbook §14–§16 | `DontJustRollWithIt.MD` §8–§9 (packets) |
| "Are these docs still right?" | `RESEARCH_REVIEW.MD` | §3 corrections, §4 residual risks |
| Conflicting or missing information | `DontJustRollWithIt.MD` §4–§5 | Ask before implementing |

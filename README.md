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

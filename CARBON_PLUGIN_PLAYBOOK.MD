# Carbon Plugin Playbook

> **Purpose.** A repeatable process for taking a C# Rust plugin from idea to production on Carbon —
> including the **HarmonyMod fallback** when Carbon has no suitable hook.
>
> **Companions:** `CarbonMod.MD` (framework/API reference), `Rust.MD` (client/server internals),
> `Senior_C.MD` (language, patterns, security, architecture), `Exceptions.md` (exception
> architecture and the separate error log), `DontJustRollWithIt.MD` (evidence/collaboration
> protocol), `RESEARCH_REVIEW.MD` (source audit).
>
> **Assumptions:** server-side only; Carbon on a current Rust build; C# 10-safe plugin code; no
> client mods.

---

## Table of contents

0. [Working agreement](#0-working-agreement)
1. [Delivery model: pick the right artifact](#1-delivery-model-pick-the-right-artifact)
2. [Project and repository setup](#2-project-and-repository-setup)
3. [Coding standards that matter in-game](#3-coding-standards-that-matter-in-game)
4. [Plugin skeleton and lifecycle](#4-plugin-skeleton-and-lifecycle)
5. [Hook selection and authoring](#5-hook-selection-and-authoring)
6. [The Harmony fallback procedure](#6-the-harmony-fallback-procedure)
7. [Configuration, data and secrets](#7-configuration-data-and-secrets)
8. [Commands and UI](#8-commands-and-ui)
9. [Persistence choices](#9-persistence-choices)
10. [External services and networking](#10-external-services-and-networking)
11. [Error handling and exception logging](#11-error-handling-and-exception-logging)
12. [Testing strategy](#12-testing-strategy)
13. [Performance workflow](#13-performance-workflow)
14. [Release, versioning and changelog](#14-release-versioning-and-changelog)
15. [Wipe-day and Rust-update runbook](#15-wipe-day-and-rust-update-runbook)
16. [Incident response and rollback](#16-incident-response-and-rollback)
17. [Review checklists](#17-review-checklists)
18. [Templates](#18-templates)
19. [Sources](#19-sources)

---

## 0. Working agreement

This playbook is used with `DontJustRollWithIt.MD`. Two rules from it apply to every task:

1. **Evidence or assumption — never guess.** Tag claims `[SRC]`, `[DOC]`, `[USER]`, `[TEST]`,
   `[INFERRED]`, `[ASSUMED]` or `[UNKNOWN]`. Source beats documentation; the live Linux/Carbon build
   beats both.
2. **Stop and ask at the high-impact forks.** API presence, patch targets, destructive data
   changes, security/economy behaviour, and architecture choices are question gates. Do the
   research first, then ask a decision-shaped question with options and a recommendation.

The operator has a prepared Linux Carbon environment and can deploy/test. Use that loop: hand off
exact commands, expected output, failure signals and rollback; ask for a feedback packet (`DontJustRollWithIt.MD` §9)
rather than "did it work?".

> **No plugin ships without the exception standard in §11** (`Exceptions.md`): guarded boundaries,
> a separate per-plugin exception log, a global guard, and verified failure behaviour.

---

## 1. Delivery model: pick the right artifact

Work down this list; stop at the first option that satisfies the requirement.

| Requirement | Artifact | Why |
|---|---|---|
| Normal gameplay/admin feature on Carbon | **Carbon `.cs` plugin** | Fast iteration, built-in config/data/permissions/CUI. |
| Same feature must run on Oxide too | `.cs` plugin with `Oxide.Plugins` namespace + `#if CARBON` guards | One source, two hosts. |
| Missing hook, only needed while your plugin is loaded | **`[AutoPatch]` inside the plugin** | Carbon manages apply/unapply on reload. |
| Missing hook, must work on vanilla/Oxide or survive Carbon unload | **HarmonyMod DLL** | Runs without Carbon; standard `IHarmonyModHooks` contract. |
| Shared code for several plugins | **`ICarbonExtension`** DLL | Hot-loaded shared library. |
| Deep engine/tooling integration with no gameplay API | HarmonyMod + reflection | Last resort; most fragile. |
| New art/client behaviour | **Not a server plugin** | Client assets cannot be streamed; see `Rust.MD` §10. |

### 1.1 Decision tree

```
Does a Carbon/Oxide hook exist?
├─ Yes → use it (compose hooks if no single hook matches)
└─ No
   ├─ Can a timer/state scan approximate it without cheating? → do that first
   ├─ Is the patch only needed while your plugin is loaded?
   │    └─ Yes → [AutoPatch] in the plugin
   └─ Must it work on vanilla Rust, Oxide, or after Carbon unload?
        └─ Yes → HarmonyMod DLL (IHarmonyModHooks + [HarmonyPatch])
```

### 1.2 What "fallback" does *not* mean

- It is not "rewrite everything in Harmony". Carbon hooks are faster to write, safer on reload and
  visible to other plugins; use them by default.
- It is not "ship a HarmonyMod to patch Carbon". Patch the game, not the host.
- It is not "unload Harmony at runtime". `harmony.unload` unp patches but cannot unload the CLR
  assembly; assume one load per process.

---

## 2. Project and repository setup

### 2.1 Recommended layout (one repo, multiple artifacts)

```
MyPlugin/
├── src/
│   ├── MyPlugin/                 # Carbon plugin (.cs files; deployed as source)
│   │   ├── MyPlugin.cs           # [Info], CarbonPlugin, lifecycle
│   │   ├── MyPlugin.Commands.cs
│   │   ├── MyPlugin.Hooks.cs
│   │   ├── MyPlugin.Config.cs
│   │   └── MyPlugin.UI.cs
│   ├── MyPlugin.Core/            # pure logic, netstandard2.0/2.1, no Rust refs
│   └── MyPlugin.Harmony/         # HarmonyMod DLL (only if needed)
│       ├── MyMod.cs              # IHarmonyModHooks
│       └── Patches/
├── tests/
│   └── MyPlugin.Core.Tests/      # net8.0, xUnit v3
├── deploy/
│   ├── carbon/plugins/           # symlink/copy target for dev server
│   └── HarmonyMods/
├── docs/
│   ├── CHANGELOG.md
│   └── CONFIG.md
├── .editorconfig
├── Directory.Build.props
└── MyPlugin.sln
```

- Keep **game logic** in `MyPlugin.Core`; it compiles and tests without Rust assemblies.
- Keep **Rust/Carbon adapters** thin in `MyPlugin`.
- Only add `MyPlugin.Harmony` when the fallback is actually required; it doubles your test matrix.
- Deploy `.cs` files (not the compiled plugin DLL) to Carbon. The DLL is only for HarmonyMod or an
  extension.

### 2.2 Build configuration

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <LangVersion>10</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>

  <PropertyGroup Condition="'$(RustManaged)' == ''">
    <RustManaged Condition="'$(OS)' == 'Windows_NT'">C:\rust-server\RustDedicated_Data\Managed</RustManaged>
    <RustManaged Condition="'$(OS)' != 'Windows_NT'">/srv/rust/RustDedicated_Data/Managed</RustManaged>
  </PropertyGroup>
</Project>
```

- `LangVersion=10` keeps you honest: Carbon may parse preview syntax, but the runtime is .NET
  Framework 4.8 and not every feature has its polyfill.
- `Nullable=enable` in your code; Carbon's compiler does not enforce it, but your IDE/tests will.
- Reference game assemblies with `Private=false`; never copy `Assembly-CSharp.dll` to output.

### 2.3 Local development loop

1. Run a local server with Carbon (`Carbon.QuickStart` bundle or manual Doorstop install).
2. Symlink/copy `src/MyPlugin/*.cs` into `carbon/plugins/MyPlugin/` (Carbon supports subfolders
   and reloads on change with `Watchers.ScriptWatchers`).
3. Attach a debugger (doorstop `[UnityMono] debug_enabled=true`, `debug_address=127.0.0.1:5337`)
   or use `c.reload MyPlugin` + logs.
4. For HarmonyMods, copy `MyPlugin.Harmony.dll` to `HarmonyMods/` and use
   `harmony.unload MyPlugin.Harmony` → replace → `harmony.load MyPlugin.Harmony`. Do not hot-swap.

### 2.4 CI

- Build `Core` + tests on Linux (`net8.0`) — fast.
- Build plugin/Harmony artifacts against a pinned `RustDedicated_Data/Managed` snapshot (downloaded
  via DepotDownloader, app 258550). Cache it; do not commit game DLLs.
- Run unit tests on every push; run a smoke test on a disposable server for release candidates.
- Produce a zip with `carbon/plugins/...`, `HarmonyMods/...`, `README`, `CHANGELOG`.

---

## 3. Coding standards that matter in-game

Full language rules: `Senior_C.MD`. In-game-specific rules:

### 3.1 Threading

- Hooks and timers run on the main thread. Never block it.
- No `.Result`, `.Wait()`, `Thread.Sleep`, synchronous HTTP/DB/file I/O in a hook.
- For background work, start a tracked task, cancel it in `Unload()`, and marshal results back with
  `NextFrame`/`NextTick`.
- Do not touch `BasePlayer`, `BaseEntity`, `Transform`, `GameObject`, CUI or `ServerMgr` from a
  worker thread.

### 3.2 Allocation

- No LINQ in per-tick/per-damage/per-entity hooks; use `for` and pre-sized lists.
- Reuse buffers; `Pool.Get<T>()`/`Pool.Free(ref x)` for temporary collections.
- Avoid string concatenation in loops; cache messages; use `StringPool` for prefab paths.
- Watch closures: a lambda capturing a player/entity per tick allocates and extends lifetimes.

### 3.3 Failure behaviour

- Hook and command exceptions are caught by Carbon and logged, but the feature silently stops.
  **Timer** exceptions are worse: Carbon catches them and **destroys the timer**, so a repeating task
  stops forever after one throw. **Harmony patch** exceptions are not caught at all without a
  finalizer.
- Every boundary you own goes through `Safe.Run`/`Safe.RunAsync`/`Safe.Unload`, with a
  `PluginGuard` as the last-chance net and a separate `ExceptionLog` (`Exceptions.md`).
- Decide **fail-open vs fail-closed** per feature:
  - cosmetic/UI/optional → fail open (log and continue),
  - economy/permissions/security → fail closed (deny and log),
  - startup/config → fail fast (disable the plugin rather than run on unknown settings).
- Never let a background task throw unobserved; wrap the whole loop and observe the task.
- Log once with context (boundary, plugin version, player/entity, fingerprint); never log per tick.

### 3.4 Determinism and reload

- Keep state in instance fields, not statics. If you need static caches, clear them in `Unload()`.
- Destroy every timer, CUI panel, subscription and Harmony patch you create.
- Cancel every task/CTS; close every socket/file handle.
- Assume `Unload()` can run at any time, including mid-operation.

### 3.5 Naming and files

- Plugin class name = file name = `[Info]` title (Carbon/Oxide convention).
- One concern per partial file for large plugins; `.cszip` for distribution.
- Prefix private fields `_camelCase`; constants `PascalCase`; no Hungarian notation.
- Public API (extension/service) uses interfaces and XML docs; plugin internals stay private.

---

## 4. Plugin skeleton and lifecycle

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Carbon;
using Carbon.Extensions;
using MyPlugin.Infrastructure;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;

namespace Carbon.Plugins;

[Info("MyPlugin", "YourName", "1.0.0")]
[Description("One-sentence purpose.")]
public class MyPlugin : CarbonPlugin
{
    #region Infrastructure

    private ExceptionLog _exceptions;
    private PluginGuard _guard;

    #endregion

    #region Configuration

    private Configuration _config;

    public class Configuration
    {
        [JsonProperty(PropertyName = "Version (DO NOT CHANGE)", Order = int.MaxValue)]
        public VersionNumber Version = new(1, 0, 0);

        [JsonProperty("Enabled")]
        public bool Enabled = true;
    }

    protected override void LoadDefaultConfig() => _config = new Configuration();

    protected override void SaveConfig() => Config.WriteObject(_config, true);

    protected override void LoadConfig()
    {
        base.LoadConfig();
        _config = Config.ReadObject<Configuration>();

        if (_config == null)
        {
            LoadDefaultConfig();
            SaveConfig();
            return;
        }

        if (_config.Version < new VersionNumber(1, 1, 0))
        {
            _config.Version = new VersionNumber(1, 1, 0);
            SaveConfig();
        }
    }

    #endregion

    #region Lifecycle

    private void Loaded()
    {
        // 1) Separate exception log first.
        _exceptions = new ExceptionLog(
            Title,
            Version.ToString(),
            Path.Combine(Defines.GetLogsFolder(), Title),
            environment: BuildEnvironment(),
            isMainThread: () => ThreadEx.IsOnMainThread());

        // 2) Last-chance guard before any feature code.
        _guard = new PluginGuard(Title, _exceptions);
        _guard.Install();

        // 3) Guarded initialization.
        Safe.Run("Loaded", () =>
        {
            permission.RegisterPermission("myplugin.use", this);
            permission.RegisterPermission("myplugin.admin", this);
            Puts($"v{Version} loaded; exceptions -> {_exceptions.Directory}");
        }, _exceptions, OnSafeError);
    }

    private void OnServerInitialized()
    {
        Safe.Run("OnServerInitialized", () =>
        {
            if (!_config.Enabled)
            {
                Puts("Disabled by config.");
                return;
            }

            // world-dependent setup: caches, entities, timers
        }, _exceptions, OnSafeError);
    }

    private void Unload()
    {
        // Every cleanup step runs even if one throws.
        Safe.Unload("Unload", _exceptions,
            () => _guard?.Dispose(),
            () => _exceptions?.Dispose());

        _guard = null;
        _exceptions = null;
    }

    private void OnSafeError(Exception ex)
        => LogError($"[Safe] {ex.GetType().Name}: {ex.Message}", ex);

    private static string BuildEnvironment()
    {
        try
        {
            return "carbon=" + (Community.Runtime?.Analytics?.Version?.ToString() ?? "unknown")
                 + ";unity=" + UnityEngine.Application.unityVersion;
        }
        catch
        {
            return "unknown";
        }
    }

    #endregion

    #region Commands

    [ChatCommand("myplugin")]
    [Permission("myplugin.use")]
    private void Command(BasePlayer player, string command, string[] args)
    {
        if (player == null) return;

        Safe.Run($"chat:{command}", () =>
        {
            player.ChatMessage("Hello.");
        }, _exceptions, ex =>
        {
            OnSafeError(ex);
            player.ChatMessage("An internal error occurred; the server log has details.");
        });
    }

    #endregion
}
```

Checklist for the skeleton:

- [ ] `[Info]` + `[Description]` present, class/file names match.
- [ ] Config has a version and a migration path.
- [ ] Permissions registered before first use.
- [ ] `Loaded` does cheap setup; `OnServerInitialized` does world setup.
- [ ] `Unload` releases everything (timers, UI, tasks, subscriptions, patches, sockets).
- [ ] No static mutable state (or explicitly cleared).
- [ ] `ExceptionLog` + `PluginGuard` installed in `Loaded`, disposed in `Unload` (`Exceptions.md`).
- [ ] Every hook/command/timer/task/patch body goes through `Safe.*`; cleanup uses `Safe.Unload`.
- [ ] Exception log path is per-plugin and writable; fault-injection test passes.

---

## 5. Hook selection and authoring

### 5.1 Find the right hook

1. Search the Carbon hook reference and Carbon source for the event you need.
2. If a hook exists, use it — even if the signature is slightly awkward.
3. If no hook exists, compose from several hooks (e.g. `OnEntityDeath` + `OnEntityKill` +
   `OnPlayerDisconnected`) before patching.
4. If the state is periodic and a 1–5 s delay is acceptable, use a timer scan; it is far safer than
   a patch.
5. Only then use `[AutoPatch]`, and only then a HarmonyMod.

### 5.2 Hook implementation rules

- Match the exact signature; a wrong argument type means the hook is never called.
- Null-check every argument; players/entities can die/disconnect mid-hook.
- Return the documented sentinel (`null` to continue, `false`/non-null to block/override where
  applicable). Do not return `true` from a "Can*" hook unless the contract says so.
- Keep hooks short: validate → enqueue/perform small work → return.
- Wrap the hook body in `Safe.Run` (or `Safe.Run<T>` with the documented fallback); Carbon's catch
  prevents a crash but does not preserve your feature's semantics.
- Log at most once per failure mode; never log per tick. The exception log dedupes by fingerprint.
- Never start a fire-and-forget task in a per-tick hook; if you must, use `Safe.FireAndForget`
  (observed) and track it for unload.

### 5.3 Hook performance gate

Before shipping, measure with `c.profile` or a stopwatch:

- p95 hook time under 1 ms for per-tick hooks,
- under 5 ms for damage/spawn hooks,
- no allocations in the steady state (check with the profiler's memory flag).

If you exceed the budget, optimize or move work to a timer/batch.

---

## 6. The Harmony fallback procedure

### 6.1 Step 1 — `[AutoPatch]` inside the plugin

Use when the patch is part of your plugin's feature and should disappear on unload/reload.

```csharp
using System;
using HarmonyLib;
using Oxide.Core.Plugins;

namespace Carbon.Plugins;

[Info("NoSuicide", "You", "1.0.0")]
public class NoSuicide : CarbonPlugin
{
    [AutoPatch(
        IsRequired = true,
        Order = AutoPatchAttribute.Orders.AfterOnServerInitialized,
        PatchSuccessCallback = nameof(OnPatchApplied))]
    [HarmonyPatch(typeof(BasePlayer), "CanSuicide", new Type[] { })]
    public class Patch_CanSuicide
    {
        public static bool Prefix(BasePlayer __instance, ref bool __result)
        {
            __result = false;
            return false;
        }
    }

    public void OnPatchApplied()
    {
        Puts("Patch applied.");
    }
}
```

- `IsRequired = true` unloads the plugin if patching fails (good for security/economy patches).
- `Order` controls when Carbon applies it; `AfterOnServerInitialized` is the safe default for game
  methods.
- The patch class is nested and instanced by Carbon; methods stay `static`.
- No manual unpatch needed.
- **Guard the patch body.** Harmony 2 does not catch prefix/postfix exceptions by default; Carbon's
  hook/command/timer containment does not apply here. Use try/catch inside every patch method, log
  via a static accessor to your `ExceptionLog`, and choose fail-open vs fail-closed explicitly. Add a
  `Finalizer(Exception __exception)` if you need an observation point (`Exceptions.md` §6.7).

### 6.2 Step 2 — HarmonyMod DLL

Use when the patch must exist without Carbon, on vanilla RustDedicated, or must be loaded before
Carbon's hooks.

**Contract**

```csharp
public interface IHarmonyModHooks
{
    void OnLoaded(OnHarmonyModLoadedArgs args);
    void OnUnloaded(OnHarmonyModUnloadedArgs args);
}
```

**Minimal mod**

```csharp
using HarmonyLib;

public sealed class MyMod : IHarmonyModHooks
{
    public void OnLoaded(OnHarmonyModLoadedArgs args)
    {
        UnityEngine.Debug.Log("MyMod loaded.");
    }

    public void OnUnloaded(OnHarmonyModUnloadedArgs args)
    {
        UnityEngine.Debug.Log("MyMod unloaded.");
    }
}

[HarmonyPatch(typeof(BasePlayer), "CanSuicide", new System.Type[] { })]
public static class NoSuicidePatch
{
    public static bool Prefix(ref bool __result)
    {
        __result = false;
        return false;
    }
}
```

**Project**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>10</LangVersion>
    <AssemblyName>MyMod</AssemblyName>
    <OutputType>Library</OutputType>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="0Harmony">
      <HintPath>$(RustManaged)/0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Assembly-CSharp">
      <HintPath>$(RustManaged)/Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Rust.Data">
      <HintPath>$(RustManaged)/Rust.Data.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

Deploy:

1. Stop the server.
2. Copy `MyMod.dll` (and only your own dependencies) to `HarmonyMods/`.
3. Start; check `harmony_log.txt` and the console for load errors.
4. On Carbon, `harmony.mods` lists it with hook/patch counts.
5. To update, stop → replace DLL → start (or `harmony.unload MyMod` → replace → `harmony.load MyMod`
   with the caveat that the old assembly stays in memory).

**Do not** ship `0Harmony.dll`; the server loads its own from `RustDedicated_Data/Managed`.

### 6.3 Step 3 — hybrid

- Put rules in `MyPlugin.Core`.
- Carbon plugin supplies commands/config/UI and calls Core.
- HarmonyMod supplies the missing patch and calls the same Core (or a shared static API/extension).
- Detect the host at runtime (`Type.GetType("Carbon.Community,Carbon.Common") != null`) so a single
  DLL can run under both.
- Use distinct Harmony IDs (`com.example.myplugin.carbon`, `com.example.myplugin.harmony`) and guard
  against double-patching with `Harmony.GetPatchInfo`.

### 6.4 Fallback acceptance checklist

- [ ] Patch target verified on the current Rust build (`AccessTools.Method` returns non-null).
- [ ] Patch kind is the least invasive (prefix/postfix before transpiler).
- [ ] Prefix/postfix wrapped in try/catch with explicit fail-open/fail-closed behaviour.
- [ ] A finalizer observes/suppresses deliberately, not by accident (`Exceptions.md` §6.7).
- [ ] Patch signature matches exactly (instance vs static, ref/out).
- [ ] Tested on vanilla **and** Carbon.
- [ ] Unload removes timers/handles; patches are removed by the loader.
- [ ] Mod-owned exception log exists under `HarmonyMods_Data/<Mod>/logs/`; guard installed.
- [ ] `harmony_log.txt` clean; no duplicate patch warnings; deliberate fault-injection test passed.
- [ ] Documented in README: why the fallback exists and which method it patches.

---

## 7. Configuration, data and secrets

### 7.1 Rules

- Version every config (`VersionNumber` last property).
- Migrate forward on load; never require hand-editing.
- Validate ranges/enums; clamp and log instead of crashing.
- Unknown JSON properties are ignored by Newtonsoft by default — do not rely on them for behaviour.
- Save atomically where it matters (temp file + replace) for data files.
- Secrets never go in config; use Carbon Vault (`{factory:id}`) and guard with `#if CARBON`.
- Do not log config dumps that may contain tokens.

### 7.2 Config migration pattern

```csharp
protected override void LoadConfig()
{
    base.LoadConfig();
    _config = Config.ReadObject<Configuration>();

    if (_config == null)
    {
        LoadDefaultConfig();
        SaveConfig();
        return;
    }

    var changed = false;

    if (_config.Version < new VersionNumber(1, 1, 0))
    {
        // 1.0 -> 1.1: rename "Cooldown" (seconds) to "CooldownMs" (milliseconds)
        _config.CooldownMs = _config.Cooldown * 1000;
        _config.Version = new VersionNumber(1, 1, 0);
        changed = true;
    }

    if (changed) SaveConfig();
}
```

### 7.3 Data files

```csharp
var data = Interface.Oxide.DataFileSystem;
const string key = "MyPlugin/players";

var model = data.ExistsDatafile(key)
    ? data.ReadObject<PlayerStore>(key)
    : new PlayerStore();

data.WriteObject(key, model);
```

- Path: `carbon/data/MyPlugin/players.json`.
- For high-frequency writes, batch and flush on a timer/shutdown, not every event.
- Keep a backup before migrations; write a `.bak` copy.
- For >10k records or multi-key queries, use SQLite/MySQL instead of JSON.

---

## 8. Commands and UI

### 8.1 Command checklist

- [ ] Correct attribute per surface: chat / console / RCon / universal / protected.
- [ ] Auth: `[Permission]`, `[Group]`, `[AuthLevel]`, `[Cooldown]` (stack them).
- [ ] Null player handled for console/RCon.
- [ ] Args parsed with `TryParse`; length/range validated.
- [ ] Reply on failure with usage, not a stack trace.
- [ ] Admin actions logged (actor, target, result).
- [ ] Destructive commands require confirmation or a second step.

### 8.2 UI checklist

- [ ] Unique panel names; destroy before re-add.
- [ ] Buttons use console commands or `chat.say /cmd`; no assumption that a chat command is directly
      callable from CUI.
- [ ] Server-side authorization on every button/input action.
- [ ] Panels destroyed on unload/disconnect; rebuild on reconnect.
- [ ] No secrets or admin-only data in CUI JSON.
- [ ] Throttle updates (2–10 Hz for progress bars); update existing elements instead of rebuilding.
- [ ] Player names/text sanitized for rich-text injection.

### 8.3 Accessibility and UX

- Show the command in help/`/help` output.
- Keep messages short; use consistent color/prefix.
- Provide a "close" button on cursor-enabled panels.
- Do not open UI automatically on join unless the player opts in.

---

## 9. Persistence choices

| Need | Use | Notes |
|---|---|---|
| Small per-plugin state | JSON via `DataFileSystem` | Simple, versioned, human-readable. |
| High-frequency updates | SQLite (WAL) or batched JSON | Batch writes; never per tick. |
| Relational/multi-table | MySQL/MariaDB (if available) | Parameterized SQL; connection pool. |
| Cross-server shared state | External DB/Redis | Requires a network service; latency/failure handling. |
| Secrets | Carbon Vault | Encrypted at rest; `#if CARBON`. |

Rules:

- One writer per file; serialize with a lock/queue.
- Use transactions for multi-row updates; WAL for concurrent readers.
- Parameterize queries; never concatenate identifiers or values.
- Back up before migrations; test migrations on a copy of production data.
- Version your schema and write forward-only migrations.

---

## 10. External services and networking

- **HTTP**: allow-list hosts, set timeouts, handle non-2xx, retry with jitter, never on the main
  thread. Store API keys in Vault.
- **WebSockets** (Bridge): password required; validate every message; treat it as untrusted input;
  add idempotency if delivery matters.
- **RCON/WebRCON**: admin-only; bind/firewall; do not embed the password in plugin config.
- **Queues/outbox**: if an action must not be lost (purchases, bans), write to a local outbox table
  first, then deliver with retries and an idempotent consumer.
- **Time**: use UTC everywhere; store timestamps as UTC; convert for display.

Failure design:

- Circuit-break repeated failures; do not hammer a dead endpoint every tick.
- Degrade to a cached/default value and log once.
- Never let an external outage crash the plugin or the server.

---

## 11. Error handling and exception logging

**Full standard and code:** `Exceptions.md`. This section is the process view.

### 11.1 Non-negotiables

- Every hook, command, timer, task, Harmony patch, Bridge handler, UI callback, `Load()` body and
  unload step is guarded.
- A **separate exception log** exists per plugin:
  `carbon/logs/<Plugin>/<Plugin>_exceptions-YYYY-MM-DD.log` (+ `.jsonl` for analysis).
  HarmonyMods use `HarmonyMods_Data/<Mod>/logs/`.
- The global guard (`AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`,
  `Application.logMessageReceivedThreaded`, `Carbon.Logger.OnErrorCallback`) is installed at load and
  removed at unload.
- The logger never throws; a failed log write must not break gameplay.
- No secrets/PII in log records; CR/LF sanitized; JSON escaped.

### 11.2 What Carbon already catches (and why you still guard)

| Boundary | Carbon catches? | Failure mode if you do nothing |
|---|---|---|
| Hook | Yes, logs, does not rethrow | That hook's work is skipped; a failed `Can*` may allow the action. |
| Command | Yes, logs | Player gets no feedback; action aborts silently. |
| Timer | Yes, **destroys the timer** | A repeating task stops forever after one throw. |
| Config | Yes | Plugin may run with defaults/null config. |
| Task | `UnobservedTaskException` | Result is lost; no context. |
| `Load()` virtual | **No** (direct call; only the constructor is wrapped) | Escapes to the loader's per-type catch, logged as `Failed to compile`; plugin stays half-loaded. |
| Harmony patch | **No** (unless a finalizer is present) | Exception enters the game method's caller. |

Carbon's containment is why crashes are rare and **silent feature loss is common**. The separate log
is how you see it.

### 11.3 Boundary pattern

```csharp
private object OnEntityDeath(BasePlayer player, HitInfo info)
{
    return Safe.Run("OnEntityDeath", () =>
    {
        if (player == null || info == null) return null;
        // work
        return null;
    }, fallback: null, _exceptions, OnSafeError);
}

private void OnSafeError(Exception ex)
{
    LogError($"[Safe] {ex.GetType().Name}: {ex.Message}", ex);
}
```

- Wrap the whole callback; do not wrap only the "risky" line.
- Choose fail-open vs fail-closed per feature (`Exceptions.md` §5).
- Per-item loops catch inside the loop so one bad player/entity does not abort the batch.
- `Unload` uses `Safe.Unload` so every cleanup step runs even if one throws.

### 11.4 Exception log review

Before release, read the log after fault injection, not just the console:

```bash
tail -n 40 carbon/logs/MyPlugin/MyPlugin_exceptions-$(date -u +%F).log
jq -c 'select(.event=="PLUGIN.TIMER")' carbon/logs/MyPlugin/MyPlugin_exceptions-$(date -u +%F).jsonl | tail
```

Every record must answer: what boundary, which plugin version, what input, what stack, how many
repeats, and whether it is safe to retry.

### 11.5 Acceptance

- [ ] Guard installed before feature init; disposed before the log.
- [ ] Fault injection passes for hook/command/timer/task/patch/thread.
- [ ] Read-only log directory: plugin continues, warning once.
- [ ] `PLUGIN.UNHANDLED` crash file created on a deliberate fatal test **on a disposable server**.
- [ ] No per-tick spam: fingerprint/dedupe/rate limit verified.
- [ ] Logs `chmod 750` directory / `chmod 640` files, owned by the server user.

---

## 12. Testing strategy

### 12.1 Unit tests (outside Rust)

- Target `net8.0`, xUnit v3, AwesomeAssertions.
- Test `MyPlugin.Core`: rules, pricing, cooldown math, parsers, migrations.
- Pure functions first; isolate time/IO behind interfaces.
- No `[SetUp]`/`[TearDown]`; constructor builds, `IDisposable` cleans.
- Assertions: one logical assertion per test; name tests `Method_State_Expected`.

```csharp
public sealed class CooldownTests
{
    [Fact]
    public void TryConsume_WithinWindow_ReturnsFalse()
    {
        var clock = new FakeClock(DateTime.UnixEpoch);
        var cooldown = new Cooldown(TimeSpan.FromSeconds(10), clock);

        Assert.True(cooldown.TryConsume("p1"));
        Assert.False(cooldown.TryConsume("p1"));

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(cooldown.TryConsume("p1"));
    }
}
```

### 12.2 In-game integration tests

- Disposable local server with Carbon.
- Scripted client or a second account for chat/UI/RPC paths.
- Test matrix: fresh start, reload, config migration, missing permission, malformed args, disconnect
  during operation, plugin unload while a timer/task is running.
- **Exception matrix:** throw from a wrapped hook, command, repeating timer, task, raw-thread guard,
  Harmony prefix and finalizer; verify the separate log and that the timer/task survives where it
  should. See `Exceptions.md` §12.
- **Log failure matrix:** read-only log directory, tiny size cap, concurrent writers from 100
  threads, unload during write. The plugin must continue.
- Test with 2+ players to catch "works for host only" bugs.

### 12.3 Smoke test script (release)

1. Start server, `c.plugins` → plugin loaded, version correct.
2. Run each command as player, moderator and owner; verify auth failures.
3. Open/close UI; click every button; reconnect and reopen.
4. Trigger the primary hook (damage/build/kill) and verify the effect.
5. Reload plugin; verify no duplicate UI/timers/patches.
6. Unload plugin; verify no errors, no leftover UI/entities.
7. Restart server; verify config/data persists.
8. Trigger the debug fault-injection command for hook/command/timer/task/patch; verify one record
   per fingerprint in `carbon/logs/<Plugin>/<Plugin>_exceptions-<date>.log` and that a repeating
   timer continues firing after a throw.
9. Check logs for exceptions/warnings and for unexpected `PLUGIN.*` records; review the `.jsonl`
   with `jq` if present.

---

## 13. Performance workflow

1. **Baseline.** Record population, tick rate, CPU and memory at a known load.
2. **Profile.** `c.profile` around the suspected window; read p95/p99, not just averages.
3. **Find the hot path.** Distinguish your hook time from the game method it wraps.
4. **Optimize one thing.** Remove allocations, narrow scans, cache lookups, batch work.
5. **Re-measure** at the same load; keep the change only if it moves the metric.
6. **Guard.** Add caps (entities per tick, messages per second, queue length).
7. **Document** the budget in the plugin README.

Common wins:

- index players by `userID` instead of scanning `activePlayerList`,
- cache prefab IDs and config values,
- replace LINQ with loops in hot hooks,
- update CUI fields instead of rebuilding,
- move I/O to a queue and flush on a timer,
- cap per-player UI/command rates.

---

## 14. Release, versioning and changelog

### 14.1 SemVer

- **Major**: breaking config/permission/API change or a wipe-required data change.
- **Minor**: new feature, backwards compatible.
- **Patch**: fix, no config change.

### 14.2 Release artifacts

```
dist/
├── MyPlugin-1.2.0.zip
│   ├── carbon/plugins/MyPlugin/          (or MyPlugin.cszip)
│   ├── HarmonyMods/MyMod.dll             (only if used)
│   ├── README.md
│   └── CHANGELOG.md
└── SHA256SUMS
```

- Include the exact Rust/Carbon versions tested.
- Include the HarmonyMod fallback notes (what it patches and why).
- Publish the checksum; advise admins to verify.

### 14.3 Changelog template

```markdown
## [1.2.0] - 2026-10-17
### Added
- ...
### Changed
- Config: `Cooldown` (seconds) → `CooldownMs`; migration is automatic.
### Fixed
- ...
### Compatibility
- Rust protocol ...
- Carbon build ...
- Requires HarmonyMod `MyMod.dll` (patches `BasePlayer.CanSuicide`).
```

---

## 15. Wipe-day and Rust-update runbook

Rust forces an update on the **first Thursday of each month**; Carbon publishes matching builds.

1. **Before:** freeze plugin changes 24 h ahead; back up configs/data; note current versions.
2. **Update:** stop server → update Rust (SteamCMD) → update Carbon to the matching branch/build.
3. **Boot once:** watch Carbon startup, hook install, HarmonyMod load, `harmony_log.txt`.
4. **Deploy:** plugin updates; check `c.plugins`, configs, permissions.
5. **Smoke:** join, command, UI, main hook, DB write, reload.
6. **Monitor:** first hour errors/performance; keep rollback artifacts.
7. **After:** record the new build/protocol in the compatibility matrix.

If something breaks:

- Check whether the game method you patch still exists/has the same signature.
- Check whether Carbon moved/renamed a hook or changed a return contract.
- Disable the feature via config first; roll back the plugin second; roll back Carbon last.

---

## 16. Incident response and rollback

### 16.1 Triage

1. Capture logs (`carbon/logs`, **the plugin exception log**, server console, `harmony_log.txt`).
   Start with the exception log's fingerprint count, then read the first full record.
2. Identify the first error, not the cascade.
3. Correlate with the last deploy/config change and with `Carbon.Core.log` timestamps.
4. Reproduce on a test server with the same build; use the fault-injection command to confirm the
   boundary still behaves as designed.

### 16.2 Rollback

- Plugin: restore the previous `.cs`/`.cszip` and `c.reload`.
- HarmonyMod: `harmony.unload <name>` → replace DLL → `harmony.load <name>`; if unstable, remove the
  DLL and restart.
- Config: restore the backup; restart or reload.
- Data: restore the backup only after stopping the server (never overwrite a live SQLite WAL).

### 16.3 Post-incident

- Add a regression test (unit or smoke).
- Add a guard/cap that would have limited the blast radius.
- Update the runbook/changelog.

---

## 17. Review checklists

### 17.1 Code review

- [ ] C# 10-safe; no unavailable BCL APIs.
- [ ] No blocking calls or main-thread I/O.
- [ ] All hooks null-check; sentinel returns correct.
- [ ] All timers/tasks/patches/UI destroyed in `Unload`.
- [ ] No static mutable state (or cleared).
- [ ] Config versioned + migration.
- [ ] Permissions checked server-side; no UI-only auth.
- [ ] Errors logged once, no per-tick spam; fingerprint/dedupe works.
- [ ] Every boundary guarded (`Safe.*`); `Unload` runs all cleanup steps.
- [ ] `ExceptionLog` + `PluginGuard` installed/disposed; separate file verified.
- [ ] Harmony patches have try/catch (+ finalizer where needed).
- [ ] No unguarded raw threads; no `async void` outside handlers.
- [ ] Tests cover core logic, migrations and exception paths.

### 17.2 Security review

- [ ] Every command/RPC validates caller, ownership, bounds, cooldown.
- [ ] No secrets in config/logs/CUI.
- [ ] SQL parameterized; identifiers allow-listed.
- [ ] External input size-limited and parsed safely.
- [ ] RCON restricted; Bridge password set.
- [ ] Third-party DLLs reviewed/pinned.

### 17.3 Performance review

- [ ] No LINQ/allocations in hot hooks.
- [ ] Entity scans bounded; indexes used.
- [ ] UI updates throttled/batched.
- [ ] I/O batched; DB transactions.
- [ ] Caps on entities/messages/queue lengths.
- [ ] Profiled before/after.

### 17.4 Release review

- [ ] Version bumped; changelog updated.
- [ ] Tested on vanilla (if HarmonyMod) and Carbon.
- [ ] Config migration tested on old data.
- [ ] Fault-injection matrix passed; exception log reviewed and attached to the release notes.
- [ ] Log rotation/retention and file permissions verified on Linux.
- [ ] Rollback artifact available.
- [ ] README documents permissions, commands, config, patches and the exception-log location.

---

## 18. Templates

> The exception infrastructure (`ExceptionLog`, `PluginGuard`, `Safe`) is in `Exceptions.md` §9.
> Keep those files in `src/MyPlugin/Infrastructure/` and include them in the `.cszip`.

### 18.1 `.editorconfig`

```ini
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
indent_style = space
indent_size = 4
trim_trailing_whitespace = true

[*.{cs,csx}]
dotnet_sort_system_directives_first = true
csharp_new_line_before_open_brace = all
csharp_style_var_for_built_in_types = false:suggestion
csharp_style_var_when_type_is_apparent = true:suggestion
csharp_style_expression_bodied_methods = when_on_single_line:suggestion
```

> There is no built-in compiler rule that forbids `async void`. Add a Roslyn analyzer (for example
> an `AsyncFixer`/`Meziantou`-style rule) or review for it manually; do not copy random rule IDs
> into `.editorconfig` without verifying they exist.

### 18.2 HarmonyMod entry template

```csharp
using System;
using HarmonyLib;

public sealed class MyMod : IHarmonyModHooks
{
    public void OnLoaded(OnHarmonyModLoadedArgs args)
    {
        Log("loaded");
    }

    public void OnUnloaded(OnHarmonyModUnloadedArgs args)
    {
        Log("unloaded");
    }

    private static void Log(string message)
        => UnityEngine.Debug.Log($"[MyMod] {message}");
}
```

### 18.3 Config template

```csharp
public class Configuration
{
    [JsonProperty(PropertyName = "Version (DO NOT CHANGE)", Order = int.MaxValue)]
    public VersionNumber Version = new(1, 0, 0);

    [JsonProperty("Enabled")]
    public bool Enabled = true;

    [JsonProperty("Debug")]
    public bool Debug = false;
}
```

---

## 19. Sources

Checked 2026-10-17.

- Carbon docs — creating your project, creating hooks, commands, permissions, configuration,
  extensions, Vault, conditionals, `.cszip`, profiler, modifiers, local server hosting.
  <https://carbonmod.gg/>
- Carbon source — `CarbonPlugin`, `Core.Commands.Engine`, `Components/Harmony`, `Switches`,
  `Carbon.Startup/src/Entrypoint.cs`, `Carbon.Compat/.../HarmonyEntrypoint.cs`.
  <https://github.com/CarbonCommunity/Carbon>
- Facepunch, *Meta Madness* (2024-05-02) — Harmony 2.3, recompile mods.
  <https://rust.facepunch.com/news/meta-madness>
- Facepunch, July 2022 update — `harmony.load`/`harmony.unload`.
  <https://rust.facepunch.com/news/july-2022-update>
- `IHarmonyModHooks` / `HarmonyLoader` (decompiled server code) — contract, loader order,
  `com.facepunch.rust_dedicated.<name>`, `harmony_log.txt`, `TryLoadMod`/`TryUnloadMod`.
- `Facepunch/Rust.HarmonyMods` — GatherManager `.csproj` (net4.6.2, references to
  `RustDedicated_Data/Managed`, DepotDownloader).
- OWASP ASVS/Top 10:2025 and cheat sheets (input validation, secrets, access control) — see
  `Senior_C.MD` §7.
- xUnit.net v3 docs; AwesomeAssertions — see `Senior_C.MD` §2.
- Carbon profiler (`c.profile`, `carbon/config.profiler.json`) and Rust server tick guidance
  (`server.tickrate` 30–60, read at startup).
- Rust "Built Different" (June 2026) — Unity 6, minimum Windows versions, monthly update cadence.
- Microsoft Learn, *Best practices for exceptions* / *AppDomain.UnhandledException* /
  *TaskScheduler.UnobservedTaskException* — containment, notification-only last chance, stack
  preservation. See `Exceptions.md` §14.
- OWASP, *Logging Cheat Sheet* — exclude secrets/PII, sanitize CR/LF, logging failure must not stop
  the app, retention/access control.
- Carbon source (`main`, 2026-10-17): `HookCallerInternal.cs`, `Oxide/Command.cs`,
  `Timer.Scheduler.cs`, `RustPlugin.cs`, `Logger.cs`, `FileLogger.cs` — what Carbon catches and where
  it logs.
- Harmony 2 finalizer docs / `MethodPatcher.cs` v2.3.6 — patches are not caught without a finalizer.
- Unity 6 `Application.logMessageReceivedThreaded` — thread-safe handler, no Unity APIs.

> **Recency note:** the process here is tool-agnostic and does not expire; the build/deploy specifics
> do. Re-verify the Carbon/Harmony versions and the patched method signatures on every Rust update.

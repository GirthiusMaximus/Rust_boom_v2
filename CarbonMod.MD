# CarbonMod + HarmonyMod Reference

> **Purpose.** Everything needed to design, build, deploy and debug a C# plugin on **Carbon** —
> plus the **HarmonyMod** path for cases where Carbon has no suitable hook.
>
> **Verified against:** Carbon `main` (source inspected 2026-10-17), Carbon docs (2025–2026),
> Rust `public`/`release` dedicated server, Harmony 2.3+ (verify the shipped `0Harmony.dll`; upstream 2.4.x exists).
>
> **Companion docs:** `Senior_C.MD` (language/engineering), `Rust.MD` (client/server internals),
> `CARBON_PLUGIN_PLAYBOOK.MD` (process), `Exceptions.md` (exception architecture + separate error
> log), `DontJustRollWithIt.MD` (evidence/collaboration protocol), `RESEARCH_REVIEW.MD` (source
> recency audit).

---

## Table of contents

1. [What Carbon is](#1-what-carbon-is)
2. [Installation and directory layout](#2-installation-and-directory-layout)
3. [Plugin compilation and lifecycle](#3-plugin-compilation-and-lifecycle)
4. [Hooks](#4-hooks)
5. [When Carbon has no hook: patching and Harmony](#5-when-carbon-has-no-hook-patching-and-harmony)
6. [Commands](#6-commands)
7. [Permissions](#7-permissions)
8. [Config, data, localization and secrets](#8-config-data-localization-and-secrets)
9. [Timers, async and shutdown](#9-timers-async-and-shutdown)
10. [UI: CUI, LUI and ClientEntity](#10-ui-cui-lui-and-cliententity)
11. [Bridge (server ↔ server)](#11-bridge-server--server)
12. [Extensions and modules](#12-extensions-and-modules)
13. [HarmonyMod in depth](#13-harmonymod-in-depth)
14. [Hybrid Carbon + HarmonyMod patterns](#14-hybrid-carbon--harmonymod-patterns)
15. [Performance and profiling](#15-performance-and-profiling)
16. [Security model](#16-security-model)
17. [Compatibility, versioning and updates](#17-compatibility-versioning-and-updates)
18. [Quick reference](#18-quick-reference)
19. [Error handling and logging](#19-error-handling-and-logging)
20. [Sources](#20-sources)

---

## 1. What Carbon is

Carbon is a **Harmony-based, in-memory mod loader** for Rust dedicated servers. It is a drop-in
alternative to Oxide/uMod and runs most Oxide plugins unmodified.

Key facts:

- Carbon patches Rust assemblies **in memory** at boot (`Carbon.Startup` / publicizer); it does not
  rewrite files on disk unless Developer Mode is enabled.
- Plugins are `.cs` source files compiled at runtime by Roslyn, or `.cszip` packages of partial
  classes.
- Carbon targets **net48** (Unity Mono). Plugins are compiled against Rust's managed assemblies.
- Carbon advertises **C# 10 + Harmony 2.0** for plugins. In current source the script compiler uses
  Roslyn `LanguageVersion.Preview`; the *runtime API surface* is still .NET Framework 4.8. Write
  C# 10-safe code unless you have verified a newer feature in-game (see `RESEARCH_REVIEW.MD`).
- It implements Oxide's plugin API, permissions, CUI, configs, data files, timers and `Lang`, so
  `Oxide.Plugins.RustPlugin` plugins usually work.
- It has native additions: `CarbonPlugin`, `CUI`/`LUI`, `ClientEntity`, `Bridge`, `Vault`,
  conditions, `.cszip`, modules and a Mono profiler.

### 1.1 Components (from Carbon's own layout)

| Component | Responsibility |
|---|---|
| `Carbon.Preloader` | Loads dependencies, self-updates, invokes `Carbon.Startup`. |
| `Carbon.Startup` | In-memory Rust assembly patching/publicizing, path moves/renames. |
| `Carbon.Bootstrap` | Primary AppDomain boot, loaders, watchers, extension/module managers. |
| `Carbon.Common` | Framework core: plugins, hooks, CUI, components, Oxide compatibility. |
| `Carbon.SDK` | Contracts/interfaces with no implementation. |
| `Carbon.Modules` | Built-in modules (admin UI, stack manager, etc.). |
| `Carbon.Compat` | Oxide and HarmonyMod conversion (Harmony 1 → 2, entrypoints, IL rewrites). |
| `Carbon.Test` | Automated test hooks/events. |
| `Carbon.Profiler` | Optional Mono profiler, also shipped as a standalone HarmonyMod. |

### 1.2 What it is *not*

- Not a client mod. The shipped Rust client is IL2CPP + EAC; server plugins cannot change client
  code or stream new assets. See `Rust.MD`.
- Not a sandbox. Plugins run in-process with full trust. Review third-party plugins.
- Not a substitute for Harmony knowledge when a hook does not exist.

---

## 2. Installation and directory layout

### 2.1 Boot model

Carbon uses **Unity Doorstop** to inject `Carbon.Preloader.dll` before Unity starts. The server is
started normally (`RustDedicated` / `RustDedicated.exe`); `doorstop_config.ini` points at Carbon.
On boot:

1. `Carbon.Preloader` ensures dependencies and updates.
2. `Carbon.Startup` patches/publicizes Rust assemblies and performs moves/renames.
3. `Carbon.Bootstrap` loads Carbon assemblies, extensions, modules and Harmony mods.
4. `Carbon.Common` starts the plugin loader, hooks and console commands.

### 2.2 Directory tree (typical)

```
<rust-root>/
├── RustDedicated.exe
├── doorstop_config.ini
├── HarmonyMods/                     # Rust's built-in Harmony mod DLLs
├── carbon/
│   ├── config.json                  # Carbon framework config
│   ├── config.profiler.json         # profiler config (optional)
│   ├── plugins/                     # .cs and .cszip plugins
│   │   └── cszip_dev/               # dev-only unpacked .cszip folders
│   ├── configs/                     # <PluginName>.json
│   ├── data/                        # plugin data files
│   ├── lang/                        # localization files
│   ├── extensions/                  # compiled ICarbonExtension DLLs
│   ├── managed/
│   │   └── modules/                 # Carbon module DLLs
│   ├── harmony/                     # dropped here, moved to HarmonyMods at boot
│   ├── lib/                         # Carbon dependencies (0Harmony.dll, etc.)
│   ├── logs/                        # Carbon logs
│   ├── developer/                   # patched assemblies when DeveloperMode=true
│   └── modifiers/                   # JSON modifiers
├── server/<identity>/               # Rust identity data
│   ├── carbon.perms.db              # opt-in SQLite permissions
│   └── carbon.vault                 # default vault location
└── HarmonyMods_Data/                # convention for mod data (not created by loader)
```

> Exact folder names come from Carbon `Defines` (`GetConfigsFolder`, `GetScriptsFolder`,
> `GetExtensionsFolder`, `GetManagedModulesFolder`, …). Use `-carbon.*` switches to override paths
> when hosting multiple servers from one image.

### 2.3 Useful command-line switches

| Switch | Effect |
|---|---|
| `-carbon.rootdir` | Overrides Carbon root. |
| `-carbon.carbonconfigdir` | Carbon's own config folder (`config.json`, `config.profiler.json`). |
| `-carbon.configdir` | Plugin configs (`carbon/configs`). |
| `-carbon.scriptdir` | Plugin scripts (`carbon/plugins`). |
| `-carbon.datadir`, `-carbon.langdir` | Data / localization folders. |
| `-carbon.extdir`, `-carbon.moduledir`, `-carbon.logdir` | Extensions / modules / logs folders. |
| `-carbon.modifierdir` | `carbon/modifiers`. |
| `-carbon.profiledir` | Profiler output folder. |
| `-carbon.sqlpermsdb` | SQLite permissions DB path. |
| `-harmonydir` | Overrides **Rust's** `HarmonyMods` folder. |
| `+carbon.onboot`, `+carbon.onserverinit` | Commands executed at boot / after server init. |
| `-bridge.password` | **Required** to start Carbon.Bridge. Optional `-bridge.ip`, `-bridge-port`. |

### 2.4 `carbon/config.json` (defaults)

```json
{
  "DeveloperMode": false,
  "IsModded": true,
  "Prefixes": [
    { "Value": "/", "PrintToChat": false, "PrintToConsole": false, "SuggestionAuthLevel": 2 }
  ],
  "Aliases": { "carbon": "c.version" },
  "Rcon": true,
  "Language": "en",
  "WebRequestIp": null,
  "Watchers": {
    "ScriptWatchers": true,
    "ZipScriptWatchers": true,
    "ScriptWatcherOption": 0,
    "ModuleWatchers": true,
    "ExtensionWatchers": true
  },
  "Permissions": {
    "PlayerDefaultGroup": "default",
    "AdminDefaultGroup": "admin",
    "ModeratorDefaultGroup": "moderator",
    "AutoGrantPlayerGroup": true,
    "AutoGrantAdminGroup": true,
    "AutoGrantModeratorGroup": true,
    "BypassAdminCooldowns": false,
    "PermissionSerialization": 0
  },
  "Analytics": { "Enabled": true },
  "SelfUpdating": { "Enabled": true, "HookUpdates": true },
  "Debugging": { "ScriptDebuggingOrigin": "", "HookLagSpikeThreshold": 1000 },
  "Processors": { "ScriptProcessingRate": 0.2, "ZipScriptProcessingRate": 0.5 },
  "Publicizer": {
    "PublicizedAssemblies": [
      "Assembly-CSharp.dll", "Facepunch.Console.dll", "Facepunch.Network.dll",
      "Facepunch.Nexus.dll", "Facepunch.Ping.dll", "Facepunch.Unity.dll",
      "Rust.Clans.Local.dll", "Rust.FileSystem.dll", "Rust.Harmony.dll",
      "Rust.Global.dll", "Rust.Data.dll"
    ],
    "PublicizerMemberIgnores": ["^HiddenValueBase$", "^HiddenValue`1$", "^Pool$"]
  },
  "Logging": {
    "LogSplitSize": 2.5,
    "LogSeverity": 2,
    "LogFileMode": 2,
    "LogVerbosity": 0,
    "CommandSuggestions": true
  },
  "Profiler": { "RecordingWarnings": true },
  "Compiler": {
    "UnloadOnFailure": false,
    "ConditionalCompilationSymbols": ["CARBON", "RUST", "OXIDE_PUBLICIZED"]
  },
  "Misc": { "ShowConsoleInfo": true }
}
```

- `DeveloperMode` writes patched/publicized assemblies to `carbon/developer/patched_assemblies`
  (handy for decompiling with ILSpy/dnSpy).
- `Prefixes` are per-auth chat prefixes; `Aliases` map console/RCon commands (works for native Rust
  commands too, e.g. `o.reload` → `c.reload`).
- `IsModded` puts the server on the Modded browser tab; `Rcon` controls the RCON listener.
- `Watchers` drive hot reload; disable script watchers if your editor writes partial files.
- `Publicizer.PublicizedAssemblies` is mandatory; do not remove entries.
- `Compiler.ConditionalCompilationSymbols` defines `CARBON`, `RUST`, `OXIDE_PUBLICIZED` for plugins.

### 2.5 Installation checklist

- [ ] Stop the server; remove Oxide/uMod DLLs (`Oxide.*`) from `RustDedicated_Data/Managed`.
- [ ] Install the Carbon build matching the Rust branch (production/main/experimental/staging/aux).
- [ ] Verify `doorstop_config.ini` points at `carbon/managed/Carbon.Preloader.dll`.
- [ ] Start server; run `c.version`, `c.plugins`, `c.find c.`.
- [ ] Put plugins in `carbon/plugins`; configs appear in `carbon/configs`.
- [ ] Keep EAC on; Carbon is server-side.
- [ ] After each Rust update, reinstall/update Carbon before debugging plugins.

---

## 3. Plugin compilation and lifecycle

### 3.1 Minimum plugin

```csharp
using System;
using Carbon.Plugins;

namespace Carbon.Plugins;

[Info("MyPlugin", "YourName", "1.0.0")]
[Description("Does one useful thing.")]
public class MyPlugin : CarbonPlugin
{
    private void Loaded()
    {
        Puts("MyPlugin loaded.");
    }

    private void OnServerInitialized()
    {
        Puts($"Server initialized; {BasePlayer.activePlayerList.Count} players online.");
    }

    private void Unload()
    {
        Puts("MyPlugin unloaded.");
    }
}
```

- Namespace `Carbon.Plugins` + `CarbonPlugin` is the native path.
- Namespace `Oxide.Plugins` + `RustPlugin` is the Oxide-compatible path. It also runs on Carbon.
- Class name should match the plugin name; `[Info]` title is what admins see.
- The file must be a single top-level plugin class (partial classes allowed for `.cszip`).
- Carbon compiles the file in memory; syntax errors appear in the console and plugin list shows a
  failure. There is no separate DLL to deploy for `.cs` plugins.

### 3.2 Lifecycle order (practical)

| Stage | Method | Notes |
|---|---|---|
| Compile | (Roslyn) | On load/reload; errors keep the previous version loaded if hot reload. |
| Construct | ctor | Keep trivial; no Unity/game access assumptions. |
| Setup | `Setup(...)` | Carbon/Oxide initializes libraries, config, permission, timers. |
| Load | `Loaded()` / `Init()` | Good for config load, permission registration, timer setup. |
| Server ready | `OnServerInitialized()` | World/entities available; register commands if not attribute-based. |
| Runtime | hooks | Main thread; keep short. |
| Reload/unload | `Unload()` | Destroy timers, CUI, subscriptions, patches, save data. |
| Shutdown | `OnServerShutdown()` / `OnAsyncServerShutdown()` | Last chance to flush; bounded. |

`Loaded()` is called after the plugin object and its libraries exist but before the world is
necessarily ready. Use `OnServerInitialized()` for anything touching players/entities.

**Verified call order** (`ModLoader.InitializePlugin` → `Plugin.IInit`/`Plugin.ILoad`): constructor →
`ILoadConfig()` → `ILoadDefaultMessages()` → `Init` hook → `OnLoaded` hook → `Loaded` hook → the
`Load()` virtual. Two consequences:

- `Init`/`Loaded`/`Unload`/`OnServerInitialized` are `CallHook` invocations, so they pass through
  Carbon's guarded hook caller.
- `Load()` is a **direct virtual call**, not a hook. If it throws, only `ScriptLoader`'s per-type catch
  sees it (logged misleadingly as `Failed to compile '<file>'`) and the plugin stays half-loaded.
  Keep `Load()` trivial or wrap its whole body; do heavy setup in `Loaded()`/`OnServerInitialized()`.

### 3.3 Hot reload

- `c.reload <PluginName>` recompiles and reloads a `.cs` plugin.
- File watchers reload on change by default (`Watchers.ScriptWatchers`).
- Static state is a common reload bug: depending on load context it can persist, duplicate, or hold
  the old assembly alive. Prefer instance state; if you must use statics, reset them in `Unload()`.
- Harmony patches applied by the old assembly may persist if not unpatched; use `[AutoPatch]` so
  Carbon manages them, and always `UnpatchAll` manual patches in `Unload()`.
- Timers, CUI panels, event subscriptions and background tasks must be destroyed/cancelled.

### 3.4 `.cszip` packages (large plugins)

- Split a plugin into partial classes with the same namespace and class name.
- Primary file declares `[Info]` and `: CarbonPlugin`.
- Zip the `.cs` files and rename to `.cszip`; drop in `carbon/plugins`.
- For development, create a folder with the same name under `carbon/plugins/cszip_dev/` and edit the
  `.cs` files there; Carbon hot-loads changes. Do not ship the dev folder.

```
MyPlugin.cszip
 ├── MyPlugin.Main.cs
 ├── MyPlugin.Commands.cs
 ├── MyPlugin.Hooks.cs
 └── MyPlugin.UI.cs
```

### 3.5 Conditional compilation

Carbon injects symbols (configurable in `Compiler.ConditionalCompilationSymbols`):

```csharp
private void OnServerInitialized()
{
#if CARBON
    Puts("Running on Carbon.");
#else
    Puts("Running on Oxide or another host.");
#endif

#if WIN
    Puts("Windows server.");
#elif UNIX
    Puts("Linux server.");
#endif

#if STAGING
    Puts("Rust staging branch.");
#elif AUX01
    Puts("Rust aux01 branch.");
#elif AUX02
    Puts("Rust aux02 branch.");
#endif

#if RUST_ABV_2511_243_1
    Puts("Rust protocol above 2511.243.1.");
#elif RUST_IS_2511_243_1
    Puts("Exact protocol.");
#endif
}
```

Also available: `RUST_BLW_*`, `CARBON_ABV_*`, `CARBON_IS_*`, `OXIDE_PUBLICIZED`.

Use these to keep one source compatible across Carbon/Oxide and Rust updates. The preprocessor is
compile-time; for a single DLL that must detect the host at runtime, use reflection:
`Type.GetType("Carbon.Community,Carbon.Common") != null`.

### 3.6 Plugin project for IDE development

Carbon plugins are compiled in-game, but you want IntelliSense and unit tests:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>10</LangVersion>
    <Nullable>enable</Nullable>
    <AssemblyName>MyPlugin</AssemblyName>
    <RootNamespace>MyPlugin</RootNamespace>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="Assembly-CSharp">
      <HintPath>$(RustManaged)/Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>$(RustManaged)/0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <!-- Facepunch.*, Rust.*, UnityEngine.* as needed -->
  </ItemGroup>

  <ItemGroup>
    <Compile Remove="tests/**" />
  </ItemGroup>
</Project>
```

- Set `RustManaged` to a local server's `RustDedicated_Data/Managed`.
- Keep `Private=false` so server assemblies are not copied into your output.
- The actual deployment artifact is still the `.cs` file (or `.cszip`), not the DLL — unless you are
  building a HarmonyMod or extension.
- For pure logic, add a separate `net8.0` test project referencing the same source files.

---

## 4. Hooks

### 4.1 How Carbon hooks work

Carbon's hooks are **Harmony patches generated/loaded at runtime**, keyed by numeric hook IDs derived
from the hook name as the first 4 bytes of its MD5 (little-endian). For example `CanUseUI` is
`1307002116` and `OnDestroyUI` is `503981600`, the same constants `CuiHelper` uses. When a plugin
defines a method named `OnEntityTakeDamage`, Carbon connects the patched game method to that plugin
method.

Important consequences:

- Only hooks that at least one plugin uses are patched (performance).
- Hook signatures are validated; a wrong parameter type means your method is not called (or the
  plugin fails to load depending on the hook).
- Hooks run on the **main thread** and must return quickly.
- Return values matter. Many hooks use `null` = "continue/default"; `false` or a non-null object can
  block or override. Always check the hook reference for the exact contract.

Reference: <https://carbonmod.gg/references/hooks/>

### 4.2 Hook signature examples

```csharp
private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
{
    if (entity == null || info == null) return null;

    if (entity is BasePlayer player && _godMode.Contains(player.userID))
    {
        info.damageTypes.ScaleAll(0f);
        return true; // handled; block default damage
    }

    return null; // let Rust/Carbon continue
}

private void OnPlayerConnected(BasePlayer player)
{
    Puts($"{player.displayName} ({player.userID}) connected.");
}

private void OnEntityBuilt(Planner plan, UnityEngine.GameObject go)
{
    var entity = go.GetComponent<BaseEntity>();
    if (entity != null) Puts($"Built {entity.ShortPrefabName}");
}
```

- Prefer `object` return for hooks that can be overridden/blocked; return `null` to continue.
- For `void` hooks, never throw; log and return.
- Null-check every framework object; players disconnect mid-hook.

### 4.3 Custom hooks (plugin-to-plugin)

Carbon lets a plugin expose hooks that other plugins can call. Native hook definitions use
`[HookAttribute.Patch]` + `HookCaller.CallStaticHook(hookId, args)`:

```csharp
using API.Hooks;
using Carbon;

[HookAttribute.Patch(
    "OnMyCustomEvent",            // hook name plugins subscribe to
    "OnMyCustomEvent [main]",     // display name used by the hook reference
    typeof(MyPatchTarget),        // game type being patched
    nameof(MyPatchTarget.Method), // method that fires the hook
    new[] { typeof(BasePlayer) })] // argument types, in order
public class MyCustomHookPatch
{
    public static bool Prefix(BasePlayer player, ref bool __result)
    {
        // hook id: generated from the hook name (never invent one)
        if (HookCaller.CallStaticHook(MyCustomHookPatch.HookId, player) is bool result)
        {
            __result = result;
            return false;
        }

        __result = default;
        return true;
    }

    // MD5("OnMyCustomEvent") first 4 bytes, little-endian — matches Carbon's hook id.
    public const uint HookId = 3478436080;
}
```

The numeric id is a stable hash of the hook name; take it from Carbon's hook generator (or from
an existing hook definition of the same name) rather than choosing your own.

- Hook IDs are generated from the hook name; the docs site has a generator.
- `Interface.CallHook("Name", ...)` is the Oxide path and still works, but Carbon's native path is
  faster because it avoids string dispatch.
- For plugin-only hooks, Carbon also supports a simpler pattern: define a method and call it from
  another plugin via `plugins.Find("Other").Call("HookName", args)` (Oxide compat). Prefer an
  explicit C# interface/extension when both plugins are yours.

### 4.4 Calling hooks from your plugin

If another plugin defines a hook, call it through the plugin manager rather than reflection:

```csharp
var other = plugins.Find("OtherPlugin");
other?.Call("OnMyCustomEvent", player);
```

Be defensive: the other plugin may be absent or may throw. Isolate calls in try/catch at plugin
boundaries and log.

### 4.5 Hook performance rules

- Do nothing expensive in per-tick/per-damage hooks; early-return on irrelevant entities.
- Cache `BasePlayer` lookups; avoid `BasePlayer.FindByID` in hot paths (it scans).
- Avoid LINQ, string formatting and allocations per call.
- Use flags/collections keyed by `userID`/`net.ID`; clean up on disconnect/death.
- If a hook fires very often, consider a Harmony prefix that early-outs before calling Carbon, or a
  different event with less traffic.

### 4.6 Hook exceptions (what Carbon actually does)

Carbon wraps hook invocation in `HookCallerInternal.DoCall`:

```csharp
try
{
    result2 = hook.Method.Invoke(hookable, args);
}
catch (Exception ex)
{
    var exception = ex.InnerException ?? ex; // unwrap TargetInvocationException
    Logger.Error($"Failed to call hook '{readableHook}' on plugin '{hookable.Name} v{hookable.Version}'", exception);
}
```

Consequences:

- The server and plugin **do not crash**; the hook's work is skipped for that call.
- The hook returns `null`/default; for a `Can*` hook this can **allow** the action. Do not rely on the
  host to enforce your rule.
- The error goes to `Carbon.Core.log`, not to a per-plugin file. Mirror it with
  `Carbon.Logger.OnErrorCallback` or guard the hook yourself (`Exceptions.md`).
- Carbon does not rethrow, so a failure is invisible unless someone reads the log.

Wrap the body and choose the fallback deliberately:

```csharp
private object OnEntityDeath(BasePlayer player, HitInfo info)
{
    return Safe.Run("OnEntityDeath", () =>
    {
        if (player == null || info == null) return null;
        // ...
        return null;
    }, fallback: null, _exceptions, OnSafeError);
}
```

---

## 5. When Carbon has no hook: patching and Harmony

This is the core fallback decision. Work down the list:

1. **Use an existing Carbon/Oxide hook.** Search the hook reference and Carbon source before
   patching. Many "missing" events are derivable by composing hooks.
2. **Use a periodic/timer check** if the condition is state-based and a small delay is acceptable
   (e.g. scan every 1–5 s instead of patching a rare method).
3. **Patch inside the plugin with `[AutoPatch]`.** Best for plugin-specific behaviour; Carbon manages
   apply/unapply and reload.
4. **Ship a HarmonyMod DLL** when the patch must exist without Carbon, must run under vanilla/ Oxide,
   or must survive Carbon being unloaded. This is the true fallback.

### 5.1 `[AutoPatch]` inside a Carbon plugin

```csharp
using System;
using HarmonyLib;
using Oxide.Core.Plugins;
using Carbon.Plugins;

namespace Carbon.Plugins;

[Info("NoSuicide", "You", "1.0.0")]
public class NoSuicide : CarbonPlugin
{
    [AutoPatch]
    [HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.CanSuicide))]
    public static class Patch_CanSuicide
    {
        public static bool Prefix(BasePlayer __instance, ref bool __result)
        {
            __result = false;
            return false; // do not run original
        }
    }
}
```

Options:

```csharp
[AutoPatch(
    IsRequired = true,                                        // unload plugin if patch fails
    Order = AutoPatchAttribute.Orders.AfterOnServerInitialized,
    PatchSuccessCallback = nameof(OnPatchComplete))]
[HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.CanSuicide))]
public static class Patch_CanSuicide { /* ... */ }
```

Rules:
- Patch **methods** must be `static`; the containing class may be a nested `public class` or `static`
  class, and must carry at least one `[HarmonyPatch]` attribute for auto-discovery.
- `Prefix` returning `false` skips the original; set `__result` if the caller uses it.
- `Postfix` can read/modify `__result` via `ref`.
- `Transpiler` edits IL; fragile across Rust updates. Keep it tiny and log the original method hash.
- Do not patch the same method from two plugins with conflicting semantics; document your patch.
- `[AutoPatch]` handles unpatching on unload/reload. Manual `new Harmony(id).PatchAll()` requires
  `UnpatchAll(id)` in `Unload()`.

### 5.2 Harmony basics you must know

- Harmony 2 namespace is **`HarmonyLib`** (Harmony 1 used `Harmony`). Rust upgraded to Harmony 2.3 in
  May 2024; old mods must be recompiled.
- Patch methods are **static**; instance state must be static or passed via `__instance`/`__state`.
- Injections: `__instance`, `__result`, `__resultRef`, `__state`, `___field`, `__args`,
  `__originalMethod`, `__runOriginal`.
- Priorities: `[HarmonyPriority]`, `[HarmonyBefore]`, `[HarmonyAfter]`; default 400.
- A prefix can return `bool` (false = skip original) or `void`. A postfix can return `void` or a
  replacement result (for some signatures).
- Exceptions inside a patch can break the game method; wrap risky logic in try/catch and fail safe.
- Patching a generic method or an inlined method is unreliable. Test on the real build.
- Harmony patches are per-AppDomain and in-memory; they vanish on restart, and hot-swapping a DLL
  that is already loaded is not supported (invalid IL/crash risk).

### 5.3 Which patch type?

| Need | Use | Notes |
|---|---|---|
| Block an action | Prefix returning `false` | Set `__result` to the caller's expected "denied" value. |
| Change arguments | Prefix with same-named args / `__args` | Mutate before original. |
| Observe after | Postfix | Read `__result`; do not assume success. |
| Replace result | Prefix `ref __result` + return false, or Postfix `ref __result` | Match the original return type. |
| Add/remove IL | Transpiler | Most fragile; use `CodeMatcher` and log. |
| Catch exceptions | Finalizer | Runs even if original throws; can swallow/replace. |
| Patch many methods | `[HarmonyTargetMethods]` | Return `IEnumerable<MethodBase>`; validate each. |

### 5.4 Failure-safe patching

```csharp
[HarmonyPatch(typeof(SomeGameType), nameof(SomeGameType.SomeMethod))]
public static class SafePatch
{
    public static bool Prefix(SomeGameType __instance)
    {
        try
        {
            // your logic
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("SafePatch failed; continuing vanilla.", ex);
            return true; // fail open so the game keeps working
        }
    }
}
```

Fail-open is right for cosmetic/optional features. Fail-closed is right for security/economy checks
(e.g. deny the action if your validation throws). Decide per patch and document it.

### 5.5 Version-proofing patches

- Patch by `nameof` where possible; strings break silently on rename.
- Log the resolved `MethodInfo` (declaring type, signature, module version) at load.
- Add a startup self-test: call a harmless path or inspect `Harmony.GetPatchInfo` to confirm the
  patch applied.
- Keep transpilers minimal; prefer prefix/postfix.
- Maintain a compatibility matrix per Rust protocol and re-test monthly (Rust updates first Thursday).

---

## 6. Commands

### 6.1 Command types

| Attribute | Chat | F1 console | Server console/RCon |
|---|---|---|---|
| `[ChatCommand("x")]` | ✅ | ❌ | ❌ |
| `[ConsoleCommand("x")]` | ❌ | ✅ | ✅ |
| `[RConCommand("x")]` | ❌ | ❌ | ✅ |
| `[Command("x")]` | ✅ | ✅ | ✅ |
| `[ProtectedCommand("x")]` | ❌ | ⚠️ | ⚠️ |

Signatures:

```csharp
[ChatCommand("kit")]
private void KitCommand(BasePlayer player, string command, string[] args)
{
    if (player == null) return;
    player.ChatMessage($"args: {string.Join(", ", args)}");
}

[ConsoleCommand("myplugin.status")]
private void StatusCommand(ConsoleSystem.Arg arg)
{
    arg.ReplyWith($"online: {BasePlayer.activePlayerList.Count}");
}

[RConCommand("myplugin.reloaddata")]
private void ReloadData(ConsoleSystem.Arg arg) { /* ... */ }

[Command("myplugin.universal")]
private void Universal(BasePlayer player, string command, string[] args)
{
    if (player != null)
    {
        player.ChatMessage("hello");
        return;
    }

    Puts("hello (server console)");
}
```

- Always null-check `BasePlayer` in `[Command]`; it is null when invoked from server console/RCon.
- `ConsoleSystem.Arg` gives `arg.GetString(0)`, `arg.GetInt(1)`, `arg.HasArgs(n)`, `arg.ReplyWith`.
- Command names are case-insensitive; attribute names should be lowercase/namespaced.
- Chat commands are invoked with the configured prefix (`/` by default).

### 6.2 Authorization attributes

```csharp
[ChatCommand("wipe")]
[Permission("myplugin.wipe")]
[AuthLevel(2)]              // 0 player, 1 moderator, 2 owner, 3 developer
[Cooldown(60_000)]          // ms per player
private void WipeCommand(BasePlayer player, string command, string[] args) { /* ... */ }
```

- `[Permission]`, `[Group]` and `[AuthLevel]` can be combined; all must pass.
- `[Cooldown]` is per player and relative; it does not protect server-console calls.
- Use `[ProtectedCommand]` for commands that should only be callable from Carbon-generated UI:

```csharp
[ProtectedCommand("myplugin.buy")]
private void Buy(ConsoleSystem.Arg arg)
{
    var player = arg.Player();
    if (player == null) return;
    // ...
}
```

`Community.Protect("myplugin.buy")` returns a randomized command name
(`carbonprotecc_<random>`) that the UI can use; players cannot guess/call the original. This is not a
substitute for server-side validation — still check permission, balance and item ownership.

### 6.3 Command design rules

- Parse and validate all args before touching state.
- Reply with concise usage; never dump stack traces to players.
- Rate-limit expensive commands even if `[Cooldown]` is present (it is per player, not global).
- Log admin actions with actor, target and result.
- Keep commands thin; call into a service/use case.
- Avoid one giant `switch`; map subcommands to handlers (`Dictionary<string, Action<...>>`).

### 6.4 Command exceptions

Carbon wraps chat and console callbacks in `Oxide/Command.cs`:

- chat: `Failed executing chat command '{command}' in '{plugin}' [callback]`
- console/RCon: `Failed executing console command '{command}' in '{plugin}' [callback]` or
  `[internal]`
- `TargetParameterCountException` is swallowed (signature mismatch), other exceptions are logged and
  unwrapped.

Again: no crash, but **no player feedback** and the command silently aborts. Guard the handler,
validate args, and reply with a neutral message:

```csharp
Safe.Run($"chat:{command}", () =>
{
    // validate + act
}, _exceptions, ex =>
{
    OnSafeError(ex);
    player.ChatMessage("Command unavailable; the server log has details.");
});
```

---

## 7. Permissions

### 7.1 Register and check

```csharp
private void Init()
{
    permission.RegisterPermission("myplugin.use", this);
    permission.RegisterPermission("myplugin.admin", this);
}

private void OnPlayerConnected(BasePlayer player)
{
    if (permission.UserHasPermission(player.UserIDString, "myplugin.admin"))
    {
        Puts($"{player.displayName} is an admin for MyPlugin.");
    }
}
```

- Permission strings should be lowercase, dot-namespaced and stable.
- Register in `Init()`/`Loaded()` before checking.
- `UserHasPermission` accepts `ulong` or string SteamID; use `player.UserIDString` to avoid format
  issues.
- For groups: `permission.GetGroups()`, `CreateGroup(name, title, rank)`, `RemoveGroup`,
  `GrantGroupPermission`, `RevokeGroupPermission`, `GetGroupPermissions(name, parents)`,
  `GetGroupRank`, `GetGroupTitle`, `GetGroupParent`.
- Permissions default to the in-memory protobuf store; `c.migrate_perms_sql` switches to SQLite at
  `<root>/server/<identity>/carbon.perms.db` (the DB is locked while the server runs),
  `c.migrate_perms_proto` switches back. Back up whichever store you use.
- Admin/moderator groups can be auto-granted from auth level (`Permissions` config).

### 7.2 Permission model for a plugin

Define a matrix up front:

| Permission | Grants | Default |
|---|---|---|
| `myplugin.use` | Open UI / basic commands | all players (grant on connect or default group) |
| `myplugin.vip` | Multipliers / cosmetics | VIP group |
| `myplugin.admin` | Reload config, inspect others | admin group |
| `myplugin.wipe` | Destructive ops | owner only + `[AuthLevel(2)]` |

- Check permission **and** ownership (`target.userID == caller.userID` unless admin override).
- Do not treat "UI hidden" as authorization.
- Provide `myplugin.admin` overrides explicitly; log them.
- For temporary access, prefer timed groups/permissions managed by a plugin, not hardcoded IDs.

---

## 8. Config, data, localization and secrets

### 8.1 Config pattern (Oxide/Carbon compatible)

```csharp
using Newtonsoft.Json;
using Oxide.Core;

namespace Carbon.Plugins;

public class MyPlugin : CarbonPlugin
{
    public Configuration PluginConfig;

    public class Configuration
    {
        [JsonProperty(PropertyName = "Version (DO NOT CHANGE)", Order = int.MaxValue)]
        public VersionNumber Version = new(1, 0, 0);

        [JsonProperty(PropertyName = "Enabled")]
        public bool Enabled = true;

        [JsonProperty(PropertyName = "Multiplier")]
        public float Multiplier = 1.5f;
    }

    protected override void LoadDefaultConfig() => PluginConfig = new Configuration();

    protected override void SaveConfig() => Config.WriteObject(PluginConfig, true);

    protected override void LoadConfig()
    {
        base.LoadConfig();
        PluginConfig = Config.ReadObject<Configuration>();

        if (PluginConfig == null)
        {
            LoadDefaultConfig();
            SaveConfig();
            return;
        }

        if (PluginConfig.Version < new VersionNumber(1, 1, 0))
        {
            MigrateConfig();
            PluginConfig.Version = new VersionNumber(1, 1, 0);
            SaveConfig();
        }
    }

    private void MigrateConfig()
    {
        // Example: rename "Multi" -> "Multiplier" while preserving value.
        var raw = Config.ReadObject<Dictionary<string, object>>();
        if (raw != null && raw.TryGetValue("Multi", out var old))
        {
            PluginConfig.Multiplier = Convert.ToSingle(old);
        }
    }
}
```

- Config path: `carbon/configs/<PluginName>.json`.
- Always version your config and migrate forward; never silently drop unknown keys.
- `Config.WriteObject(obj, true)` formats JSON.
- `LoadDefaultConfig` is called when the file is missing/corrupt.
- Do not store secrets in config; use Vault (below).

### 8.2 Data files

```csharp
using Oxide.Core;

var data = Interface.Oxide.DataFileSystem;
const string file = "MyPlugin/players";

var model = data.ExistsDatafile(file)
    ? data.ReadObject<PlayerData>(file)
    : new PlayerData();

data.WriteObject(file, model);
data.SaveDatafile(file); // force flush if needed
```

- Data path: `carbon/data/MyPlugin/players.json` (subfolders allowed).
- `DataFileSystem` methods: `ExistsDatafile`, `ReadObject<T>`, `WriteObject<T>`, `SaveDatafile`,
  `ForEachObject<T>`, `GetFiles`, `DeleteDataFile`.
- Writes are not atomic by default across process crashes; keep a `.bak` and write via temp+rename
  for critical data.
- For large/high-frequency data use SQLite, not JSON.
- Version your data schema and migrate; back up before migration.

### 8.3 Localization

```csharp
private void Loaded()
{
    lang.RegisterMessages(new Dictionary<string, string>
    {
        ["Welcome"] = "Welcome, {0}!",
        ["NoPermission"] = "You do not have permission."
    }, this, "en");
}

private void OnPlayerConnected(BasePlayer player)
{
    player.ChatMessage(lang.GetMessage("Welcome", this, player.UserIDString, player.displayName));
}
```

- Files live in `carbon/lang/<PluginName>/<locale>.json` (or are generated from defaults).
- Always provide a default language; fall back to `en`.
- Never concatenate translated strings; use format placeholders and pass values in order.
- Validate player names before embedding in messages (rich text/CUI).

### 8.4 Carbon Vault (secrets)

Vault stores encrypted values in `carbon.vault` (`<root>/server/identity/carbon.vault` by default).

```
> c.vault_add apiKey "super-secret" encrypted passwords
> c.vault
factory     items  encrypted  value
passwords   apiKey True
```

Config reference:

```json
{
  "ApiKey": "{passwords:apiKey}"
}
```

```csharp
public class Configuration
{
    public string ApiKey;
}
```

For a property to resolve protected values, annotate it (Carbon only):

```csharp
#if CARBON
[JsonConverter(typeof(Carbon.Components.Vault.Protected))]
#endif
public string ApiKey;
```

- Vault values are decrypted in memory; do not log them.
- Back up `carbon.vault` securely; losing it loses secrets.
- `c.vault_add <key> <value> [encrypted|true] [factory|global]`; the factory is the folder-like
  namespace used in `{factory:id}` references.
- On Oxide, `#if CARBON` excludes the converter and the config holds plaintext (document this).
- For CI/dev, use environment variables instead of committing secrets.

---

## 9. Timers, async and shutdown

### 9.1 Timers

```csharp
using Oxide.Plugins; // Timer

private Timer _tick;

private void OnServerInitialized()
{
    _tick = timer.Every(5f, () =>
    {
        // main thread; keep short
        foreach (var player in BasePlayer.activePlayerList)
        {
            // ...
        }
    });

    timer.Once(10f, () => Puts("10 seconds later"));
    timer.In(2f, () => Puts("2 seconds later"));
}

private void Unload()
{
    _tick?.Destroy();
    _tick = null;
}
```

- Timers run on the main thread.
- `timer.Repeat(action, repetitions, interval)` for a fixed count; `Every` for infinite.
- Always keep a reference and destroy it in `Unload()`; otherwise the callback runs after reload
  against a dead plugin instance.
- `NextFrame`/`NextTick` defer to the next frame; useful to avoid re-entrancy.
- Do not use `Thread.Sleep` or busy loops.

### 9.2 Async in plugins

```csharp
private async void OnServerInitialized()
{
    try
    {
        var data = await LoadRemoteAsync(_shutdown.Token);
        Puts($"Loaded {data.Count} records.");
    }
    catch (OperationCanceledException) { /* shutdown */ }
    catch (Exception ex) { Logger.Error("Load failed.", ex); }
}
```

- Hooks are `void`; `async void` is the only option for framework callbacks. Wrap in try/catch.
- Prefer starting a tracked task:

```csharp
private CancellationTokenSource _shutdown = new();
private Task _worker;

private void OnServerInitialized()
{
    _worker = RunAsync(_shutdown.Token);
}

private async Task RunAsync(CancellationToken ct)
{
    try { await Task.Delay(Timeout.Infinite, ct); }
    catch (OperationCanceledException) { }
}

private void Unload()
{
    _shutdown.Cancel();
    try { _worker?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    _shutdown.Dispose();
}
```

- `Wait` in `Unload` is acceptable for bounded shutdown, but never on the main thread during normal
  gameplay.
- Use `ConfigureAwait(false)` in pure library code, not before touching Unity/BasePlayer.
- Carbon bundles UniTask; use it for Unity-facing async when you need player-loop semantics.

### 9.3 Async server shutdown (CarbonPlugin)

```csharp
public override async ValueTask OnAsyncServerShutdown()
{
    await _database.FlushAsync();
    await base.OnAsyncServerShutdown();
}
```

- Gives async code a chance to flush before the process exits.
- Bound the work with a timeout; the server will not wait forever.
- `ValueTask` requires the runtime support Carbon provides; verify if targeting Oxide too.

### 9.4 Background work rules

- Never touch `BasePlayer`, entities, transforms or CUI from a worker thread.
- Marshal results back via `NextFrame`/`NextTick` or a main-thread dispatcher.
- Bound queues and concurrency; cancel on unload.
- Log exceptions from background tasks; unobserved exceptions are silent.
- Carbon installs `TaskScheduler.UnobservedTaskException` (`Logger.InitTaskExceptions`), but the
  task result is still lost; await/observe your own tasks.

### 9.5 Timer and task exceptions (the silent-stop trap)

`Timer.Scheduler.FireTimer` catches a throwing callback, logs
`Failed processing a timer of {delay}s in '{plugin}'`, and **destroys that timer**. A repeating
timer stops permanently after one exception — the most common "it worked, then silently stopped"
bug.

```csharp
// Wrong: one throw kills the repeating timer.
timer.Every(1f, Reconcile);

// Right: guard the whole callback; the timer survives.
timer.Every(1f, () => Safe.Run("timer:reconcile", Reconcile, _exceptions, OnSafeError));
```

For tasks, use `Safe.RunAsync`/`Safe.FireAndForget` so faults are observed and logged; see
`Exceptions.md` §6.4/§9.3.

---

## 10. UI: CUI, LUI and ClientEntity

### 10.1 The transport chain (verified)

```
plugin
  → CuiHelper.AddUi(player, json)
  → CommunityEntity.ServerInstance.ClientRPC(RpcTarget.Player("AddUI", player), json)
  → client CommunityEntity.AddUI(RPCMessage)
  → JSON array parsed into CUI elements
```

- Destroy one: `CuiHelper.DestroyUi(player, name)` → `"DestroyUI"`.
- Destroy many: `CommunityEntity.ServerInstance.SendDestroyUIs(player, List<string>|string[])` →
  `"DestroyUIs"` protobuf (`CuiHelper` only wraps single-panel `DestroyUi`).
- The client defaults `parent` to `"Overlay"` and supports `name`, `update`, `activeSelf`,
  `components[]`, `fadeOut`, `destroyUi`.
- Button/input `command` executes via `ConsoleNetwork.ClientRunOnServer(cmd)`; chat commands must be
  sent as `chat.say /cmd`. Carbon's protected commands are the safe way to expose actions.

### 10.2 Oxide/Carbon `CuiHelper`

```csharp
using Oxide.Game.Rust.Cui;
using UnityEngine;

private void ShowPanel(BasePlayer player)
{
    var elements = new CuiElementContainer
    {
        new CuiPanel
        {
            Image = { Color = "0.1 0.1 0.1 0.9" },
            RectTransform = { AnchorMin = "0.1 0.7", AnchorMax = "0.4 0.9" },
            CursorEnabled = true
        },
        new CuiLabel
        {
            Text = { Text = "Hello", FontSize = 18, Align = TextAnchor.MiddleCenter },
            RectTransform = { AnchorMin = "0 0.5", AnchorMax = "1 1" }
        },
        new CuiButton
        {
            Button = { Command = "chat.say /myplugin close", Color = "0.2 0.6 0.2 0.9" },
            RectTransform = { AnchorMin = "0.3 0.1", AnchorMax = "0.7 0.4" },
            Text = { Text = "Close", FontSize = 14, Align = TextAnchor.MiddleCenter }
        }
    };

    CuiHelper.DestroyUi(player, "MyPluginPanel");
    CuiHelper.AddUi(player, elements);
}

private void Unload()
{
    foreach (var player in BasePlayer.activePlayerList)
    {
        CuiHelper.DestroyUi(player, "MyPluginPanel");
    }
}
```

- `CuiElementContainer` serializes to the JSON array the client expects.
- Name every panel uniquely; destroy before re-adding to avoid duplicates.
- `CuiHelper.ActivePanels` tracks panels per player; `DestroyActivePanelList(player, except)` cleans
  up on unload.
- `CanUseUI` and `OnDestroyUI` hooks fire on add/destroy; respect them if other plugins manage UI.
- CUI text supports Unity rich text (`<color>`, `<size>`, `<b>`). Escape/strip player input to avoid
  visual injection.
- CUI is per-player and transient; it does not survive reconnect. Rebuild on `OnPlayerConnected` or
  `OnPlayerSleepEnded`.

### 10.3 LUI (Carbon's fluent UI)

```csharp
using Carbon.Components;

private void ShowLui(BasePlayer player)
{
    using var cui = new CUI(CuiHandler);

    var root = cui.v2.CreateParent(CUI.ClientPanels.Overlay, LuiPosition.Full, "MyPluginRoot");

    var panel = cui.v2.CreatePanel(
        root,
        new LuiPosition(0.1f, 0.7f, 0.4f, 0.9f),
        LuiOffset.None,
        "0.1 0.1 0.1 0.9");

    cui.v2.CreateText(panel, LuiPosition.None, LuiOffset.None, 18, "1 1 1 1", "Hello");

    cui.v2.CreateButton(
        panel,
        new LuiPosition(0.3f, 0.1f, 0.7f, 0.4f),
        LuiOffset.None,
        "chat.say /myplugin close",
        "0.2 0.6 0.2 0.9");

    cui.v2.SendUi(player);
}
```

- LUI is an internal Carbon component (not a separate plugin), always available on Carbon.
- It supports panels, text, images (sprite, PNG, item icon, URL, Steam avatar), buttons, inputs,
  outlines, colors/materials.
- Buttons are protected by default (`isProtected = true`); the generated command is safe to expose.
- `SendUiJson` is useful for debugging; bytes are more efficient in production.
- LUI is Carbon-only; for Oxide compatibility use `CuiHelper` or `#if CARBON`.

### 10.4 ClientEntity (per-client networked entity)

```csharp
using Carbon.Components;
using Network;
using UnityEngine;

private sealed class MarkerEntity : ClientEntity
{
    public MarkerEntity(uint prefabId, Vector3 position, Quaternion rotation)
        : base(null, prefabId)
    {
        Position = position;
        Rotation = rotation.eulerAngles;
    }

    public override void OnRpc(string rpc, Message message)
    {
        base.OnRpc(rpc, message);
        // client → server interaction for this synthetic entity
    }
}

private MarkerEntity _marker;

private void SpawnMarker(BasePlayer player)
{
    var prefabId = StringPool.Get("assets/prefabs/deployable/chair/chair.deployed.prefab");
    if (prefabId == 0) return;

    _marker = new MarkerEntity(prefabId, player.transform.position, Quaternion.identity);
    _marker.SpawnFor(player.Connection);
}

private void Unload()
{
    _marker?.Dispose();
    _marker = null;
}
```

- `Create(prefabName, position, rotation, proto, netId, group)` resolves a prefab name and returns a
  base `ClientEntity`; to handle client RPCs you must **subclass** `ClientEntity` and override
  `OnRpc`, because the RPC callback is a virtual method on the entity itself.
- `SpawnFor(connection)` / `SpawnAll(connections)`, `KillFor`, `KillAll`, `Dispose`.
- `SendNetworkUpdate`, `SendNetworkUpdate_Flags`, `SendNetworkUpdate_Position`; the `Flags` setter
  also auto-sends an update.
- Incoming client RPC is routed only if the connection is in the entity's watchers; `OnRpc` is only
  called for those connections.
- Use for visual-only/per-player props, holograms, custom interactions. Do not use for gameplay
  entities that must persist; they are "braindead" and server-side only.
- Network cost is per watcher; spawn to the minimum set of players.
- Always dispose on unload; otherwise watchers leak and clients keep ghost entities.

### 10.5 UI rules

- Server-side validate every button/input command; UI is not authorization.
- Destroy all panels on unload and on player disconnect (Carbon tracks active panels; also handle
  reconnect).
- Keep JSON payloads small; batch add/update instead of many RPCs.
- Use `update: true` for existing elements rather than destroy/recreate when possible.
- Avoid per-tick UI updates; throttle to 2–10 Hz for progress bars.
- Do not embed secrets or admin-only data in CUI JSON; the client can read it.

---

## 11. Bridge (server ↔ server)

Carbon.Bridge is a WebSocket IPC layer built on Fleck (the RCON listener), independent of Rust's
RCON. It lets multiple Rust servers or external tools exchange messages.

### 11.1 Start

```
+RCon.Port 28016
+bridge.password "a-long-random-secret"
# optional: -bridge.ip 0.0.0.0  -bridge-port 28017
```

- The bridge server does **not** start unless `-bridge.password` is set.
- Default port: `RCon.Port + 1`.
- Use TLS termination/VPN/firewall; the bridge itself is plain WebSocket.

### 11.2 Server side

`BridgeServer` is **abstract** in current source. Derive from it and call
`Start(BridgeServerInfo)`:

```csharp
using Carbon.Components;

private sealed class MyBridgeServer : BridgeServer
{
    public override void OnBridgeConnection(BridgeConnection connection)
        => Logger.Log($"Bridge client {connection.Id} connected.");

    public override void OnBridgeDisconnection(BridgeConnection connection)
        => Logger.Log($"Bridge client {connection.Id} disconnected.");

    public override void OnServerConnected()
        => Logger.Log("Bridge listener started.");

    public override void OnServerDisconnected()
        => Logger.Log("Bridge listener stopped.");
}

private MyBridgeServer _server;

private void StartBridge()
{
    _server = new MyBridgeServer();
    _server.Start(new BridgeServerInfo
    {
        port = RCon.Port + 1,
        ip = "0.0.0.0",
        password = "a-long-random-secret",
        context = "MyPlugin",
        messages = new CustomMessages(),
        maxConnections = 32,
        maxConnectionsPerIp = 4
    });
}

private void Unload()
{
    _server?.Shutdown();
    _server = null;
}
```

> The published Bridge docs still show `Bridge.Server ??= new(); Bridge.Server.Start(port, password,
> ip, messages)`. That member is **not present** in current `Components/Bridge.cs`; use
> `BridgeServerInfo` and verify against the build you target.

### 11.3 Client side

```csharp
using Carbon.Components;

private BridgeClient _client;

private async void OnServerInitialized()
{
    var messages = new CustomMessages();

    _client = await Bridge.StartClient(
        "127.0.0.1",
        RCon.Port + 1,
        "super-secret",
        messages);

    var writer = BridgeWrite.Rent();
    writer.BridgeMessage(BridgeMessages.Channels.Custom);
    writer.String("hello");
    await _client.Send(writer);
    BridgeWrite.Return(ref writer);
}

private async void Unload()
{
    if (_client != null)
    {
        await _client.Disconnect();
        _client = null;
    }
}

private sealed class CustomMessages : BridgeMessages
{
    protected override void OnCustom(BridgeRead read)
        => Logger.Log(read.String());

    protected override void OnRpc(BridgeRead read) { /* custom RPC */ }
    protected override void OnCommand(BridgeRead read) { /* custom command */ }
    protected override void OnUnhandled(BridgeRead read) { /* drain */ }
}
```

- Channels: `Rpc`, `Command`, `Custom`; anything unknown dispatches to `OnUnhandled` (drain it).
- `BridgeWrite.Rent()` / `Return(ref writer)`; write types in the same order the reader expects.
- `OnCommand` can run server commands (`ConsoleSystem.Run(ConsoleSystem.Option.Server, ...)`) —
  authenticate and validate; never execute raw remote input.
- Use it for cross-server chat/economy, web→game commands, or telemetry. It is transport, not a
  durable queue; add an outbox/idempotency if delivery matters.

---

## 12. Extensions and modules

### 12.1 Extensions (`ICarbonExtension`)

An extension is a compiled DLL in `carbon/extensions` that provides shared code to plugins.

```csharp
#if CARBON
using System;
using API.Assembly;
using Carbon;

namespace MyExtension;

public sealed class ExtensionEntrypoint : ICarbonExtension
{
    public void Awake(EventArgs args) { }

    public void OnLoaded(EventArgs args)
    {
        Community.Runtime.Events.Subscribe(API.Events.CarbonEvent.OnServerInitialized, _ =>
        {
            Logger.Log("Extension ready.");
        });
    }

    public void OnUnloaded(EventArgs args) { }
}
#endif
```

- Build against Carbon assemblies; the class must implement `ICarbonExtension`.
- Extensions are hot-reloaded from `carbon/extensions` (watcher).
- Use them for shared libraries, not for per-plugin state.
- Version the extension API; plugins compiled against an older extension can break on update.

### 12.2 Modules

Carbon modules are built-in features (admin panel, stack manager, vanish, whitelist, etc.) loaded
from `carbon/managed/modules`. Module integration documentation is marked "coming soon"; for now,
interact through their public APIs/hooks or avoid coupling. Do not assume module internals are stable
across Carbon updates.

---

## 13. HarmonyMod in depth

### 13.1 Contract

Rust ships a built-in Harmony mod loader. A mod is a DLL in `HarmonyMods/` (beside
`RustDedicated.exe`) that:

- Implements `IHarmonyModHooks` (global namespace) with `OnLoaded`/`OnUnloaded`.
- Contains static `[HarmonyPatch]` classes; the loader calls `Harmony.PatchAll(assembly)`.

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

[HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.CanSuicide))]
public static class NoSuicidePatch
{
    public static bool Prefix(ref bool __result)
    {
        __result = false;
        return false;
    }
}
```

- `OnHarmonyModLoadedArgs`/`OnHarmonyModUnloadedArgs` are empty marker classes.
- The loader creates the entry type with `Activator.CreateInstance` (parameterless constructor).
- Patch classes are discovered by `[HarmonyPatch]`; static methods named `Prefix`/`Postfix`/
  `Transpiler`/`Finalizer` or annotated.
- Multiple `IHarmonyModHooks` types per assembly are supported; each is instantiated.

### 13.2 Loader internals (what actually happens)

From the decompiled `HarmonyLoader` (Rust):

1. `HarmonyLoader.LoadHarmonyMods(modsFolderPath)` ensures `0Harmony.dll` is loaded, sets
   `Harmony.DEBUG = true`, and sets `HARMONY_LOG_FILE` to `harmony_log.txt`.
2. It adds the mods folder to the assembly resolver, so dependencies next to the mod load.
3. It enumerates `*.dll` and skips names starting with `System.`, `Microsoft.`, `Newtonsoft.` or
   `UnityEngine.`; everything else is passed to `TryLoadMod`.
4. `TryLoadMod` first unloads an existing mod with the same file name, loads the assembly from a
   `byte[]` (`assembly.Location` is therefore empty), finds `IHarmonyModHooks` implementors, creates
   the mod's `Harmony` instance with ID `com.facepunch.rust_dedicated.<name>`, and calls
   `PatchAll(assembly)`.
5. On success it calls `OnLoaded` on each hook and adds the mod to `loadedMods`.
6. `TryUnloadMod` calls `OnUnloaded`, `UnpatchAll(harmonyId)`, removes it from `loadedMods`.

Important consequences:

- **Hot-swapping a loaded DLL is unsupported.** `TryLoadMod` unloads by name first, but the CLR
  cannot unload an assembly; loading a new copy can produce duplicate types/invalid IL. Use
  `harmony.unload`, stop the server, replace the DLL, start, `harmony.load`.
- A patch exception in `PatchAll` aborts the whole mod load; check `harmony_log.txt`.
- A DLL with **no** `IHarmonyModHooks` type still loads and patches; hooks are optional for pure
  patch-only mods.
- Dependency resolution is by file name in `HarmonyMods/`; ship dependencies beside the mod or
  merge them.
- The loader logs to `harmony_log.txt` in the working directory (often the server root).

### 13.3 Commands and lifecycle

| Command | Effect |
|---|---|
| `harmony.load <Name>` | Load `<Name>.dll` from `HarmonyMods/`; unloads an existing mod with that name first. |
| `harmony.unload <Name>` | Call `OnUnloaded`, unpatch the mod's Harmony ID, remove it. |
| `harmony.mods` | Carbon-only console command (auth 2): lists loaded mods with hook count and patched-method count. |

- Loaded mods **persist across restarts** (the server scans `HarmonyMods/` at boot).
- `harmony.unload` does not delete the DLL; it just unloads patches for this process. On restart it
  loads again unless you remove/rename the DLL.
- Adding a DLL while the server runs does nothing until `harmony.load <Name>`.
- Renaming a mod changes its Harmony ID; don't expect unload by the old name to work.
- `-harmonydir <path>` overrides the folder Rust scans (Carbon exposes the same switch).

### 13.4 Carbon interaction

Carbon and HarmonyMods coexist, with caveats:

- At startup, Carbon moves the contents of `carbon/harmony/` into `<rust-root>/HarmonyMods/`.
- Rust's own `HarmonyLoader` then loads those DLLs, just like vanilla.
- Carbon's `harmony.mods` reports `HarmonyLoader.loadedMods` (name, hooks, patched methods).
- Carbon's **compat layer** can convert Harmony 1.x mods to Harmony 2:
  - `HarmonyConverter` applies `HarmonyTypeRef`, `HarmonyPatchProcessor`, `OxideTypeRef`, etc.
  - `HarmonyEntrypoint` finds types implementing `IHarmonyModHooks`, generates `load`/`unload`
    entrypoints, and schedules `new HarmonyLib.Harmony("__CCL:{asm}:{guid:N}").PatchAll()` on
    `CarbonEvent.HookValidatorRefreshed` (guarded by a `loaded` bool).
  - This is why a correctly built Harmony 2 mod can run under both vanilla Rust and Carbon.
- Carbon does **not** guarantee full HarmonyMod compatibility, especially for mods that:
  - patch the small blacklist of host methods Carbon strips during conversion (for example
    `ServerMgr.UpdateServerInformation`),
  - rely on Harmony 1 APIs,
  - transpile methods that Carbon has already publicized/patched,
  - assume a specific load order relative to Carbon hooks.

Practical rule: **build against Harmony 2.3+, use unique Harmony IDs, patch only game methods, and
test under vanilla and Carbon separately.**

### 13.5 Building a HarmonyMod

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <LangVersion>10</LangVersion>
    <AssemblyName>MyMod</AssemblyName>
    <OutputType>Library</OutputType>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
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
    <!-- Facepunch.Network, Facepunch.Console, UnityEngine.* as needed -->
  </ItemGroup>
</Project>
```

- Target `net48` (or `netstandard2.0` if you avoid Framework-only APIs); the server is Mono/net48.
- `Private=false` for server/Harmony references; copy only your own dependencies.
- Output `MyMod.dll` → `HarmonyMods/`.
- Do not ship `0Harmony.dll`; the server provides it (version mismatch causes type identity issues).
- For config/data, use `HarmonyLoader.modPath` or a path relative to the server root:
  - config convention: `HarmonyConfig/<Mod>.json`
  - data convention: `HarmonyMods_Data/<Mod>/...`
  These are conventions, not loader-provided paths; create directories yourself.

### 13.6 Config/data in a HarmonyMod (no Oxide base class)

```csharp
using Newtonsoft.Json;

public sealed class ModConfig
{
    public bool Enabled { get; set; } = true;
    public float Multiplier { get; set; } = 1.5f;
}

public static class ModStorage
{
    // The loader may load the mod from a byte[] (Assembly.Location is empty), so resolve
    // paths from the game assembly instead: <root>/RustDedicated_Data/Managed/Assembly-CSharp.dll
    private static string Root
    {
        get
        {
            var managed = Path.GetDirectoryName(typeof(BasePlayer).Assembly.Location)!;
            return Directory.GetParent(Directory.GetParent(managed)!.FullName)!.FullName;
        }
    }

    public static string ConfigPath =>
        Path.Combine(Root, "HarmonyConfig", "MyMod.json");

    public static ModConfig Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path))
        {
            var defaults = new ModConfig();
            Save(defaults);
            return defaults;
        }

        return JsonConvert.DeserializeObject<ModConfig>(File.ReadAllText(path)) ?? new ModConfig();
    }

    public static void Save(ModConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
    }
}
```

- Resolve paths from the **server root**. `Assembly.Location` for the mod itself is empty because
  Rust loads HarmonyMods via `Assembly.Load(byte[])`; derive the root from the game assembly or use
  `Environment.CurrentDirectory` when the server is launched from its root.
- Use atomic writes (temp + `File.Replace`) for critical data.
- Version your JSON and tolerate missing fields (`[JsonProperty(DefaultValueHandling = ...)]`).

### 13.7 HarmonyMod unload discipline

- In `OnUnloaded`, destroy timers (`Timer`/`MonoBehaviour`), unsubscribe events, remove console
  commands, close sockets, and flush data.
- `harmony.unload` only unp patches; it does not call `Dispose` on your objects unless you do it in
  `OnUnloaded`.
- Do not keep static references to `BasePlayer`/entities after unload; they hold the old assembly
  alive and can cause memory leaks.
- `Harmony.UnpatchAll(harmonyId)` is done by the loader; do not call `UnpatchAll()` with no ID (it
  unpatches everything, including other mods/plugins).
- If your mod spawns `MonoBehaviour` runners, `Destroy` their GameObjects in `OnUnloaded`.

### 13.8 Debugging HarmonyMods

- Check `harmony_log.txt` first: load failures, dependency resolution, patch errors.
- Use `harmony.mods` under Carbon to see hook/patched-method counts.
- Add a startup log with assembly version, Harmony version, and each patch target:
  `AccessTools.Method(...)` null check.
- Use `Harmony.DEBUG = true`; Harmony writes detailed patch info.
- Test on a **vanilla** server (no Carbon) to isolate loader issues, then under Carbon.
- Never test by overwriting a loaded DLL; stop, replace, start.

### 13.9 HarmonyMod vs Carbon plugin

| Need | Carbon `.cs` plugin | HarmonyMod DLL |
|---|---|---|
| Rapid iteration | ✅ hot reload | ❌ stop/load |
| Carbon APIs (CUI helper, permissions, config, timers) | ✅ built in | ❌ build yourself |
| Works on vanilla/Oxide | ❌ | ✅ |
| Deep IL patching | ✅ via `[AutoPatch]` | ✅ native |
| Runtime language features | C# 10-safe | C# 10-safe |
| Distribution | source file | compiled binary |
| Trust/safety | source review possible | opaque DLL |
| Unload/reload cleanliness | framework-assisted | manual |
| Best for | gameplay features, admin tools | low-level engine patches, cross-framework fixes |

Use a Carbon plugin by default. Use a HarmonyMod when the feature must exist without Carbon or must
patch something Carbon does not expose.

### 13.10 Patch and mod exceptions

Harmony 2 does **not** wrap prefix/postfix invocation in a try/catch unless a **finalizer** is
present (verified in `MethodPatcher.cs` v2.3.6). A throwing prefix therefore propagates into the
patched game method's caller; under Carbon it is not covered by the plugin hook/command/timer
containment.

- Wrap every patch body in try/catch; log through a static accessor to a mod-owned `ExceptionLog`
  (`HarmonyMods_Data/<Mod>/logs/`).
- Add `static Exception Finalizer(Exception __exception)` to observe; return `__exception` to rethrow,
  `null` to suppress (only when state is consistent).
- Wrap `PatchAll`/patch installation; log the target `MethodInfo`; fail the patch, not the process.
- Wrap `OnLoaded`/`OnUnloaded`; do not rely on the loader to contain partial initialization.
- Full templates: `Exceptions.md` §11.

---

## 14. Hybrid Carbon + HarmonyMod patterns

### 14.1 Shared core, two hosts

```
MyFeature.Core/        (netstandard2.0 or net48 class library, no Carbon/Oxide refs)
MyFeature.Carbon/      (.cs plugin: config, commands, CUI, permissions; calls Core)
MyFeature.Harmony/     (HarmonyMod DLL: IHarmonyModHooks + patches; calls Core)
```

- Core contains rules, models, persistence interfaces and pure logic.
- Carbon host supplies adapters for players/items/UI.
- Harmony host supplies patches and its own config/data.
- Avoid duplicating logic; version Core and reference the same binary in both builds if possible
  (HarmonyMod can load a dependency from `HarmonyMods/`; Carbon plugin compilation does not
  automatically reference arbitrary DLLs — embed the source or use an extension).

### 14.2 Detect the host at runtime

```csharp
public static bool IsCarbon =>
    Type.GetType("Carbon.Community,Carbon.Common") != null;

public static bool IsOxide =>
    Type.GetType("Oxide.Core.Interface,Oxide.Core") != null;
```

- Compile-time `#if CARBON` only works in Carbon's compiler; a HarmonyMod DLL is compiled once, so
  use reflection for runtime detection.
- Do not take a hard dependency on Carbon types from a HarmonyMod; load them reflectively if needed.
- If both are present, prefer Carbon's APIs and skip duplicate patches.

### 14.3 Avoid double patching

- Use a unique Harmony ID per host/build (`com.example.myfeature.carbon`,
  `com.example.myfeature.harmony`).
- If the Carbon plugin already patches method X, the HarmonyMod must not patch X again unless the
  patches are commutative and intentional.
- Add a guard: a static `Applied` flag plus `Harmony.GetPatchInfo(method)` inspection.
- Log which host applied the patch; include it in bug reports.

### 14.4 Migration path

1. Start with a Carbon plugin; isolate domain logic in a library.
2. If a missing hook blocks you, add an `[AutoPatch]` inside the plugin first.
3. If the patch must work without Carbon, extract it into a HarmonyMod that calls the same Core.
4. Keep the Carbon plugin for UX (commands/UI/config); the HarmonyMod only does the low-level patch.
5. Test both hosts on the same Rust build; document the matrix.

### 14.5 Fallback decision tree

```
Is there a Carbon/Oxide hook?
├─ Yes → use it (compose hooks if needed)
└─ No
   ├─ Can a timer/state scan approximate it safely? → do that
   ├─ Is the patch only needed while your plugin is loaded? → [AutoPatch] in the plugin
   └─ Must it work on vanilla/Oxide, or survive Carbon unload?
      └─ Ship a HarmonyMod DLL (IHarmonyModHooks + [HarmonyPatch])
```

---

## 15. Performance and profiling

### 15.1 The cost model

- The server is single-threaded; every millisecond in a hook is a millisecond not simulating.
- Patching has a fixed cost at load; hook dispatch has a per-call cost. Carbon patches only used
  hooks, which is a major win over "patch everything".
- Allocations cause GC spikes; Rust's GC is Unity/Mono's, and a big collection can hitch the server.
- Network/UI/DL calls are orders of magnitude slower than memory; never do them synchronously in a
  hot hook.

### 15.2 Profiler

Carbon ships a Mono profiler (`c.profile` commands; config `carbon/config.profiler.json`). It can
profile game assemblies, Carbon, plugins, modules, extensions and Harmony mods.

```json
{
  "Enabled": true,
  "TrackCalls": true,
  "SourceViewer": true,
  "Assemblies": ["Assembly-CSharp"],
  "Plugins": ["*"],
  "Modules": ["*"],
  "Extensions": [],
  "Harmony": ["MyMod"]
}
```

- `-c` calls, `-t` timings, `-cm` call memory, `-gc` GC events.
- Timings significantly affect performance; profile in a test environment, not peak hours.
- Use `c.profile` to record a window around a suspected hook; read the hot path, not just totals.
- For pure C# logic, BenchmarkDotNet outside the game is more precise.

### 15.3 Optimization checklist

- [ ] No LINQ/allocations in per-tick/per-damage/per-entity hooks.
- [ ] Early-return on irrelevant entities before expensive work.
- [ ] Cache lookups (`BasePlayer`, config, prefab IDs).
- [ ] Batch DB writes; use transactions; move them off the main thread.
- [ ] Throttle UI updates; batch CUI changes.
- [ ] Bound queues/collections; clean up on disconnect/death.
- [ ] Avoid `string` concatenation in loops; use `StringBuilder`/`StringPool`.
- [ ] Use `Facepunch.Pool` for temporary lists/dicts; return in `finally`.
- [ ] Measure before/after with the profiler; keep a baseline.

### 15.4 Common performance bugs

| Symptom | Cause | Fix |
|---|---|---|
| Server hitches every few seconds | Large LINQ/allocations in a timer | Rewrite as `for`, reuse buffers. |
| Memory grows after hours | Leaked players/entities in dictionaries | Remove on disconnect/death; use `WeakReference` only if appropriate. |
| High CPU at idle | Per-tick scan over all entities | Use hooks/events or a spatial index; increase interval. |
| UI lag | Rebuilding CUI every tick | Update only changed fields; throttle. |
| DB stalls | Synchronous writes on main thread | Queue + background writer; WAL. |
| Patch load slow | Many transpilers/patches | Reduce patch count; prefer prefix/postfix. |

---

## 16. Security model

- **Plugins are fully trusted code.** There is no sandbox. A malicious plugin can read files, send
  network requests, ban players or crash the server. Vet sources, hashes and permissions.
- **Clients are untrusted.** Every command, RPC, CUI action and argument can be forged.
- **Admins are privileged but fallible.** Validate RCon/console input; log destructive actions.
- **Carbon Vault** protects secrets at rest; it does not protect against a compromised server.
- **Protected commands** hide UI-only commands from players; they do not replace authorization.
- **RCON** is `ws://` (not `wss://`); bind to localhost/VPN, firewall, long password, rotate.
- **Bridge** requires a password and is plain WebSocket; use a private network/TLS proxy.
- **Web requests** from plugins can leak server IP/keys; allow-list hosts, set timeouts, validate
  responses.
- **Third-party DLLs** (HarmonyMods/extensions) run with full trust; prefer source plugins.

Security checklist is in `Senior_C.MD` §7; apply it to every command, UI action, DB query and
external call.

---

## 17. Compatibility, versioning and updates

### 17.1 Rust update cadence

- Rust ships a forced update on the **first Thursday of each month**; servers must wipe/update.
- Carbon publishes builds per Rust branch (production, staging, aux). Update Carbon before debugging
  plugins after a Rust update.
- Plugin APIs (game types/methods) change; patches and transpilers are the most fragile.
- Keep a compatibility matrix: plugin version × Carbon build × Rust protocol × Oxide/Carbon.

### 17.2 Oxide compatibility

- `Oxide.Plugins.RustPlugin` + Oxide APIs usually run on Carbon.
- Carbon-specific APIs are unavailable on Oxide; guard with `#if CARBON` or runtime detection.
- CUI: `CuiHelper` is compatible; Carbon's `CUI`/`LUI` are not.
- Config/data paths differ (`oxide/config` vs `carbon/configs`); Carbon migrates some on install but
  do not rely on it for new plugins.
- Commands: Oxide `cmd.AddChatCommand` etc. still work; Carbon attributes are cleaner.
- Test on both if you publish to both ecosystems.

### 17.3 Versioning your plugin

- SemVer: breaking config/permission/API change → major; feature → minor; fix → patch.
- Include version in `[Info]` and in config (`Version (DO NOT CHANGE)`).
- Migrate config/data forward; never require admins to hand-edit JSON.
- Keep a changelog with migration notes and breaking changes.
- Deprecate old permission/command names with aliases; remove after a major version.

### 17.4 Wipe-day runbook

1. Stop server before the Rust update.
2. Update Rust via SteamCMD; update Carbon to the matching build.
3. Start once to let Carbon patch/publicize; watch for hook failures.
4. Deploy plugin updates; check `c.plugins` and logs.
5. Run smoke tests: join, run primary command, open UI, trigger one hook, check DB.
6. Monitor errors/performance for the first hour; keep rollback artifacts.

---

## 18. Quick reference

### 18.1 Console commands

| Command | Purpose |
|---|---|
| `c.version` | Carbon version. |
| `c.plugins` | Loaded plugins and status. |
| `c.reload <Plugin>` | Recompile/reload a plugin. |
| `c.find c.` | List Carbon commands. |
| `harmony.load <Name>` | Load a HarmonyMod DLL. |
| `harmony.unload <Name>` | Unload patches for a mod. |
| `harmony.mods` | List loaded Harmony mods (Carbon, auth 2). |
| `c.profile ...` | Profiler controls. |
| `c.vault`, `c.vault_add`, `c.vault_remove` | Secret store. |

### 18.2 Attribute cheat sheet

```csharp
[Info("Name", "Author", "1.0.0")]
[Description("...")]
[ChatCommand("cmd")]
[ConsoleCommand("cmd")]
[RConCommand("cmd")]
[Command("cmd")]
[ProtectedCommand("cmd")]
[Permission("plugin.use")]
[Group("admin")]
[AuthLevel(2)]
[Cooldown(5_000)]
[AutoPatch]
[HarmonyPatch(typeof(T), nameof(T.M))]
```

### 18.3 Folder cheat sheet

| Path | Contents |
|---|---|
| `carbon/plugins` | `.cs`, `.cszip` |
| `carbon/configs` | `<Plugin>.json` |
| `carbon/data` | plugin data |
| `carbon/lang` | localization |
| `carbon/extensions` | `ICarbonExtension` DLLs |
| `carbon/managed/modules` | module DLLs |
| `carbon/harmony` | moved to `HarmonyMods` at boot |
| `HarmonyMods` | Rust Harmony mod DLLs |
| `HarmonyMods_Data` | conventional mod data |
| `HarmonyConfig` | conventional mod config |

### 18.4 Logging cheat sheet

```csharp
Puts("info");                                  // [Title] info
LogWarning("warn");                            // warning
LogError("failed", ex);                        // error + exception
LogToFile("audit", "player bought kit", this); // carbon/logs/<Plugin>/<plugin>_audit-<date>.txt

Carbon.Logger.OnErrorCallback += OnCarbonError; // filter by plugin name; unsubscribe in Unload
Carbon.Logger.InitTaskExceptions();             // Carbon already calls this at startup
```

- Separate exception log: `carbon/logs/<Plugin>/<Plugin>_exceptions-YYYY-MM-DD.log` (+ `.jsonl`).
- HarmonyMod: `HarmonyMods_Data/<Mod>/logs/`.
- Guard: `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`,
  `Application.logMessageReceivedThreaded`, `Carbon.Logger.OnErrorCallback`.
- See `Exceptions.md` for code, dedupe/rotation and tests.

### 18.5 Minimal hybrid layout

```
MyPlugin/
├── MyPlugin.cs                 # CarbonPlugin: config, commands, UI, hooks
├── Core/                       # pure logic, no Carbon refs
├── Adapters/                   # Carbon adapters
└── Harmony/
    ├── MyMod.cs                # IHarmonyModHooks
    ├── Patches/                # [HarmonyPatch] classes
    └── MyMod.csproj            # net48, references 0Harmony + game DLLs
```

---

## 19. Error handling and logging

Carbon gives you three overlapping logging surfaces. Use all three for different purposes.

### 19.1 Plugin-facing API

| API | Writes to | Use |
|---|---|---|
| `Puts(msg)` / `Log(msg)` | Console + `carbon/logs/Carbon.Core.log` (via Carbon logger) | Normal lifecycle/info, prefixed `[Title]`. |
| `LogWarning(msg)` / `PrintWarning(...)` | Console/log at warning severity | Recoverable/expected problems. |
| `LogError(msg, ex)` / `PrintError(...)` / `RaiseError(msg)` | Console/log at error severity | Boundary failures; include the exception object. |
| `LogToFile(filename, text, this, timeStamp: true)` | `carbon/logs/<Plugin>/<plugin>_<filename>-<yyyy-MM-dd>.txt` | Simple per-plugin audit/error file; appends. |
| `Carbon.Logger.Error/Warn/Log` | `Carbon.Core.log` + Unity console | Framework-level/explicit logging. |
| `Carbon.Logger.OnErrorCallback` | Your callback (`Action<string, Exception, int>`) | Mirror every Carbon-logged error; filter by plugin name. |
| `Carbon.Logger.InitTaskExceptions()` | `Carbon.Core.log` | Already called by Carbon at startup; `UnobservedTaskException` → `SetObserved()` + log. |
| `Application.logMessageReceived` (Carbon) | `Carbon.Core.log` only for `Null`/`Index` conditions | Not a complete net; do not rely on it. |

### 19.2 `LogToFile` details (verified)

`RustPlugin.LogToFile(string filename, string text, Plugin plugin = null, bool timeStamp = true, bool anotherBool = false)`:

- With `plugin: this`: folder `carbon/logs/<Plugin>/`, file `<plugin>_<filename>-<date>.txt`
  (lowercase), line prefix `[yyyy-MM-dd HH:mm:ss] `.
- With `plugin: null`: folder is `carbon/logs/` (or the subfolder in `filename`), file
  `<filename>-<date>.txt`.
- It uses `OsEx.File.Append` — **serialize calls** if you write from multiple threads; it is not a
  full logger (no rotation, no dedupe, no schema).

For anything beyond a few lines, use the dedicated `ExceptionLog` in `Exceptions.md` §9:
`carbon/logs/<Plugin>/<Plugin>_exceptions-YYYY-MM-DD.log` + `.jsonl`, with fingerprinting, dedupe,
rate limits, rotation and crash files.

### 19.3 `carbon/logs` layout and rotation

| Item | Behaviour |
|---|---|
| `carbon/logs/Carbon.Core.log` | Carbon's main log; `FileLogger` splits at `Logging.LogSplitSize` (default 2.5 MB in `config.json`; field default is 5 MB), archives to `carbon/logs/archive/`. |
| `carbon/logs/archive/` | `Carbon.Core.<timestamp>.log` archives and backups. |
| `carbon/logs/<Plugin>/` | `LogToFile` and your exception log. |
| `carbon/logs/Carbon.Core_locked.log` | Fallback when the main file cannot be backed up (in use). |
| Severity | `Logging.LogSeverity`; debug verbosity `Logging.LogVerbosity`; file mode `Logging.LogFileMode` (0 off, 2 immediate flush). |

### 19.4 What Carbon catches, and what it does not

| Boundary | Carbon behaviour |
|---|---|
| Hook | try/catch in `HookCallerInternal.DoCall`; logs `Failed to call hook ...`; **no rethrow**. |
| Chat/console command | try/catch in `Oxide/Command.cs`; logs `Failed executing ... [callback]/[internal]`. |
| Timer | `Timer.Scheduler.FireTimer` catches and **destroys the timer**. |
| Config load | `RustPlugin.ILoadConfig` catches and logs. |
| Unobserved task | `Logger.InitTaskExceptions` observes and logs. |
| Harmony patch | **Not caught** without a finalizer. |
| Raw background thread | **Not caught**; process may terminate. |
| Static constructor | `TypeInitializationException`; type unusable. |

The practical consequence: crashes are rare, **silent feature loss is common**. Install the guard
and write the separate file (`Exceptions.md` §7–§11).

### 19.5 Integration sketch

```csharp
private void Loaded()
{
    _exceptions = new ExceptionLog(
        Title, Version.ToString(),
        Path.Combine(Defines.GetLogsFolder(), Title),
        environment: BuildEnvironment(),
        isMainThread: () => ThreadEx.IsOnMainThread());

    _guard = new PluginGuard(Title, _exceptions);
    _guard.Install();
}

private void Unload()
{
    Safe.Unload("Unload", _exceptions,
        () => _guard?.Dispose(),
        () => _exceptions?.Dispose());
}
```

`PluginGuard` subscribes to `AppDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException`,
`Application.logMessageReceivedThreaded` (`LogType.Exception` only) and, under `#if CARBON`,
`Carbon.Logger.OnErrorCallback`.

### 19.6 Limits (do not oversell the guard)

- `AppDomain.UnhandledException` is **notification only**; the process still terminates.
- Stack overflow, access violation, OOM and native crashes are not reliably catchable.
- Unity main-thread exceptions may be logged and continued by Unity, but state can be inconsistent.
- `OnErrorCallback` is global; another plugin may assign `=` and drop your handler.
- A per-plugin file is required for analysis; `Carbon.Core.log` alone is not enough.

---

## 20. Sources

Checked 2026-10-17.

- Carbon documentation: <https://carbonmod.gg/> — developers, features, hooks, commands, permissions,
  timers, async shutdown, extensions, CUI/LUI, ClientEntity, Bridge, Vault, conditionals, `.cszip`,
  profiler, configuring Carbon. Docs pages dated 2025–2026.
- Carbon source: <https://github.com/CarbonCommunity/Carbon> — `CarbonPlugin.cs`,
  `Core.Plugin.cs`, `Core.Commands.Engine.cs` (`harmony.mods`), `Components/Switches.cs`
  (`-harmonydir`), `Carbon.Startup/src/Entrypoint.cs` (`carbon/harmony` → `HarmonyMods`),
  `Carbon.Compat/src/Patches/Harmony/HarmonyEntrypoint.cs` (`__CCL` entrypoint),
  `Carbon.Compat/src/Converters/HarmonyConverter.cs`, `Carbon.Profiler/src/HarmonyProfiler.cs`,
  `Components/ClientEntity.cs`, `Oxide/CUI/CuiHelper.cs`.
- Facepunch, *Harmony is Rust's built-in modding platform* (Meta Madness, 2024-05-02) — Harmony 2.3,
  recompile requirement. <https://rust.facepunch.com/news/meta-madness>
- Facepunch, *July 2022 update* — `harmony.load`/`harmony.unload` added.
  <https://rust.facepunch.com/news/july-2022-update>
- Decompiled `IHarmonyModHooks`/`HarmonyLoader` (Rust build) — loader internals, `harmony_log.txt`,
  `loadedMods`, `com.facepunch.rust_dedicated.<name>`.
- Carbon docs: *Creating Hooks* (`[HookAttribute.Patch]`, `HookCaller.CallStaticHook`),
  *Commands*, *Permissions*, *Configuration Management*, *Extensions*, *Vault*, *ClientEntity*.
- Carbon docs: *Profiler (Mono)* and *Modifiers* (Harmony config category, `carbon/harmony`).
- Codefling, *Carbon* product page — "C# 10 + Harmony 2.0" claim (secondary; source is authoritative).
- Facepunch/Rust.Community — `CommunityEntity` CUI transport (`AddUI`, `DestroyUI`, `DestroyUIs`).
- OxideMod/Oxide.Rust — `RustCui` wrapper and `CanUseUI`/`OnDestroyUI` hooks.
- Carbon source (checked 2026-10-17): `src/Carbon/src/Hooks/HookCallerInternal.cs`,
  `src/Carbon.Components/Carbon.Common/src/Oxide/Command.cs`,
  `.../Oxide/Libraries/Timer.Scheduler.cs`, `.../Oxide/RustPlugin.cs` (`LogToFile`),
  `.../Carbon/Components/Logger.cs` (`InitTaskExceptions`, `OnErrorCallback`),
  `.../Carbon/Components/FileLogger.cs`, `src/Carbon/src/Community.cs`.
- Microsoft Learn, *Best practices for exceptions* / *AppDomain.UnhandledException* /
  *TaskScheduler.UnobservedTaskException* — see `Exceptions.md` §14.
- Harmony, *Finalizer* and `MethodPatcher.cs` v2.3.6 — patches are not caught without a finalizer.
- OWASP, *Logging Cheat Sheet* — secret/PII exclusion, log injection, retention, failure isolation.
- Unity 6, `Application.logMessageReceivedThreaded` — thread-safe handler, no Unity APIs.

> **Recency caveat:** the `Rust.HarmonyMods` sample repository (GatherManager) targets `net4.6.2` and
> was last updated in 2022. It is still useful for loader/interface shape, but **recompile against
> Harmony 2.3+** and re-test; do not copy its project settings verbatim.

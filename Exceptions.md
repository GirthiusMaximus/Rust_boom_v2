# Exceptions.md — Exception Handling, Error Logging and Crash Containment

> **Purpose.** A complete, Carbon/HarmonyMod-specific standard for handling exceptions in Rust
> plugins, with a **separate exception log** for analysis, a **global guard** for last-chance
> capture, and ready-to-use code.
>
> **Applies to:** Carbon `.cs` plugins, `.cszip` packages, `ICarbonExtension` DLLs, and HarmonyMod
> DLLs.
>
> **Companions:** `CARBON_PLUGIN_PLAYBOOK.MD` §11, `CarbonMod.MD` §19, `DontJustRollWithIt.MD`
> (evidence/collaboration protocol), `Senior_C.MD` §1.6/§7.12.

---

## Table of contents

1. [The honest contract](#1-the-honest-contract)
2. [Exception mechanics on net48/Mono](#2-exception-mechanics-on-net48mono)
3. [What Carbon already catches (verified)](#3-what-carbon-already-catches-verified)
4. [What nothing catches](#4-what-nothing-catches)
5. [Policy: fail open, fail closed, fail fast](#5-policy-fail-open-fail-closed-fail-fast)
6. [Boundary catalogue and guards](#6-boundary-catalogue-and-guards)
7. [Global guard design](#7-global-guard-design)
8. [The separate exception log](#8-the-separate-exception-log)
9. [Implementation](#9-implementation)
10. [Carbon plugin integration](#10-carbon-plugin-integration)
11. [HarmonyMod integration](#11-harmonymod-integration)
12. [Testing exception handling](#12-testing-exception-handling)
13. [Review and acceptance checklist](#13-review-and-acceptance-checklist)
14. [Sources](#14-sources)

---

## 1. The honest contract

"Handle every possible exception" is impossible in the literal sense. A managed process cannot
recover from a stack overflow, an access violation, a native engine crash, `Environment.FailFast`,
or an unhandled exception on a raw background thread in .NET Framework — the process terminates.
Microsoft's own guidance is blunt: *"a crashed app is more reliable and diagnosable than an app with
undefined behavior."*

What a plugin **can** guarantee:

1. **Containment at every boundary you own.** Hooks, commands, timers, tasks, patches, UI callbacks,
   I/O and unload steps never let an exception escape into host code.
2. **A last-chance net** for the paths that bypass those boundaries (`AppDomain.UnhandledException`,
   `TaskScheduler.UnobservedTaskException`, Unity's log stream, Carbon's error callback).
3. **Evidence.** Every captured exception is written to a **separate, structured, rotated log file**
   with enough context to reproduce and fix it.
4. **Degradation, not corruption.** Decide per feature whether to fail open (skip the feature) or
   fail closed (deny the action), and never leave half-mutated state.
5. **No self-inflicted crashes.** No `async void` outside framework handlers, no unguarded raw
   threads, no `.Result`/`.Wait()` on the main thread, no throwing from loggers or cleanup.

The guard described here is a **safety net**, not a force field. Prevention comes from §6.

---

## 2. Exception mechanics on net48/Mono

### 2.1 Hierarchy and wrapping

- All managed exceptions derive from `System.Exception`.
- `TargetInvocationException` wraps exceptions thrown by reflected calls (Carbon unwraps it as
  `ex.InnerException ?? ex`).
- `AggregateException` carries one or more task exceptions; call `.Flatten()` before logging.
- `TypeInitializationException` wraps a static-constructor failure; the plugin type may be unusable.
- `OperationCanceledException` (and `TaskCanceledException`) means cancellation, not a fault; treat
  shutdown cancellation as normal.

### 2.2 Rethrow and preserve the stack

```csharp
catch (Exception ex)
{
    Log(ex);
    throw;                 // preserves the original stack
}
```

- `throw;` — rethrows, keeps stack.
- `throw ex;` — resets the stack to this line; never do it.
- `ExceptionDispatchInfo.Capture(ex).Throw()` — rethrows later/elsewhere with the original stack
  (available on net48). Use when you must store an exception and rethrow after cleanup.

### 2.3 Catch filters (`when`)

```csharp
catch (IOException ex) when (IsDiskFull(ex))
{
    // handle only the expected case
}
```

Filters run before the stack unwinds; non-matching filters fall through to outer handlers. Use them
to avoid catching too broadly.

### 2.4 Fatal and uncatchable exceptions

| Exception | Reality |
|---|---|
| `StackOverflowException` | Cannot be caught; process terminates. |
| `OutOfMemoryException` | May be caught, but state is unreliable; log and fail fast. |
| `AccessViolationException` | Corrupted state; not delivered to `AppDomain.UnhandledException` unless legacy CSE policy; do not rely on it. |
| `ThreadAbortException` | Legacy .NET Framework; avoid `Thread.Abort` entirely. |
| `Environment.FailFast` / native crash | No managed handler; use OS crash dumps/core dumps. |

Do **not** write `catch (Exception)` around these expecting recovery. Catch them only to log and
exit cleanly where the runtime permits.

### 2.5 Exceptions in async code

- A faulted `Task` stores its exception; it surfaces when awaited, or via
  `TaskScheduler.UnobservedTaskException` if never observed.
- `async void` exceptions are raised on the `SynchronizationContext` (Unity logs them); the caller
  cannot catch them. Only `async void` framework handlers, always with an internal try/catch.
- `Task.WhenAll` throws the first exception; inspect `task.Exception?.Flatten()` for all.
- Cancellation is `OperationCanceledException` with a cancelled token; do not log it as an error.
- `.Result`/`.Wait()` wrap faults in `AggregateException` and can deadlock the main thread.

### 2.6 Reflection and Harmony

- Reflection wraps faults in `TargetInvocationException`; unwrap `InnerException`.
- Harmony 2 does **not** catch exceptions thrown by prefix/postfix methods. Without a finalizer,
  the exception propagates into the patched game method's caller. A finalizer makes Harmony wrap the
  original and all other patches in a try/catch and can observe/suppress/replace the exception.
  ([Harmony docs](https://harmony.pardeike.net/v2/articles/patching-finalizer.html); verified in
  `MethodPatcher.cs` at tag `v2.3.6.0`.)

---

## 3. What Carbon already catches (verified)

Carbon is not a silent host: it wraps several entry points and logs to `Carbon.Core.log`. Knowing
exactly what it does prevents both duplicate handling and false confidence.

| Boundary | Carbon behaviour | Consequence for your plugin |
|---|---|---|
| **Hook invocation** | `HookCallerInternal.DoCall` wraps `hook.Method.Invoke(...)` in try/catch, unwraps `InnerException`, logs `Failed to call hook '{hook}' on plugin '{plugin} v{version}'`, and **does not rethrow**. | The server/plugin does not crash, but that hook's work is skipped. Return values from a failed hook are `null`/default; a failed `Can*` hook may allow the action. Wrap for semantics, not just crashes. |
| **Chat/console command** | `Oxide/Command.cs` wraps callbacks in try/catch, logs `Failed executing chat command '{command}' in '{plugin}' [callback]` (also `[internal]`), unwraps `InnerException`; `TargetParameterCountException` is ignored. | No crash, but the player gets no feedback. Catch at the command boundary to reply with a safe message. |
| **Timer callback** | `Timer.Scheduler.FireTimer` catches, logs `Failed processing a timer of {delay}s in '{plugin}'`, and **destroys the timer**. | A single throw kills a repeating timer. Always wrap the callback; otherwise your periodic task silently stops forever. |
| **Config load** | `RustPlugin.ILoadConfig` wraps `LoadConfig()` and logs `Failed ILoadConfig`. | A bad config does not crash the server, but the plugin may run with defaults/null. Validate and repair. |
| **Unobserved task** | `Logger.InitTaskExceptions()` subscribes to `TaskScheduler.UnobservedTaskException`, calls `SetObserved()`, logs `Unobserved task exception [GC]`. | Process does not terminate by default, but the task's result is lost. Observe/await your own tasks. |
| **Unity error log** | `Community` subscribes to `Application.logMessageReceived` and mirrors only conditions starting with `Null` or `Index` as `Unhandled error occurred`. | Many Unity-logged exceptions are **not** mirrored by Carbon. Your guard should capture `LogType.Exception`. |
| **Global error callback** | `Carbon.Logger.OnErrorCallback` (`Action<string, Exception, int>`) is invoked for every `Logger.Error`. | You can mirror Carbon's hook/command/timer errors into your own file, filtered by plugin name. |
| **Plugin `Load()` (virtual)** | `ModLoader.InitializePlugin` wraps only the constructor; `IInit()` and `ILoad()` are called directly. `ILoad()` runs `OnLoaded` → `Loaded` → your `Load()` override, then the throw escapes to `ScriptLoader`'s per-type catch, which logs `Failed to compile '<file>'`. | The plugin is already in the package with hooks subscribed, so it stays **half-loaded** and the log blames compilation. Guard the `Load()` body yourself. |
| **Plugin load/compile failure** | Compilation errors are logged by Carbon before the plugin exists. | Your exception log cannot capture these; read `Carbon.Core.log`. |

**Key source files** (Carbon `main`, checked 2026-10-17):

- `src/Carbon/src/Hooks/HookCallerInternal.cs` — hook try/catch, unwrap, log, no rethrow.
- `src/Carbon.Components/Carbon.Common/src/Oxide/Command.cs` — command try/catch, `[callback]`/`[internal]`.
- `src/Carbon.Components/Carbon.Common/src/Oxide/Libraries/Timer.Scheduler.cs` — `FireTimer` catch + destroy.
- `src/Carbon.Components/Carbon.Common/src/Oxide/RustPlugin.cs` — `ILoadConfig`, `LogToFile`, `Puts`/`LogError`.
- `src/Carbon.Components/Carbon.Common/src/Carbon/Components/Logger.cs` — `InitTaskExceptions`, `OnErrorCallback`, `Write`.
- `src/Carbon.Components/Carbon.Common/src/Carbon/Components/FileLogger.cs` — `carbon/logs/<name>.log`, archive, split.
- `src/Carbon/src/Community.cs` — calls `Logger.InitTaskExceptions()`.
- `src/Carbon.Components/Carbon.Common/src/Carbon/Modding/ModLoader.cs` — constructor try/catch, direct `IInit()`/`ILoad()` calls, unload loop.
- `src/Carbon.Components/Carbon.Common/src/Oxide/Plugin.cs` — `IInit`, `ILoad` (`OnLoaded` → `Loaded` → `Load()`), `IUnload` internals.
- `src/Carbon/src/Loaders/ScriptLoader.cs` — per-type catch that logs `Failed to compile '<file>'`.

> **Important:** Carbon's containment means a broken hook rarely crashes the server. That is exactly
> why silent feature loss is the usual failure mode — and why the separate exception log matters.

---

## 4. What nothing catches

| Path | What happens | Prevention |
|---|---|---|
| Exception on a raw `new Thread(...)` | .NET Framework terminates the process; `AppDomain.UnhandledException` is notification only. | Never use raw threads unguarded; use `Task` + `Safe`, or a wrapped worker with a top-level catch. |
| `async void` method | Exception is posted to the `SynchronizationContext`; caller cannot catch; Unity logs it. | Use `async Task`; if a handler must be `async void`, wrap the entire body in try/catch. |
| Harmony prefix/postfix throw | Propagates into the patched method's caller (no catch without a finalizer). | try/catch every patch body; add a finalizer to observe. |
| Harmony transpiler throw | Throws during `PatchAll`; the mod/plugin may fail to load. | Wrap patch installation; log target + IL; fail the patch, not the process. |
| Static constructor throw | `TypeInitializationException`; the type is unusable for the process lifetime. | Avoid static constructors; use `Lazy<T>`/explicit init with guards. |
| Stack overflow / OOM / access violation / native crash | Process dies; managed handler may not run. | Bound recursion/allocations; fix native assumptions; keep crash dumps. |
| Logger throws | If unhandled, it can crash the caller. | Logger must never throw; catch inside, rate-limit console fallback. |
| `Unload()` cleanup throw | Later cleanup steps may be skipped; state leaks into the next load. | Run each cleanup in its own try/catch (`Safe.Unload`). |

---

## 5. Policy: fail open, fail closed, fail fast

Decide per feature **before** coding. Document the choice next to the hook/patch.

| Feature kind | On exception | Rationale |
|---|---|---|
| Cosmetic (UI, markers, sounds, chat formatting) | **Fail open**: log, skip the effect, continue vanilla | No gameplay/security impact; never punish the player for a cosmetic bug. |
| Optional integration (Discord, metrics, web panel) | **Fail open** + circuit breaker | The game must survive an external outage. |
| Economy, permissions, ownership, anti-cheat | **Fail closed**: deny the action, log, notify admins | A wrong allow is worse than a denied action. |
| Data persistence | **Fail fast for the write**, keep the in-memory state consistent, retry/queue | Silent data loss is unacceptable; do not half-write. |
| Startup/config | **Fail fast for the plugin** (unload/disable) if the config cannot be trusted | Running with unknown settings is worse than not running. |
| Patch installation | **Fail fast for the patch** (disable feature/unload plugin if `IsRequired`) | A missing security patch must be visible. |

Rules:

- Never let an exception leave a lock held, a transaction open, an entity half-spawned, or a file
  half-written.
- Prefer `try { mutate } catch { rollback; throw; }` with a rollback that cannot throw.
- If you fail closed, tell the player something neutral ("Action unavailable; try again") and log the
  detail server-side.
- If you retry, retry only **transient** failures (timeouts, `SQLITE_BUSY`, 5xx) with backoff and a
  cap; never retry validation/auth failures.

---

## 6. Boundary catalogue and guards

Every entry point gets a guard. The pattern is always: **try → do work → catch → log with context →
return the documented fallback**.

| # | Boundary | Guard | Fallback |
|---|---|---|---|
| 1 | Hook method | `Safe.Run` / `Safe.Run<T>` | `null` (continue) or documented block value |
| 2 | Chat/console/RCon command | `Safe.Run` + user reply | Safe error message |
| 3 | Timer callback | `Safe.Run` | Keep timer alive |
| 4 | `Task` / worker loop | `Safe.RunAsync` + observed task | Stop loop cleanly / retry with backoff |
| 5 | `async void` handler | try/catch around whole body | Log only |
| 6 | Harmony prefix/postfix/transpiler | try/catch inside patch; finalizer | Fail open/closed per §5 |
| 7 | HarmonyMod `OnLoaded`/`OnUnloaded` | try/catch + guard | Load degraded or fail mod load |
| 8 | CUI button/input callback | Server command guard + validation | Deny with message |
| 9 | Bridge message handler | try/catch + schema validation | Drop message, log, keep connection |
| 10 | HTTP/WebSocket callback | try/catch + timeout/CTS | Circuit break |
| 11 | File/DB/JSON read/write | try/catch + backup/fallback | Use last good value / disable feature |
| 12 | Event subscription handler | try/catch, unsubscribe on unload | Isolate one subscriber |
| 13 | Per-item loop (players/entities) | try/catch **inside** the loop | Continue with next item |
| 14 | `Unload()` | `Safe.Unload` with separate actions | Run all cleanup steps |
| 15 | Static/initialization | explicit `Initialize()` with guard | Disable plugin/feature |
| 16 | `ICarbonExtension` `Awake`/`OnLoaded`/`OnUnloaded` | try/catch + guard | Inert extension, host unaffected |

### 6.1 Hooks

```csharp
private object OnEntityDeath(BasePlayer player, HitInfo info)
{
    return Safe.Run("OnEntityDeath", () =>
    {
        if (player == null || info == null) return null;
        // ...
        return null; // continue vanilla
    }, fallback: null, _exceptions, OnSafeError);
}
```

- For `void` hooks use the `Action` overload.
- For `Can*` hooks, choose the fallback deliberately (see §5); `null` often means "no opinion".
- Keep the guarded body small; do not run long I/O inside.

### 6.2 Commands

```csharp
[ChatCommand("kit")]
private void KitCommand(BasePlayer player, string command, string[] args)
{
    if (player == null) return;

    Safe.Run($"chat:{command}", () =>
    {
        if (!args.Any()) { player.ChatMessage("Usage: /kit <name>"); return; }
        // validate + act
    }, _exceptions, ex =>
    {
        LogError($"[kit] {ex.GetType().Name}", ex);
        player.ChatMessage("Kit service is temporarily unavailable.");
    });
}
```

- Never send `ex.ToString()`/stack traces to players.
- Validate `args` length, ranges, ownership, permission inside the guard.

### 6.3 Timers — the silent killer

Carbon destroys a timer whose callback throws. This is the single most common "it worked, then
stopped after one error" bug.

```csharp
_timer = timer.Every(1f, () =>
{
    Safe.Run("timer:reconcile", Reconcile, _exceptions, OnSafeError);
});
```

- Wrap the **whole** callback, not just part of it.
- If the callback must survive partial failure, catch per item inside.
- Destroy the timer in `Unload()`.

### 6.4 Tasks and workers

```csharp
private void StartWorker()
{
    _cts = new CancellationTokenSource();
    _worker = Safe.RunAsync("worker:main", () => WorkerLoop(_cts.Token), _exceptions, OnSafeError);
}

private async Task WorkerLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        await ProcessBatch(ct).ConfigureAwait(false);
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
    }
}
```

- `Safe.RunAsync` catches, logs and returns `false`; it never lets the task fault unobserved.
- Still keep a `try/catch (OperationCanceledException)` in the loop for normal shutdown.
- Marshal results back to the main thread before touching `BasePlayer`/Unity objects.

### 6.5 Per-item loops

```csharp
foreach (var player in BasePlayer.activePlayerList)
{
    try
    {
        SendNotice(player);
    }
    catch (Exception ex)
    {
        _exceptions.Error("PLUGIN.LOOP", "SendNotice", ex, player.userID);
    }
}
```

One bad player/entity must not abort the whole batch.

### 6.6 `Unload()`

```csharp
private void Unload()
{
    Safe.Unload("Unload", _exceptions,
        () => _cts?.Cancel(),
        () => _timer?.Destroy(),
        () => CuiHelper.DestroyUi(...),
        () => _guard?.Dispose(),
        () => _exceptions?.Dispose());
}
```

- Each action isolated; a failure does not skip the rest.
- Dispose the guard **before** the log, so no handler writes to a disposed log.
- Set fields to `null` after disposal.

### 6.7 Harmony patches

```csharp
[HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.CanSuicide))]
public static class NoSuicidePatch
{
    public static bool Prefix(ref bool __result)
    {
        try
        {
            __result = false;
            return false;
        }
        catch (Exception ex)
        {
            MyPlugin.LogException("patch:CanSuicide.Prefix", ex);
            return true; // fail open: vanilla decides
        }
    }

    public static Exception Finalizer(Exception __exception)
    {
        if (__exception != null)
            MyPlugin.LogException("patch:CanSuicide.Finalizer", __exception);

        return __exception; // rethrow; return null to suppress
    }
}
```

- A finalizer is the only built-in catch around patches; without it, your prefix exception goes
  straight into the game method's caller.
- Do not suppress exceptions unless you can prove the game state is consistent.
- Transpilers run at patch time; wrap `PatchAll` and log the target method.

---

## 7. Global guard design

The guard installs four last-chance observers for the lifetime of the plugin/mod:

| Observer | Purpose | Recovery? |
|---|---|---|
| `AppDomain.CurrentDomain.UnhandledException` | Log an exception that is about to terminate the process; write a crash file. | **No** — notification only; the runtime terminates. |
| `TaskScheduler.UnobservedTaskException` | Catch faulted tasks nobody awaited; `SetObserved()`. | Yes — prevents escalation policy; task result is still lost. |
| `Application.logMessageReceivedThreaded` | Capture `LogType.Exception` entries Unity logs, including exceptions from Unity callbacks. | Depends — Unity has already handled/logged it. |
| `Carbon.Logger.OnErrorCallback` | Mirror Carbon's hook/command/timer/config errors for this plugin into the separate file. | Carbon already contained it. |

Optional, developer-only: `AppDomain.FirstChanceException` — logs every thrown exception before
handlers run. Extremely noisy, can recurse, and changes timing; enable only behind a debug flag.

### 7.1 Rules for the guard

1. Install **after** the exception log exists, before any feature code runs.
2. Handlers must never throw; wrap everything and fall back to `Console.Error`.
3. `AppDomain.UnhandledException` runs while locks may be held — do not wait on other threads, take
   locks, or call Carbon/Unity APIs. Write a dedicated crash file directly.
4. `Application.logMessageReceivedThreaded` may run on any thread, in parallel, and must not call
   Unity APIs. Use only thread-safe state.
5. Use a recursion guard (`Interlocked.Exchange`) around Unity/Carbon callbacks.
6. Filter by plugin name so one plugin's guard does not hoover up the whole server's errors.
7. Uninstall in reverse order in `Unload()`; dispose the log last.
8. The guard does not make raw threads safe. See §4.

### 7.2 What the guard cannot do

- Prevent termination for an unhandled exception on a raw background thread.
- Catch `StackOverflowException`, `AccessViolationException`, native crashes or `FailFast`.
- Recover corrupted Unity state after a main-thread exception.
- Replace per-boundary guards; it only sees what escapes them.

---

## 8. The separate exception log

### 8.1 Location

| Host | Directory | Example file |
|---|---|---|
| Carbon plugin | `carbon/logs/<Plugin>/` | `carbon/logs/MyPlugin/MyPlugin_exceptions-2026-10-17.log` |
| Carbon plugin (machine) | same | `.../MyPlugin_exceptions-2026-10-17.jsonl` |
| HarmonyMod | `HarmonyMods_Data/<Mod>/logs/` | `HarmonyMods_Data/MyMod/logs/MyMod_exceptions-2026-10-17.log` |
| Crash dump | same directory | `crash-20261017-123456-4242-ab12cd34.log` |

Use a **separate file** from `Carbon.Core.log`:

- it survives Carbon's own rotation/severity filtering;
- it contains full stacks + inner exceptions for your plugin only;
- it is easy to attach to a bug report;
- it can be parsed (`jq`) for fingerprints and frequencies.

### 8.2 Record schema

| Field | Why |
|---|---|
| `ts` (UTC, ISO 8601) | Correlate with `Carbon.Core.log`, console, player reports. |
| `severity` | `ERROR`, `FATAL`, `SUMMARY`. |
| `event` | Stable ID: `PLUGIN.HOOK`, `PLUGIN.COMMAND`, `PLUGIN.TIMER`, `PLUGIN.TASK`, `PLUGIN.PATCH`, `PLUGIN.UNITY`, `PLUGIN.CARBON`, `PLUGIN.UNHANDLED`, `PLUGIN.UNOBSERVED`, `PLUGIN.UNLOAD`, `PLUGIN.RATE_LIMIT`. |
| `plugin`, `version` | Which build produced it. |
| `context` | Hook/command/timer/patch name + key arguments (sanitized). |
| `player` | SteamID64 only if relevant; consider privacy. |
| `thread`, `main` | Detect off-main-thread Unity access. |
| `type`, `message` | Exception identity. |
| `stack` | Full stack (capped). |
| `inner` | Inner-exception chain (capped depth 5). |
| `fingerprint` | Hash of type + message + top frame; groups repeats. |
| `repeats` | Suppressed count since last write. |
| `dropped` | Records dropped by rate limit/queue. |
| `env` | Carbon/Rust/Unity versions, captured once at load. |
| `extra` | Boundary-specific key/values (`prefab`, `netId`, `command`, `unity_stack`). |

### 8.3 Deduplication and rate limiting

A broken per-tick path can throw thousands of times per second. Without limits, the logger becomes
the outage.

- **Fingerprint**: SHA-256 of `type|message|first stack frame`, first 16 hex chars.
- **Dedupe window** (default 30 s): first occurrence writes a full record; repeats increment a
  counter; on the next write after the window, `repeats=N` is included.
- **Rate limit** (default 120 records/minute): beyond that, drop with a counter; emit a
  `PLUGIN.RATE_LIMIT` summary when the window resets.
- **Queue cap** (default 8192): `TryAdd` without blocking the game thread; on overflow increment
  `dropped`.
- On dispose, write `PLUGIN.SUPPRESSED` summaries for fingerprints that still have suppressed counts.

### 8.4 Rotation, retention, durability

- One file per UTC day, plus `.1`, `.2`… suffixes when a file exceeds the size cap (default 8 MB).
- `AutoFlush = true`; error volume is low after dedupe, and crash durability matters more than
  throughput.
- Retention default 14 days; purge old `*_exceptions-*` and `crash-*` files at startup and daily.
- Flush/close on `Unload`; join the writer thread with a timeout.
- On `AppDomain.UnhandledException`, write a **separate crash file** directly (no queue, no shared
  lock) so a deadlocked writer cannot lose the evidence.

### 8.5 Logging hygiene (OWASP)

- **Never log**: passwords, API keys, tokens, session IDs, connection strings, full config dumps,
  payment data, unnecessary PII.
- **Sanitize**: replace `\r`/`\n` in single-line fields to prevent log injection; cap lengths;
  escape JSON.
- **Protect**: `chmod 750` directory, `chmod 640` files, owned by the server user; never serve logs
  from a web root.
- **Retain**: documented period; delete on request where applicable; logs are evidence, not a
  database.
- **Fail safe**: a logging failure must never take down the plugin; count failures and warn on
  console at most once per minute.
- **Test**: simulate read-only directory, full disk, concurrent writers, and a throwing formatter.

### 8.6 Analysis workflow

```bash
# Top fingerprints in today's file
jq -r 'select(.fp) | .fp' carbon/logs/MyPlugin/MyPlugin_exceptions-2026-10-17.jsonl \
  | sort | uniq -c | sort -rn | head

# Full records for one fingerprint
jq -c 'select(.fp=="ab12cd34ef567890")' carbon/logs/MyPlugin/MyPlugin_exceptions-2026-10-17.jsonl

# Correlate by time with Carbon's log
grep -n "2026-10-17T12:34" carbon/logs/Carbon.Core.log
```

Attach the **fingerprint**, the **full record**, and the surrounding `Carbon.Core.log` lines to a bug
report — not the entire file.

---

## 9. Implementation

Create these files in your plugin project (or paste them into the `.cs`/`.cszip`). They are
net48/C# 10 compatible: no `System.Text.Json`, no `ArgumentNullException.ThrowIfNull`, no
`ValueTask`, no .NET Core-only string overloads.

### 9.1 `ExceptionLog.cs`

```csharp
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MyPlugin.Infrastructure;

/// <summary>
/// Thread-safe, bounded, deduplicating exception log.
/// Writes a human-readable .log and a machine-readable .jsonl.
/// Never throws from Error(); logging failure must not break the plugin.
/// </summary>
public sealed class ExceptionLog : IDisposable
{
    private readonly BlockingCollection<ExceptionRecord> _queue =
        new BlockingCollection<ExceptionRecord>(new ConcurrentQueue<ExceptionRecord>(), 8192);

    private readonly ConcurrentDictionary<string, FingerprintState> _seen =
        new ConcurrentDictionary<string, FingerprintState>(StringComparer.Ordinal);

    private readonly object _rateLock = new object();
    private readonly string _pluginName;
    private readonly string _pluginVersion;
    private readonly string _environment;
    private readonly string _directory;
    private readonly string _baseName;
    private readonly TimeSpan _dedupeWindow;
    private readonly int _maxRecordsPerMinute;
    private readonly int _retainDays;
    private readonly Func<bool>? _isMainThread;
    private readonly Sink? _human;
    private readonly Sink? _json;
    private readonly Thread _worker;
    private readonly bool _disabled;

    private DateTime _minuteStart = DateTime.UtcNow;
    private int _recordsThisMinute;
    private long _droppedTotal;
    private long _writeFailures;
    private int _disposed;

    public string PluginName => _pluginName;
    public string Directory => _directory;

    public ExceptionLog(
        string pluginName,
        string pluginVersion,
        string logDirectory,
        string environment = "unknown",
        bool enableJson = true,
        int maxFileBytes = 8 * 1024 * 1024,
        int retainDays = 14,
        int maxRecordsPerMinute = 120,
        TimeSpan? dedupeWindow = null,
        Func<bool>? isMainThread = null)
    {
        _pluginName = SanitizeFileName(pluginName ?? "plugin");
        _pluginVersion = pluginVersion ?? "?";
        _environment = environment ?? "unknown";
        _directory = logDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        _baseName = _pluginName + "_exceptions";
        _retainDays = retainDays;
        _maxRecordsPerMinute = maxRecordsPerMinute;
        _dedupeWindow = dedupeWindow ?? TimeSpan.FromSeconds(30);
        _isMainThread = isMainThread;

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            PurgeOldFiles();
        }
        catch (Exception ex)
        {
            _disabled = true;
            TryConsole("cannot create log directory: " + ex.Message);
        }

        // Deterministic on every path: a disabled logger has no sinks (Error() also checks _disabled).
        _human = null;
        _json = null;

        if (!_disabled)
        {
            _human = new Sink(_directory, _baseName, ".log", maxFileBytes);
            _json = enableJson ? new Sink(_directory, _baseName, ".jsonl", maxFileBytes) : null;
        }

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "ExceptionLog:" + _pluginName
        };
        _worker.Start();
    }

    /// <summary>Capture a boundary exception. Safe to call from any thread.</summary>
    public void Error(
        string eventId,
        string context,
        Exception exception,
        ulong? playerId = null,
        IDictionary<string, string>? extra = null,
        string severity = "ERROR")
    {
        if (Volatile.Read(ref _disposed) != 0 || _disabled) return;

        try
        {
            var record = ExceptionRecord.Create(
                eventId, context, exception, _pluginName, _pluginVersion,
                playerId, extra, _environment, severity,
                _isMainThread?.Invoke());

            record.DroppedTotal = Interlocked.Read(ref _droppedTotal);

            if (!ShouldWrite(record)) return;

            if (!_queue.TryAdd(record))
            {
                Interlocked.Increment(ref _droppedTotal);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _writeFailures);
            TryConsole("Error() failed: " + ex.Message);
        }
    }

    /// <summary>Direct crash write for AppDomain.UnhandledException. No queue, no shared locks.</summary>
    public void WriteCrash(string eventId, Exception exception, bool terminating)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_directory);

            var record = ExceptionRecord.Create(
                eventId,
                "AppDomain.UnhandledException; terminating=" + terminating.ToString(CultureInfo.InvariantCulture),
                exception, _pluginName, _pluginVersion, null, null,
                _environment, "FATAL", null);

            var path = Path.Combine(
                _directory,
                string.Format(CultureInfo.InvariantCulture,
                    "crash-{0:yyyyMMdd-HHmmss}-{1}-{2}.log",
                    DateTime.UtcNow, Process.GetCurrentProcess().Id,
                    Guid.NewGuid().ToString("N").Substring(0, 8)));

            File.WriteAllText(
                path,
                FormatHuman(record) + Environment.NewLine + FormatJson(record) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch
        {
            // A crash handler must never throw.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _queue.CompleteAdding(); } catch { }

        try { _worker.Join(TimeSpan.FromSeconds(5)); } catch { }

        try { FlushSuppressed(); } catch { }

        try { _human?.Dispose(); } catch { }
        try { _json?.Dispose(); } catch { }
        try { _queue.Dispose(); } catch { }
    }

    private bool ShouldWrite(ExceptionRecord record)
    {
        var now = DateTime.UtcNow;

        lock (_rateLock)
        {
            if ((now - _minuteStart).TotalMinutes >= 1d)
            {
                _minuteStart = now;
                _recordsThisMinute = 0;
            }

            if (_recordsThisMinute >= _maxRecordsPerMinute)
            {
                Interlocked.Increment(ref _droppedTotal);
                return false;
            }

            _recordsThisMinute++;
        }

        var state = _seen.GetOrAdd(record.Fingerprint, _ => new FingerprintState());

        lock (state)
        {
            if (now - state.LastWritten < _dedupeWindow)
            {
                state.Suppressed++;
                return false;
            }

            record.RepeatCount = state.Suppressed + 1;
            state.Suppressed = 0;
            state.LastWritten = now;
            return true;
        }
    }

    private void WorkerLoop()
    {
        foreach (var record in _queue.GetConsumingEnumerable())
        {
            try { _human?.Write(FormatHuman(record)); }
            catch (Exception ex) { OnWriteFailure(ex); }

            if (_json != null)
            {
                try { _json.Write(FormatJson(record)); }
                catch (Exception ex) { OnWriteFailure(ex); }
            }
        }
    }

    private void OnWriteFailure(Exception ex)
    {
        var count = Interlocked.Increment(ref _writeFailures);

        if (count == 1 || count % 100 == 0)
        {
            TryConsole("write failure #" + count.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
        }
    }

    private void FlushSuppressed()
    {
        var now = DateTime.UtcNow;

        foreach (var pair in _seen)
        {
            var state = pair.Value;
            int suppressed;

            lock (state)
            {
                suppressed = state.Suppressed;
                state.Suppressed = 0;
            }

            if (suppressed <= 0) continue;

            var record = new ExceptionRecord
            {
                TimestampUtc = now,
                Severity = "SUMMARY",
                EventId = "PLUGIN.SUPPRESSED",
                PluginName = _pluginName,
                PluginVersion = _pluginVersion,
                Context = "suppressed repeats",
                ExceptionType = "RepeatSummary",
                Message = suppressed.ToString(CultureInfo.InvariantCulture) + " occurrence(s) suppressed",
                Fingerprint = pair.Key,
                RepeatCount = suppressed,
                Environment = _environment
            };

            try { _human?.Write(FormatHuman(record)); } catch { }
            if (_json != null) { try { _json.Write(FormatJson(record)); } catch { } }
        }
    }

    private void PurgeOldFiles()
    {
        if (_retainDays <= 0) return;

        var cutoff = DateTime.UtcNow.AddDays(-_retainDays);
        Purge(_baseName + "-*", cutoff);
        Purge("crash-*", cutoff);
    }

    private void Purge(string pattern, DateTime cutoff)
    {
        foreach (var file in System.IO.Directory.GetFiles(_directory, pattern))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch { }
        }
    }

    private void TryConsole(string message)
    {
        try
        {
            Console.Error.WriteLine("[ExceptionLog:" + _pluginName + "] " + message);
        }
        catch { }
    }

    private static string FormatHuman(ExceptionRecord r)
    {
        var sb = new StringBuilder(2048);

        sb.Append('[').Append(r.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append(" UTC] [").Append(r.Severity).Append("] [").Append(r.EventId).Append("] [")
          .Append(r.PluginName).Append(' ').Append(r.PluginVersion).Append("] context=")
          .Append(Clean(r.Context, 400));

        if (r.PlayerId.HasValue) sb.Append(" player=").Append(r.PlayerId.Value.ToString(CultureInfo.InvariantCulture));
        sb.Append(" thread=").Append(r.ThreadId.ToString(CultureInfo.InvariantCulture));
        if (r.IsMainThread.HasValue) sb.Append(" main=").Append(r.IsMainThread.Value ? "true" : "false");
        sb.Append(" fp=").Append(r.Fingerprint);

        if (r.RepeatCount > 1) sb.Append(" repeats=").Append(r.RepeatCount.ToString(CultureInfo.InvariantCulture));
        if (r.DroppedTotal > 0) sb.Append(" dropped_total=").Append(r.DroppedTotal.ToString(CultureInfo.InvariantCulture));

        sb.AppendLine();
        sb.Append("  ").Append(r.ExceptionType).Append(": ").Append(Clean(r.Message, 2000)).AppendLine();

        if (!string.IsNullOrEmpty(r.StackTrace)) sb.AppendLine(r.StackTrace);

        for (var i = 0; i < r.InnerExceptions.Count; i++)
        {
            sb.Append("  --- inner[").Append(i.ToString(CultureInfo.InvariantCulture)).Append("] ---").AppendLine();
            sb.AppendLine(r.InnerExceptions[i]);
        }

        if (r.Extra.Count > 0)
        {
            sb.Append("  extra: ");
            foreach (var pair in r.Extra)
            {
                sb.Append(pair.Key).Append('=').Append(Clean(pair.Value, 500)).Append("; ");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(r.Environment)) sb.Append("  env: ").AppendLine(r.Environment);

        return sb.ToString();
    }

    private static string FormatJson(ExceptionRecord r)
    {
        var sb = new StringBuilder(2048);
        var first = true;

        sb.Append('{');
        JsonField(sb, "ts", r.TimestampUtc.ToString("o", CultureInfo.InvariantCulture), ref first);
        JsonField(sb, "severity", r.Severity, ref first);
        JsonField(sb, "event", r.EventId, ref first);
        JsonField(sb, "plugin", r.PluginName, ref first);
        JsonField(sb, "version", r.PluginVersion, ref first);
        JsonField(sb, "context", r.Context, ref first);

        if (r.PlayerId.HasValue) JsonNumber(sb, "player", (long)r.PlayerId.Value, ref first);
        else JsonNull(sb, "player", ref first);

        JsonNumber(sb, "thread", r.ThreadId, ref first);

        if (r.IsMainThread.HasValue) JsonBool(sb, "main", r.IsMainThread.Value, ref first);
        else JsonNull(sb, "main", ref first);

        JsonField(sb, "type", r.ExceptionType, ref first);
        JsonField(sb, "message", r.Message, ref first);
        JsonField(sb, "stack", r.StackTrace, ref first);

        if (!first) sb.Append(',');
        first = false;
        sb.Append("\"inner\":[");
        for (var i = 0; i < r.InnerExceptions.Count; i++)
        {
            if (i > 0) sb.Append(',');
            JsonString(sb, r.InnerExceptions[i]);
        }
        sb.Append(']');

        JsonField(sb, "fp", r.Fingerprint, ref first);
        JsonNumber(sb, "repeats", r.RepeatCount, ref first);
        JsonNumber(sb, "dropped", r.DroppedTotal, ref first);
        JsonField(sb, "env", r.Environment, ref first);
        sb.Append('}');

        return sb.ToString();
    }

    private static void JsonField(StringBuilder sb, string name, string? value, ref bool first)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"').Append(name).Append("\":");
        JsonString(sb, value);
    }

    private static void JsonNumber(StringBuilder sb, string name, long value, ref bool first)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"').Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void JsonBool(StringBuilder sb, string name, bool value, ref bool first)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"').Append(name).Append("\":").Append(value ? "true" : "false");
    }

    private static void JsonNull(StringBuilder sb, string name, ref bool first)
    {
        if (!first) sb.Append(',');
        first = false;
        sb.Append('"').Append(name).Append("\":null");
    }

    private static void JsonString(StringBuilder sb, string? value)
    {
        sb.Append('"');

        if (!string.IsNullOrEmpty(value))
        {
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
        }

        sb.Append('"');
    }

    private static string Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var text = value!.Replace("\r", "\\r").Replace("\n", "\\n");

        return text.Length <= max ? text : text.Substring(0, max) + "...";
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "plugin";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return sb.ToString();
    }

    private sealed class FingerprintState
    {
        public DateTime LastWritten = DateTime.MinValue;
        public int Suppressed;
    }

    private sealed class Sink : IDisposable
    {
        private readonly string _directory;
        private readonly string _baseName;
        private readonly string _extension;
        private readonly int _maxBytes;

        private StreamWriter? _writer;
        private long _bytes;
        private DateTime _date = DateTime.MinValue;

        public Sink(string directory, string baseName, string extension, int maxBytes)
        {
            _directory = directory;
            _baseName = baseName;
            _extension = extension;
            _maxBytes = maxBytes > 0 ? maxBytes : 8 * 1024 * 1024;
        }

        public void Write(string line)
        {
            EnsureWriter();
            _writer!.WriteLine(line);

            _bytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

            if (_bytes >= _maxBytes) CloseWriter();
        }

        private void EnsureWriter()
        {
            var today = DateTime.UtcNow.Date;

            if (_writer != null && _date == today) return;

            CloseWriter();
            _date = today;

            System.IO.Directory.CreateDirectory(_directory);

            var index = 0;
            string path;

            do
            {
                var suffix = index == 0 ? string.Empty : "." + index.ToString(CultureInfo.InvariantCulture);
                path = Path.Combine(_directory,
                    _baseName + "-" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + suffix + _extension);
                index++;
            }
            while (File.Exists(path) && new FileInfo(path).Length >= _maxBytes);

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _bytes = stream.Length;
        }

        private void CloseWriter()
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _bytes = 0;
        }

        public void Dispose() => CloseWriter();
    }

    internal sealed class ExceptionRecord
    {
        public DateTime TimestampUtc;
        public string Severity = "ERROR";
        public string EventId = "PLUGIN.ERROR";
        public string PluginName = "";
        public string PluginVersion = "";
        public string Context = "";
        public ulong? PlayerId;
        public int ThreadId;
        public bool? IsMainThread;
        public string ExceptionType = "";
        public string Message = "";
        public string StackTrace = "";
        public List<string> InnerExceptions = new List<string>();
        public Dictionary<string, string> Extra = new Dictionary<string, string>(StringComparer.Ordinal);
        public string Fingerprint = "";
        public int RepeatCount = 1;
        public long DroppedTotal;
        public string Environment = "unknown";

        public static ExceptionRecord Create(
            string eventId,
            string context,
            Exception exception,
            string pluginName,
            string pluginVersion,
            ulong? playerId,
            IDictionary<string, string>? extra,
            string environment,
            string severity,
            bool? isMainThread)
        {
            exception ??= new Exception("Unknown exception");

            var record = new ExceptionRecord
            {
                TimestampUtc = DateTime.UtcNow,
                Severity = string.IsNullOrEmpty(severity) ? "ERROR" : severity,
                EventId = string.IsNullOrEmpty(eventId) ? "PLUGIN.ERROR" : eventId,
                PluginName = pluginName ?? "plugin",
                PluginVersion = pluginVersion ?? "?",
                Context = context ?? string.Empty,
                PlayerId = playerId,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                IsMainThread = isMainThread,
                ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                Message = exception.Message ?? string.Empty,
                StackTrace = exception.StackTrace ?? string.Empty,
                Environment = environment ?? "unknown"
            };

            record.Fingerprint = ComputeFingerprint(record.ExceptionType, record.Message, FirstFrame(record.StackTrace));

            var inner = exception.InnerException;
            while (inner != null && record.InnerExceptions.Count < 5)
            {
                record.InnerExceptions.Add(
                    (inner.GetType().FullName ?? inner.GetType().Name) + ": " + inner.Message +
                    System.Environment.NewLine + (inner.StackTrace ?? string.Empty));
                inner = inner.InnerException;
            }

            if (extra != null)
            {
                foreach (var pair in extra)
                {
                    if (pair.Key == null) continue;
                    record.Extra[pair.Key] = pair.Value ?? string.Empty;
                }
            }

            return record;
        }

        private static string FirstFrame(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return string.Empty;

            var lines = stack!.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? string.Empty : lines[0].Trim();
        }

        private static string ComputeFingerprint(string type, string message, string topFrame)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(type + "|" + message + "|" + topFrame);
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(16);

                for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));

                return sb.ToString();
            }
        }
    }
}
```

> **Note:** `ExceptionRecord` is nested and `internal` so the file is self-contained. If you split
> files, make it `internal sealed class` in the namespace.

### 9.2 `PluginGuard.cs`

```csharp
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MyPlugin.Infrastructure;

/// <summary>
/// Last-chance observers. This is a net, not a force field:
/// AppDomain.UnhandledException cannot prevent process termination.
/// </summary>
public sealed class PluginGuard : IDisposable
{
    private readonly string _pluginName;
    private readonly ExceptionLog? _log;
    private int _handling;
    private int _installed;

    public PluginGuard(string pluginName, ExceptionLog? log)
    {
        _pluginName = pluginName ?? "plugin";
        _log = log;
    }

    public void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        Application.logMessageReceivedThreaded += OnUnityLog;

#if CARBON
        Carbon.Logger.OnErrorCallback += OnCarbonError;
#endif
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 0) return;

#if CARBON
        try { Carbon.Logger.OnErrorCallback -= OnCarbonError; } catch { }
#endif

        try { Application.logMessageReceivedThreaded -= OnUnityLog; } catch { }
        try { TaskScheduler.UnobservedTaskException -= OnUnobservedTask; } catch { }
        try { AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandled; } catch { }
    }

    private void OnAppDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception
            ?? new Exception(Convert.ToString(e.ExceptionObject) ?? "Unknown unhandled exception");

        // No locks, no queue, no Unity/Carbon calls: the process may be terminating.
        try { _log?.WriteCrash("PLUGIN.UNHANDLED", exception, e.IsTerminating); }
        catch { }
    }

    private void OnUnobservedTask(object sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();

        var exception = e.Exception?.Flatten() ?? new Exception("Unknown unobserved task exception");

        if (!IsOurs(exception.ToString()) && !IsOurs(exception.Source)) return;

        try { _log?.Error("PLUGIN.UNOBSERVED", "TaskScheduler.UnobservedTaskException", exception); }
        catch { }
    }

    private void OnUnityLog(string condition, string stackTrace, LogType type)
    {
        // Carbon mirrors LogType.Error via Logger.OnErrorCallback. Capture raw Unity exceptions
        // (and asserts) that bypass plugin boundaries.
        if (type != LogType.Exception && type != LogType.Assert) return;
        if (!IsOurs(condition) && !IsOurs(stackTrace)) return;

        if (Interlocked.Exchange(ref _handling, 1) != 0) return;

        try
        {
            var extra = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(stackTrace)) extra["unity_stack"] = stackTrace;

            _log?.Error("PLUGIN.UNITY", "Unity log: " + condition, new Exception(condition), null, extra);
        }
        catch { }
        finally
        {
            Volatile.Write(ref _handling, 0);
        }
    }

#if CARBON
    private void OnCarbonError(string message, Exception exception, int verbosity)
    {
        if (!IsOurs(message)) return;

        try { _log?.Error("PLUGIN.CARBON", message, exception ?? new Exception(message)); }
        catch { }
    }
#endif

    private bool IsOurs(string? text)
    {
        return !string.IsNullOrEmpty(text) &&
               text!.IndexOf(_pluginName, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
```

> **Carbon symbol:** `#if CARBON` is defined by Carbon's compiler (default
> `ConditionalCompilationSymbols`). If you remove it, the guard silently loses Carbon's mirrored
> errors. The HarmonyMod build does not define it and does not reference Carbon.

### 9.3 `Safe.cs`

```csharp
#nullable enable
using System;
using System.Threading.Tasks;

namespace MyPlugin.Infrastructure;

/// <summary>
/// Boundary guards. Every entry point should go through one of these.
/// </summary>
public static class Safe
{
    public static void Run(
        string context,
        Action action,
        ExceptionLog? log,
        Action<Exception>? onError = null)
    {
        try
        {
            action();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown/cancellation; not an error.
        }
        catch (Exception ex)
        {
            Report(context, ex, log, onError);
        }
    }

    public static T Run<T>(
        string context,
        Func<T> func,
        T fallback,
        ExceptionLog? log,
        Action<Exception>? onError = null)
    {
        try
        {
            return func();
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception ex)
        {
            Report(context, ex, log, onError);
            return fallback;
        }
    }

    public static async Task<bool> RunAsync(
        string context,
        Func<Task> action,
        ExceptionLog? log,
        Action<Exception>? onError = null)
    {
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Report(context, ex, log, onError);
            return false;
        }
    }

    public static async Task<T> RunAsync<T>(
        string context,
        Func<Task<T>> action,
        T fallback,
        ExceptionLog? log,
        Action<Exception>? onError = null)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            return fallback;
        }
        catch (Exception ex)
        {
            Report(context, ex, log, onError);
            return fallback;
        }
    }

    /// <summary>Observed fire-and-forget: RunAsync catches, so the task never faults unobserved.</summary>
    public static void FireAndForget(
        string context,
        Func<Task> action,
        ExceptionLog? log,
        Action<Exception>? onError = null)
    {
        _ = RunAsync(context, action, log, onError);
    }

    /// <summary>Run every cleanup step even if one throws.</summary>
    public static void Unload(string context, ExceptionLog? log, params Action[] cleanups)
    {
        if (cleanups == null) return;

        for (var i = 0; i < cleanups.Length; i++)
        {
            try
            {
                cleanups[i]?.Invoke();
            }
            catch (Exception ex)
            {
                try { log?.Error("PLUGIN.UNLOAD", context + "[" + i + "]", ex); }
                catch { }
            }
        }
    }

    private static void Report(
        string context,
        Exception exception,
        ExceptionLog? log,
        Action<Exception>? onError)
    {
        try { log?.Error("PLUGIN.BOUNDARY", context, exception); }
        catch { }

        try { onError?.Invoke(exception); }
        catch { }
    }
}
```

> `Safe` deliberately does **not** call `Carbon.Logger` itself. Pass an `onError` callback that calls
> `LogError` (Carbon plugin) or `Debug.LogError` (HarmonyMod). This keeps the helper portable and
> prevents recursion into the guard.

---

## 10. Carbon plugin integration

```csharp
using System;
using System.IO;
using Carbon;
using Carbon.Extensions;
using Carbon.Plugins;
using MyPlugin.Infrastructure;
using Oxide.Core.Plugins;

namespace Carbon.Plugins;

[Info("MyPlugin", "You", "1.0.0")]
[Description("Example: guarded plugin with a separate exception log.")]
public class MyPlugin : CarbonPlugin
{
    private ExceptionLog? _exceptions;
    private PluginGuard? _guard;

    private void Loaded()
    {
        // 1) Log first, so every later failure is captured.
        _exceptions = new ExceptionLog(
            Title,
            Version.ToString(),
            Path.Combine(Defines.GetLogsFolder(), Title),
            environment: BuildEnvironment(),
            isMainThread: () => ThreadEx.IsOnMainThread());

        // 2) Guard before any feature code.
        _guard = new PluginGuard(Title, _exceptions);
        _guard.Install();

        // 3) Now it is safe to initialize.
        Safe.Run("Loaded", () =>
        {
            permission.RegisterPermission("myplugin.use", this);
            Puts($"v{Version} loaded; exceptions -> {_exceptions.Directory}");
        }, _exceptions, OnSafeError);
    }

    private void Unload()
    {
        Safe.Unload("Unload", _exceptions,
            () => _guard?.Dispose(),
            () => _exceptions?.Dispose());

        _guard = null;
        _exceptions = null;
    }

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

    private object OnEntityDeath(BasePlayer player, HitInfo info)
    {
        return Safe.Run("OnEntityDeath", () =>
        {
            if (player == null || info == null) return null;
            // feature work
            return null;
        }, fallback: null, _exceptions, OnSafeError);
    }

    private void OnSafeError(Exception ex)
    {
        // One line to Carbon's console/log. The exception log already has the full record.
        LogError($"[Safe] {ex.GetType().Name}: {ex.Message}", ex);
    }

    private static string BuildEnvironment()
    {
        try
        {
            return "carbon=" + (Community.Runtime?.Analytics?.Version?.ToString() ?? "unknown") +
                   ";unity=" + UnityEngine.Application.unityVersion;
        }
        catch
        {
            return "unknown";
        }
    }
}
```

### 10.1 Notes

- `Defines.GetLogsFolder()` is `carbon/logs`; the final directory is
  `carbon/logs/<Plugin>/`.
- `Title`/`Version` come from `[Info]`; `Version.ToString()` is safe.
- `ThreadEx` is `Carbon.Extensions.ThreadEx`; it compares against the main thread captured at
  startup.
- `Community.Runtime?.Analytics?.Version` should be read on the main thread; if it is unavailable
  during `Loaded`, the environment string says `unknown` — that is acceptable.
- Lifecycle order (verified): `Init` hook → `OnLoaded` → `Loaded` → the `Load()` virtual. Wiring in
  `Loaded()` therefore covers everything after it. If you also override `Load()`, wrap its body in
  `Safe.Run(..., _exceptions, OnSafeError)`: it is a direct call, not a guarded hook (see §3).
- If `new ExceptionLog(...)` itself throws, the plugin would fail to load; wrap it in a try/catch and
  fall back to `null` only if you prefer degraded logging:
  ```csharp
  try { _exceptions = new ExceptionLog(...); } catch (Exception ex) { LogError("exception log init failed", ex); }
  ```
  Do **not** let logging initialization be the reason the plugin cannot run.

### 10.2 `.cszip`

Include `ExceptionLog.cs`, `PluginGuard.cs`, `Safe.cs` in the archive with the same namespace, or
paste them into the primary file. Carbon compiles all `.cs` entries together. Keep the helper
namespace stable so the primary partial class can `using` it.

### 10.3 `ICarbonExtension` DLLs

An extension has no `CarbonPlugin` lifecycle; wire the same infrastructure in its addon callbacks:

```csharp
using System;
using System.IO;
using API.Assembly;
using Carbon;
using Carbon.Extensions;
using MyPlugin.Infrastructure;

public sealed class MyExtension : ICarbonExtension
{
    private ExceptionLog? _exceptions;
    private PluginGuard? _guard;

    public void Awake(EventArgs args) { }

    public void OnLoaded(EventArgs args)
    {
        try
        {
            _exceptions = new ExceptionLog(
                "MyExtension", "1.0.0",
                Path.Combine(Defines.GetLogsFolder(), "MyExtension"),
                environment: "extension",
                isMainThread: () => ThreadEx.IsOnMainThread());

            _guard = new PluginGuard("MyExtension", _exceptions);
            _guard.Install();
        }
        catch (Exception ex)
        {
            // Nothing is wired yet: console only, and keep the extension inert.
            try { Carbon.Logger.Error("[MyExtension] OnLoaded failed", ex); } catch { }
        }
    }

    public void OnUnloaded(EventArgs args)
    {
        Safe.Unload("OnUnloaded", _exceptions,
            () => _guard?.Dispose(),
            () => _exceptions?.Dispose());
    }
}
```

- Extensions are loaded into the same process; a per-extension log directory keeps ownership clear.
- `ICarbonExtension` derives from `ICarbonAddon` (`Awake`, `OnLoaded`, `OnUnloaded`, all taking
  `EventArgs`), and the loader does not wrap those calls — guard them like any other boundary.
- Extensions are compiled outside Carbon's script pipeline. If the project references the Carbon
  assemblies, define the `CARBON` symbol so `PluginGuard` also subscribes to
  `Carbon.Logger.OnErrorCallback`; without it the other three observers still work.
- If the extension and the plugin are separate assemblies, either reference one shared
  infrastructure assembly or compile the three files into each. Log files never collide because the
  directory and file names come from the plugin/mod name.
- If the extension is shared by several plugins, include the caller/plugin name in `context` rather
  than creating one log per caller.
- The same rules apply: guard public entry points, never throw from `OnLoaded`/`OnUnloaded`, and
  dispose in reverse order.

### 10.4 Other boundaries

- **Timers:** `timer.Every(1f, () => Safe.Run("timer:x", Tick, _exceptions, OnSafeError));`
- **Tasks:** `Safe.FireAndForget("task:x", () => WorkAsync(ct), _exceptions, OnSafeError);`
- **CUI callbacks:** they arrive as commands; guard the command handler.
- **Bridge:** guard each message handler; validate schema before acting.
- **Data:** guard `ReadObject`/`WriteObject`; keep a `.bak`; do not overwrite a corrupt file with
  defaults until you have logged/backed it up.

---

## 11. HarmonyMod integration

### 11.1 Paths

HarmonyMods are loaded from a `byte[]`, so `Assembly.Location` is empty. Derive the server root from
the game assembly:

```csharp
internal static class ModPaths
{
    public static string GetLogDirectory(string modName)
    {
        try
        {
            var gameAssembly = typeof(BasePlayer).Assembly.Location;
            var managed = Path.GetDirectoryName(gameAssembly);

            if (!string.IsNullOrEmpty(managed))
            {
                var root = Path.GetFullPath(Path.Combine(managed, "..", ".."));
                return Path.Combine(root, "HarmonyMods_Data", modName, "logs");
            }
        }
        catch { }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "HarmonyMods_Data", modName, "logs");
    }
}
```

Final path: `HarmonyMods_Data/<Mod>/logs/<Mod>_exceptions-YYYY-MM-DD.log`. This survives Carbon
unload and is independent of `carbon/logs`.

### 11.2 Entry point

```csharp
#nullable enable
using System;
using System.IO;
using HarmonyLib;
using MyPlugin.Infrastructure;
using UnityEngine;

public sealed class MyMod : IHarmonyModHooks
{
    internal static ExceptionLog? Log;
    private static PluginGuard? _guard;

    public void OnLoaded(OnHarmonyModLoadedArgs args)
    {
        Log = new ExceptionLog(
            "MyMod",
            "1.0.0",
            ModPaths.GetLogDirectory("MyMod"),
            environment: "unity=" + Application.unityVersion);

        _guard = new PluginGuard("MyMod", Log);
        _guard.Install();

        Safe.Run("OnLoaded", () =>
        {
            // initialization; may throw
        }, Log, ex => Debug.LogError("[MyMod] " + ex));
    }

    public void OnUnloaded(OnHarmonyModUnloadedArgs args)
    {
        Safe.Unload("OnUnloaded", Log,
            () => _guard?.Dispose(),
            () => Log?.Dispose());

        _guard = null;
        Log = null;
    }

    internal static void LogException(string context, Exception exception)
    {
        try { Log?.Error("PLUGIN.PATCH", context, exception); } catch { }
        try { Debug.LogError("[MyMod] " + context + ": " + exception); } catch { }
    }
}
```

### 11.3 Patch template

```csharp
[HarmonyPatch(typeof(BasePlayer), nameof(BasePlayer.CanSuicide))]
public static class NoSuicidePatch
{
    public static bool Prefix(ref bool __result)
    {
        try
        {
            __result = false;
            return false;
        }
        catch (Exception ex)
        {
            MyMod.LogException("CanSuicide.Prefix", ex);
            return true; // fail open; choose false for security-critical patches
        }
    }

    public static Exception Finalizer(Exception __exception)
    {
        if (__exception != null) MyMod.LogException("CanSuicide.Finalizer", __exception);
        return __exception; // rethrow; return null to suppress
    }
}
```

- The finalizer makes Harmony wrap the original and all patches in try/catch; it is the only
  built-in observation point.
- If a prefix throws and you have no finalizer, the exception enters the game method's caller.
- Wrap `PatchAll`/`PatchClassProcessor.Patch()` in try/catch; log the target method and unpatch on
  failure.

### 11.4 Hybrid Carbon + HarmonyMod

- Put shared rules/helpers in a common namespace (`MyPlugin.Infrastructure`).
- Carbon plugin and HarmonyMod each create their **own** `ExceptionLog` and `PluginGuard` (different
  directories: `carbon/logs/MyPlugin` vs `HarmonyMods_Data/MyMod/logs`).
- Do not share a single static log across two assemblies loaded in the same process unless you
  control their load order; duplicate/disposed writers are a real risk.
- Use distinct Harmony IDs and patch targets to avoid double patching.

---

## 12. Testing exception handling

### 12.1 Fault injection

Add a debug-only command (config `Debug = true`, `[AuthLevel(3)]`) that throws at each boundary:

```csharp
[ConsoleCommand("myplugin.debug.throw")]
[AuthLevel(3)]
private void DebugThrow(ConsoleSystem.Arg arg)
{
    if (!_config.Debug) return;

    var boundary = arg.GetString(0, "hook");

    switch (boundary)
    {
        case "hook":     Safe.Run("debug:hook", () => throw new InvalidOperationException("hook test"), _exceptions, OnSafeError); break;
        case "command":  throw new InvalidOperationException("command test"); // Carbon catches; no user reply
        case "timer":    timer.Once(0.1f, () => Safe.Run("debug:timer", () => throw new InvalidOperationException("timer test"), _exceptions, OnSafeError)); break;
        case "task":     Safe.FireAndForget("debug:task", () => Task.Run(() => throw new InvalidOperationException("task test")), _exceptions, OnSafeError); break;
        case "thread":   new Thread(() => Safe.Run("debug:thread", () => throw new InvalidOperationException("thread test"), _exceptions, OnSafeError)) { IsBackground = true }.Start(); break;
        default:         arg.ReplyWith("hook|command|timer|task|thread"); break;
    }
}
```

### 12.2 Test matrix

| Test | Expected |
|---|---|
| Throw in a wrapped hook | Hook skipped; one exception record; no crash; vanilla continues. |
| Throw in a command | Carbon logs; your wrapper replies safely; record written. |
| Throw in a repeating timer | Timer keeps firing (wrapped); record written once per dedupe window. |
| Throw in a task | No unobserved termination; record written; loop exits cleanly. |
| Throw in a Harmony prefix | Finalizer logs; game continues; no state corruption. |
| `Task.Run(() => throw)` never awaited | `UnobservedTaskException` fires after GC; record written if it belongs to the plugin. |
| Read-only log directory | Plugin continues; console warning; no exception from logger. |
| 100 threads logging concurrently | No corruption; bounded queue; `dropped` reflects pressure. |
| Log file exceeds cap | New `.1` file created; old file retained until retention. |
| Unload with a throwing cleanup | Remaining cleanup actions still run; record written. |
| Restart | New dated file; old files retained per policy. |

### 12.3 Verify the log, not just the console

```bash
tail -n 40 carbon/logs/MyPlugin/MyPlugin_exceptions-$(date -u +%F).log
jq -c 'select(.event=="PLUGIN.TIMER")' carbon/logs/MyPlugin/MyPlugin_exceptions-$(date -u +%F).jsonl | tail
```

A test that only checks "server didn't crash" misses swallowed feature failures.

### 12.4 Analyzers and CI

Enable:

- CA1031 — do not catch general exception types (allow at boundaries with justification),
- CA2200 — rethrow to preserve stack,
- CA2000 — dispose objects before losing scope,
- CA1062 — validate arguments of public methods,
- CA2016 — forward the cancellation token,
- SonarAnalyzer S2221/S2139 — exception handling/logging rules.

`TreatWarningsAsErrors=true` in CI; suppress only with a documented justification at the boundary.

---

## 13. Review and acceptance checklist

### 13.1 Code

- [ ] Every hook, command, timer, task, patch, bridge handler and unload step is guarded.
- [ ] `PluginGuard` installed before feature init; disposed before the log.
- [ ] `ExceptionLog` writes to a **separate plugin directory**, not just `Carbon.Core.log`.
- [ ] Fingerprint/dedupe/rate limit/queue cap implemented and tested.
- [ ] Rotation, retention and crash-file path verified.
- [ ] Logger never throws; read-only/full-disk behaviour tested.
- [ ] No secrets/PII in messages, context or extras.
- [ ] CR/LF sanitized in single-line fields; JSON escaped.
- [ ] No raw unguarded threads; no `async void` outside handlers.
- [ ] Harmony patches have try/catch and (where needed) a finalizer.
- [ ] `Unload` runs all cleanup even if one step fails.
- [ ] Cancellation treated as normal shutdown, not an error.

### 13.2 Operations

- [ ] `chmod 750` directory / `chmod 640` files; server user owns them.
- [ ] Logs not served by a web panel/CDN.
- [ ] Retention documented and aligned with privacy policy.
- [ ] Failure-injection test performed on the target Linux/Carbon build.
- [ ] Feedback packet includes fingerprint + record + console excerpt.
- [ ] After a Rust update, re-test the guard and patch targets.

### 13.3 Definition of done for an exception fix

- [ ] Root cause identified from a full record (not just the message).
- [ ] The failing boundary is guarded; the fallback is intentional.
- [ ] A regression test/fault injection reproduces the original path.
- [ ] The log record contains enough context to diagnose a recurrence.
- [ ] No new noise (dedupe prevents per-tick spam).

---

## 14. Sources

Checked 2026-10-17.

### .NET exception handling

- Microsoft Learn, *Best practices for exceptions* — catch only what you can handle; `throw;` vs
  `throw ex;`; `ExceptionDispatchInfo`; "a crashed app is more reliable and diagnosable than an app
  with undefined behavior."
  <https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions>
- Microsoft Learn, *AppDomain.UnhandledException* — notification of uncaught exceptions; the system
  default handler terminates the application; handlers run while locks may be held; not raised for
  corrupted-state exceptions such as stack overflow/access violation.
  <https://learn.microsoft.com/en-us/dotnet/api/system.appdomain.unhandledexception>
- Microsoft Learn, *TaskScheduler.UnobservedTaskException* — prevents exception escalation policy;
  since .NET 4.5 the process does not terminate by default; `SetObserved()` marks it handled.
  <https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.taskscheduler.unobservedtaskexception>
- Microsoft Learn, *ExceptionDispatchInfo* — capture and rethrow with the original stack.
  <https://learn.microsoft.com/en-us/dotnet/api/system.runtime.exceptionservices.exceptiondispatchinfo>
- Microsoft Learn, CA1031 (do not catch general exception types), CA2200 (rethrow to preserve
  stack), CA2000 (dispose before losing scope).
  <https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca1031>
  <https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/quality-rules/ca2200>

### Logging and security

- OWASP, *Logging Cheat Sheet* — log security-relevant events; exclude secrets/PII; sanitize CR/LF
  to prevent log injection; logging failure must not stop the application; verify retention,
  access control, resource exhaustion and failure modes.
  <https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html>
- OWASP, *Top 10:2025* A09 — Security Logging and Alerting Failures.
  <https://owasp.org/Top10/2025/>
- Serilog best practices — structured message templates, exception object, no string
  interpolation. <https://serilog.net/>
- Microsoft Learn, *.NET logging guidance* — categories, levels, scopes, high-performance logging.
  <https://learn.microsoft.com/en-us/dotnet/core/extensions/logging>

### Carbon (source verified)

- `src/Carbon/src/Hooks/HookCallerInternal.cs` — hook try/catch + log, no rethrow.
- `src/Carbon.Components/Carbon.Common/src/Oxide/Command.cs` — chat/console command try/catch,
  `[callback]`/`[internal]`, `TargetParameterCountException`.
- `src/Carbon.Components/Carbon.Common/src/Oxide/Libraries/Timer.Scheduler.cs` — timer exception
  catch + destroy.
- `src/Carbon.Components/Carbon.Common/src/Oxide/RustPlugin.cs` — `ILoadConfig` catch, `LogToFile`,
  `Puts`/`LogError`.
- `src/Carbon.Components/Carbon.Common/src/Carbon/Components/Logger.cs` — `InitTaskExceptions`,
  `OnErrorCallback`, severity handling.
- `src/Carbon.Components/Carbon.Common/src/Carbon/Components/FileLogger.cs` — `carbon/logs`,
  archive, split size, `QueueLog`.
- `src/Carbon/src/Community.cs` — `Logger.InitTaskExceptions()` at startup.
- All Carbon paths read from `main` on 2026-10-17; see `RESEARCH_REVIEW.MD` for the audit.

### Harmony

- Harmony 2 docs, *Finalizer* — a finalizer makes Harmony wrap the original and all other patches in
  a try/catch; `__exception`; return `null` to suppress.
  <https://harmony.pardeike.net/v2/articles/patching-finalizer.html>
- Harmony source, `Harmony/Internal/MethodPatcher.cs` at tag `v2.3.6.0` — no catch around
  prefix/postfix invocation unless finalizers are present.
  <https://github.com/pardeike/Harmony/blob/v2.3.6.0/Harmony/Internal/MethodPatcher.cs>

### Unity

- Unity 6, `Application.logMessageReceivedThreaded` — fires on any thread; handler must be
  thread-safe; do not call Unity APIs from it; no need to subscribe to both log events.
  <https://docs.unity3d.com/ScriptReference/Application-logMessageReceivedThreaded.html>

> **Recency:** Microsoft/OWASP/Unity/Harmony docs are maintained; Carbon/Harmony runtime behaviour
> must be re-verified after each Rust forced update (first Thursday monthly). The Carbon paths above
> are source-verified for `main` on 2026-10-17, not a guarantee for older/newer builds.

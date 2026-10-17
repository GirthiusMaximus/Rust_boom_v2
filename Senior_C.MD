# Senior C# for Rust Server Plugins

> **Purpose.** The language, engineering, security, database and architecture knowledge a senior C#
> developer needs to build production Rust server plugins — and the judgement to know which modern
> .NET advice does **not** apply to Unity/Mono.
>
> **Target runtime:** Rust dedicated server = Unity 6 + Mono, .NET Framework 4.8 API surface +
> netstandard 2.1, single-threaded game loop. Carbon compiles plugins with Roslyn
> `LanguageVersion.Preview`, but the **runtime** is still 4.8. C# 10 is the safe language baseline.
>
> **Companions:** `CarbonMod.MD` (framework), `Rust.MD` (client/server), `CARBON_PLUGIN_PLAYBOOK.MD`
> (process), `Exceptions.md` (exception architecture + separate error log),
> `DontJustRollWithIt.MD` (evidence/collaboration protocol), `RESEARCH_REVIEW.MD` (source dates).

---

## Table of contents

0. [Runtime reality and feature availability](#0-runtime-reality-and-feature-availability)
1. [Idiomatic C# and style](#1-idiomatic-c-and-style)
2. [Testing](#2-testing)
3. [Design patterns](#3-design-patterns)
4. [SOLID](#4-solid)
5. [Advanced async](#5-advanced-async)
6. [Multithreading and concurrency](#6-multithreading-and-concurrency)
7. [Secure coding](#7-secure-coding)
8. [Advanced databases](#8-advanced-databases)
9. [Software architecture](#9-software-architecture)
10. [Distributed systems](#10-distributed-systems)
11. [Review checklists](#11-review-checklists)
12. [Sources](#12-sources)

---

## 0. Runtime reality and feature availability

### 0.1 Why this matters

Most current C# guidance assumes .NET 8/9/10, nullable reference types, `System.Threading.Lock`,
`IAsyncEnumerable` LINQ operators, EF Core 10 and modern ASP.NET. A Rust plugin runs on **Mono with
the .NET Framework 4.8 BCL**. Code that compiles in a modern console app may fail to load or throw
`TypeLoadException`/`MissingMethodException` in-game.

The compiler is not the constraint — Carbon parses with `LanguageVersion.Preview` and builds against
net48. The **runtime API surface** and **polyfills** are the constraint.

### 0.2 Availability matrix

| Feature | Available in plugin? | Guidance |
|---|---|---|
| C# 7–10 syntax (patterns, switch expressions, using declarations, nullable annotations, records with polyfill) | ✅ mostly | C# 10 is the safe baseline; records/`init` need `IsExternalInit`. |
| C# 11–14 syntax (`required`, raw strings, collection expressions, primary constructors, `field`) | ⚠️ compiler-dependent | Test in-game; runtime attributes/polyfills may be missing. |
| `System.Threading.Lock` (C# 13 / .NET 9) | ❌ | Use `private readonly object _gate = new();` + `lock`. |
| `TimeProvider` (.NET 8) | ❌ | Inject an `IClock` abstraction you own. |
| `FrozenDictionary`/`FrozenSet` (.NET 8) | ❌ | `Dictionary`/`HashSet` with capacity. |
| `Parallel.ForEachAsync` (.NET 6) | ❌ | `SemaphoreSlim` + `Task.WhenAll`. |
| `Task.WaitAsync` (.NET 6) | ❌ | `Task.WhenAny(task, Task.Delay(timeout, ct))`. |
| `System.Text.Json` | ❌ (not in-box) | Use Newtonsoft.Json (Carbon ships it). |
| `IAsyncEnumerable<T>` / `await foreach` | ⚠️ needs `Microsoft.Bcl.AsyncInterfaces` | Verify in-game; otherwise return `Task<List<T>>`. |
| `ValueTask` | ⚠️ needs `System.Threading.Tasks.Extensions` | Carbon provides some support; verify before public APIs. |
| `Span<T>`/`Memory<T>` | ⚠️ needs `System.Memory` | Useful in parsers; verify assembly presence. |
| EF Core 10 | ❌ | Requires .NET 10. Use for companion services only. |
| `HttpClient` | ✅ | Reuse a static instance; never per request. |
| `System.Threading.Channels` | ⚠️ NuGet package | Only if you can ship/verify the assembly; otherwise `BlockingCollection`. |
| `System.Collections.Concurrent` | ✅ | `ConcurrentDictionary`, `ConcurrentQueue`, `BlockingCollection`. |
| `ReaderWriterLockSlim`, `SemaphoreSlim`, `Interlocked` | ✅ | Core concurrency toolkit. |
| `Task`, `async/await`, `CancellationToken` | ✅ | The async baseline. |
| `System.Diagnostics.Stopwatch`, `Activity` | ✅/⚠️ | Stopwatch always; `Activity` needs DiagnosticSource assembly. |

### 0.3 The modern-.NET horizon (context only)

- .NET 10 is the current LTS (released Nov 2025, supported to Nov 2028); C# 14.
- .NET 11 / C# 15 is in preview/RC through 2026, GA November 2026 (union types, closed hierarchies,
  extension indexers, collection-expression arguments, labeled `break`/`continue`).
- **None of this runs in a Rust plugin.** Do not let a tutorial written for .NET 10 dictate your
  plugin code; use it for companion web services, tooling and tests.

### 0.4 The two-codebase rule

Write plugins as if they were a **net48 library**:

1. `MyPlugin.Core` — `netstandard2.0`/`net48`, no Unity/Rust references, unit-testable.
2. `MyPlugin` — Carbon plugin adapters (hooks, CUI, config, permissions).
3. Optional companion service — `net8.0`/`net10.0`, where EF Core 10, minimal APIs, Serilog and
   OpenTelemetry are appropriate.

---

## 1. Idiomatic C# and style

### 1.1 Principles

- **Clarity over cleverness.** Optimize for the next reader (often you at 3 a.m. after a wipe).
- **Small, named units.** A method should read like a sentence; a class should have one reason to
  change.
- **Explicit over implicit.** Avoid magic strings, hidden global state and action-at-a-distance.
- **Fail fast, fail loud (in development), fail safe (in production).** Validate at boundaries;
  degrade gracefully at runtime.
- **Immutability by default.** `readonly`, `const`, get-only properties; mutate only where needed.
- **Composition over inheritance.** Prefer small interfaces and delegation to deep hierarchies.
- **No surprises in property getters.** Getters should be cheap, side-effect free and non-throwing.

### 1.2 Naming

| Element | Convention | Example |
|---|---|---|
| Type, method, property, event | PascalCase | `DamageCalculator`, `TryConsume` |
| Interface | `I` + PascalCase | `IItemService` |
| Private field | `_camelCase` | `_config`, `_gate` |
| Parameter, local | camelCase | `playerId`, `elapsed` |
| Constant | PascalCase | `MaxRetries` |
| Type parameter | `T`, `TKey`, `TValue` | `Dictionary<TKey, TValue>` |
| Async method | `...Async` | `LoadAsync` |
| Boolean | `Is`/`Has`/`Can`/`Should` | `IsEnabled`, `CanBuild` |

- One type per file; file name = type name.
- Namespaces mirror folders; no `Utils`/`Helpers` dumping grounds — name the responsibility.
- Avoid abbreviations except domain-standard ones (`CUI`, `RPC`, `SteamID`).

### 1.3 Formatting

- 4 spaces, no tabs; braces on new lines (Allman) for C#; one statement per line.
- Max line length ~120; break long argument lists one per line.
- `using` directives outside the namespace, System first, then alphabetical.
- Blank line between members; group with `#region` only for very large partial classes (prefer files).
- `.editorconfig` + analyzers in CI; do not argue style in review.

### 1.4 Types and API design

- Prefer `sealed` for leaf classes; unseal deliberately.
- Prefer `readonly struct` for small immutable values; avoid mutable structs.
- Return interfaces (`IReadOnlyList<T>`) from public APIs; accept the narrowest type you need.
- Use `TryXxx` for expected failure, exceptions for exceptional failure.
- Never return `null` collections; return empty arrays/`Array.Empty<T>()`.
- Avoid `bool` parameters that change behaviour (`Load(true)`); use enums/options objects.
- Avoid `out`/`ref` in public APIs unless you are modelling `TryParse`.
- Keep constructors trivial; use factories for complex creation.
- Do not expose mutable collections; expose `IReadOnlyCollection<T>` or a copy.

### 1.5 Nullability

```csharp
#nullable enable

public sealed class KitService
{
    private readonly IKitStore _store;

    public KitService(IKitStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public Kit? Find(string name)
        => string.IsNullOrWhiteSpace(name) ? null : _store.Find(name);

    public Kit GetRequired(string name)
        => Find(name) ?? throw new InvalidOperationException($"Kit '{name}' does not exist.");
}
```

- Enable nullable in your project; annotate public APIs.
- Validate constructor arguments once; do not null-check the same field everywhere.
- On net48 the annotations are compile-time only (no runtime enforcement).

### 1.6 Error handling

```csharp
try
{
    await _repository.SaveAsync(order, cancellationToken);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    throw; // shutdown, not an error
}
catch (DbException ex)
{
    _logger.LogError(ex, "Failed to persist order {OrderId}", order.Id);
    throw new OrderPersistenceException(order.Id, ex);
}
```

Rules:

- Catch only what you can handle; never `catch (Exception) { }` with an empty body.
- Rethrow with `throw;`, not `throw ex;` (preserves stack); use
  `ExceptionDispatchInfo.Capture(ex).Throw()` if you must rethrow later.
- Wrap low-level exceptions in domain exceptions at boundaries; keep the original as `InnerException`.
- Unwrap `TargetInvocationException` (reflection) and `AggregateException.Flatten()` (tasks).
- Treat `OperationCanceledException` as shutdown/control flow, not an error.
- Do not use exceptions for control flow on hot paths; prefer `Try*`.
- Log once at the boundary; do not log and rethrow at every layer.
- Include context (IDs, operation), never secrets or full payloads.
- Never catch fatal/corrupted-state exceptions expecting recovery (`StackOverflowException`,
  `AccessViolationException`, OOM); log where possible and fail fast.
- A logger must never throw; a logging failure must not break the caller.

### 1.7 Comments and documentation

- Comment **why**, not **what**; the code says what.
- XML-doc public APIs and non-obvious invariants.
- Delete commented-out code; Git remembers.
- Keep TODO with an owner/issue: `// TODO(#123): remove after Rust 2026-11 update`.
- Document patch targets and Rust-version assumptions in HarmonyMods.

### 1.8 Disposable and resource ownership

```csharp
public sealed class SnapshotWriter : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    public SnapshotWriter(string path)
        => _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
```

- Own what you create; dispose in `Unload()`/`Dispose()`.
- Use `using`/`using var` for scoped resources.
- If a class owns multiple disposables, dispose in reverse creation order and tolerate partial
  construction.
- Do not dispose objects you do not own (e.g. a `BasePlayer`).

### 1.9 Exception architecture and logging

Think in three layers:

1. **Domain exceptions** — meaningful failures (`KitNotFoundException`,
   `InsufficientFundsException`). Thrown close to the rule; no logging here.
2. **Boundary handlers** — hooks/commands/timers/tasks/patches. Catch, log with context, choose
   fail-open/fail-closed, return the documented value. This is where the plugin stops the exception
   from reaching the host/game.
3. **Last-chance observers** — `AppDomain.UnhandledException`,
   `TaskScheduler.UnobservedTaskException`, Unity's log stream, Carbon's `Logger.OnErrorCallback`.
   These produce evidence and flush logs; they **cannot** reliably recover the process.

```csharp
// Domain: no logging, no host types.
public sealed class InsufficientFundsException : Exception
{
    public ulong PlayerId { get; }

    public InsufficientFundsException(ulong playerId, int required, int available)
        : base($"Player {playerId} needs {required}, has {available}.")
        => PlayerId = playerId;
}

// Boundary: catch, log, decide, return.
private object OnEntityDeath(BasePlayer player, HitInfo info)
{
    try
    {
        _economy.Charge(player.userID, _config.DeathFee);
        return null;
    }
    catch (InsufficientFundsException ex)
    {
        _exceptions.Error("PLUGIN.ECONOMY", "OnEntityDeath", ex, player.userID);
        return null; // fail open: do not block the death
    }
    catch (Exception ex)
    {
        _exceptions.Error("PLUGIN.HOOK", "OnEntityDeath", ex, player.userID);
        return null;
    }
}
```

Global handlers — know exactly what they give you:

| Handler | Gives you | Does **not** give you |
|---|---|---|
| `AppDomain.CurrentDomain.UnhandledException` | Notification + `IsTerminating`; last chance to write a crash file | Recovery; the process still terminates |
| `TaskScheduler.UnobservedTaskException` | Faulted task nobody awaited; call `SetObserved()` | The task's result; it is already lost |
| `Application.logMessageReceivedThreaded` | Unity-logged exceptions; handler must be thread-safe and must not call Unity APIs | A catch point; Unity already handled/logged it |
| `Carbon.Logger.OnErrorCallback` | Carbon-contained hook/command/timer/config errors | Errors Carbon never logs, or other plugins' errors unless filtered |
| `AppDomain.FirstChanceException` | Every throw, before handlers | A stable production signal; extremely noisy, dev-only |

**Separate error log.** A plugin must not rely on `Carbon.Core.log` alone. Write a per-plugin file
(`carbon/logs/<Plugin>/<Plugin>_exceptions-YYYY-MM-DD.log` + `.jsonl`) with:

- UTC timestamp, severity, stable event ID, plugin version;
- boundary/context (hook, command, timer, patch), player/entity IDs where relevant;
- exception type, message, full stack, inner chain (depth-capped);
- fingerprint (type + message + top frame), repeat count, dropped count;
- thread ID and whether it is the main thread;
- environment (Carbon/Rust/Unity versions).

Operational rules:

- **Dedupe** by fingerprint (window ~30 s) and **rate-limit** records; otherwise a per-tick fault
  becomes the outage.
- **Rotate** daily/by size, **retain** a documented period, **flush** on unload; write a dedicated
  crash file directly from `AppDomain.UnhandledException` (no locks/queue).
- **Sanitize** `\r`/`\n` in single-line fields (log injection), cap lengths, escape JSON.
- **Exclude** secrets, tokens, connection strings and unnecessary PII (OWASP Logging Cheat Sheet).
- **Protect** with `chmod 750` directory / `chmod 640` files, owned by the server user; never serve
  logs from a web root.
- **Test** read-only directory, full disk, 100 concurrent writers and a throwing formatter; the
  plugin must continue.

Full implementation and templates: `Exceptions.md` §8–§11.

---

## 2. Testing

### 2.1 What to test

| Layer | Test type | Tooling |
|---|---|---|
| Domain rules, pricing, cooldowns, parsers | Unit | xUnit v3, AwesomeAssertions |
| Config/data migrations | Unit (golden files) | xUnit + temp dirs |
| SQL/repositories | Integration (real DB) | Testcontainers or a disposable SQLite/MySQL |
| Hooks/UI/commands | In-game smoke | Local Carbon server + scripted client |
| Harmony patches | Targeted integration | Vanilla + Carbon server, `harmony_log.txt` |

Do **not** try to unit-test `BasePlayer`/Unity objects; isolate them behind interfaces and test the
logic.

### 2.2 xUnit v3 in 2026

- Current line: **xUnit.net v3** (3.0.0 Jul 2025; 3.2.0 Nov 2025; 4.0.0 Aug 2026).
- Test projects are **executables** (`OutputType=Exe`), run directly or via `dotnet test`.
- Package `xunit.v3`; `xunit.abstractions` is gone (`ITestOutputHelper` lives in `Xunit`).
- `IAsyncLifetime` returns `ValueTask` and inherits `IAsyncDisposable`.
- `async void` tests fail fast; use `async Task`.
- `Assert.Skip`, `Assert.SkipWhen`, `Assert.SkipUnless` for conditional skips.
- Built-in Microsoft Testing Platform (MTP) support; v3 4.0 requires MTP v2 on the .NET 10 SDK
  (opt in via `global.json`). Pin versions in CI.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="3.2.0" />
    <PackageReference Include="AwesomeAssertions" Version="9.6.0" />
  </ItemGroup>
</Project>
```

> xUnit v3 **4.0** (Aug 2026) requires the .NET 10 SDK + Microsoft Testing Platform v2. If you are
> still on the .NET 8/9 SDK, pin the 3.2.x line; the test APIs used here are the same.

### 2.3 Assertion libraries and licensing

- **FluentAssertions v8+** (Jan 2025) is under the **Xceed commercial license** (paid for commercial
  use, ~$129.95/dev/year). FluentAssertions **v7.2.1** remains Apache-2.0; pin it if you need the old
  licence.
- **AwesomeAssertions** is an Apache-2.0 community fork with a drop-in API (namespace change to
  `AwesomeAssertions`), current 9.x. Recommended for new projects.

```csharp
using AwesomeAssertions;

result.Should().BeEquivalentTo(expected);
player.Should().NotBeNull();
```

### 2.4 Test design

- Arrange–Act–Assert; one logical assertion per test.
- No `[SetUp]`/`[TearDown]` in xUnit: constructor builds, `IDisposable`/`IAsyncDisposable` cleans.
- Name tests `Method_State_Expected`.
- Prefer real objects to mocks; mock only boundaries (time, IO, HTTP, DB).
- Use a fake clock (`IClock`) — never `DateTime.Now` in domain code.
- Test the negative path and the boundary values.
- Keep tests deterministic; no sleeps, no network, no ordering dependence.
- Golden-file tests for config migrations: old JSON in → expected JSON out.

### 2.5 Test doubles

```csharp
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; private set; }
    public FakeClock(DateTime start) => UtcNow = start;
    public void Advance(TimeSpan delta) => UtcNow += delta;
}
```

- Hand-written fakes are often clearer than a mocking framework.
- If using a mocking library, prefer one that supports netstandard2.0 and does not require dynamic
  proxy generation on Mono (Moq/NSubstitute work in tests, not in-game).
- Never ship test/mocking assemblies to the server.

### 2.6 In-game test matrix

- fresh boot vs reload,
- config missing/corrupt/migrated,
- player vs moderator vs owner vs developer,
- console/RCon caller with `BasePlayer == null`,
- disconnect/reconnect mid-operation,
- plugin unload with pending timers/tasks/UI,
- two players (host-only bugs),
- Rust update day (patch target moved).

---

## 3. Design patterns

Patterns are vocabulary, not goals. Apply one when it removes a concrete pain (duplication, rigid
conditionals, hard-to-test code), not because it is "best practice".

### 3.1 Creational

#### Singleton

**Intent:** exactly one instance, global access.
**Use when:** a process-wide registry/service genuinely has one owner (plugin manager, config store).
**Avoid:** it is a hidden dependency and a testability killer. Prefer DI/constructor injection; if
you need one, expose `Instance` and keep the constructor private.

```csharp
public sealed class KitRegistry
{
    public static KitRegistry Instance { get; } = new();
    private KitRegistry() { }
}
```

In Rust plugins, "singleton" is often just a `static` class; that survives reloads and can hold old
assemblies alive. Prefer an instance owned by the plugin and cleared in `Unload()`.

#### Factory Method / Abstract Factory

**Intent:** move creation behind an interface so callers do not `new` concrete types.
**Use when:** creation has branching (config-driven UI element, DB provider, notification channel) or
needs caching/pooling.
**Avoid:** a factory for a class with one trivial constructor.

```csharp
public interface INotifier { Task SendAsync(string message, CancellationToken ct); }

public interface INotifierFactory
{
    INotifier Create(NotificationKind kind);
}

public sealed class NotifierFactory : INotifierFactory
{
    public INotifier Create(NotificationKind kind) => kind switch
    {
        NotificationKind.Chat => new ChatNotifier(),
        NotificationKind.Discord => new DiscordNotifier(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
```

#### Builder

**Intent:** construct a complex object step by step.
**Use when:** many optional parameters, or validation must happen before construction (CUI element
trees, SQL query builders, config objects).
**Avoid:** builders for 2–3 properties; use an options object.

```csharp
var panel = new CuiBuilder("MyPanel")
    .WithAnchor("0.1 0.7", "0.4 0.9")
    .WithColor("0.1 0.1 0.1 0.9")
    .WithCursor()
    .Build();
```

#### Prototype

**Intent:** clone a configured instance.
**Use when:** you need many near-identical objects (entity templates, kit definitions) and
construction is expensive/complex.
**Avoid:** deep-copying graphs with cycles; prefer immutable templates + factories.

#### Object Pool

**Intent:** reuse expensive objects.
**Use when:** allocations dominate a hot path and objects are uniform (`List<T>`, `StringBuilder`,
buffers).
**Avoid:** pooling ordinary short-lived objects; the GC is usually cheaper than the bookkeeping.

```csharp
var buffer = Pool.Get<List<BasePlayer>>();
try { /* ... */ }
finally { Pool.Free(ref buffer); }
```

### 3.2 Structural

#### Adapter

**Intent:** make an existing type usable through the interface your domain expects.
**Use when:** integrating Rust types (`BasePlayer`, `Item`) with your Core, or supporting
Oxide/Carbon differences.
**Avoid:** adapter layers that merely forward every member 1:1.

```csharp
public interface IPlayerView
{
    ulong Id { get; }
    string Name { get; }
    bool HasPermission(string permission);
}

public sealed class CarbonPlayerView : IPlayerView
{
    private readonly BasePlayer _player;
    private readonly Permission _permissions;

    public CarbonPlayerView(BasePlayer player, Permission permissions)
        => (_player, _permissions) = (player, permissions);

    public ulong Id => _player.userID;
    public string Name => _player.displayName;
    public bool HasPermission(string permission)
        => _permissions.UserHasPermission(_player.UserIDString, permission);
}
```

#### Decorator

**Intent:** add behaviour around an object without changing it.
**Use when:** logging, metrics, caching, retries, permission checks around a service.
**Avoid:** deep decorator stacks that make debugging hard.

```csharp
public sealed class LoggingKitService : IKitService
{
    private readonly IKitService _inner;
    private readonly ILogger _logger;

    public LoggingKitService(IKitService inner, ILogger logger)
        => (_inner, _logger) = (inner, logger);

    public Task GiveAsync(ulong playerId, string kit, CancellationToken ct)
    {
        _logger.LogInformation("Giving kit {Kit} to {PlayerId}", kit, playerId);
        return _inner.GiveAsync(playerId, kit, ct);
    }
}
```

#### Facade

**Intent:** one simple API over a complex subsystem.
**Use when:** plugins call several Rust/Carbon APIs together (spawn + UI + permissions).
**Avoid:** a facade that becomes a god object.

#### Composite

**Intent:** treat individual and grouped objects uniformly.
**Use when:** CUI trees, permission groups, reward bundles, command trees.
**Avoid:** forcing a tree where a flat list suffices.

#### Flyweight

**Intent:** share immutable state across many instances.
**Use when:** thousands of objects share a prefab/config/skin definition.
**Avoid:** sharing mutable state; the flyweight must be immutable.

#### Proxy

**Intent:** a stand-in controlling access to another object.
**Use when:** lazy loading, permission gating, remote calls, caching.
**Avoid:** confusing proxy with decorator; proxy controls access, decorator adds behaviour.

#### Bridge

**Intent:** separate abstraction from implementation.
**Use when:** you support multiple backends (JSON vs SQL store, Oxide vs Carbon) and both vary.
**Avoid:** speculative abstraction with one implementation.

### 3.3 Behavioural

#### Observer

**Intent:** notify subscribers of events.
**Use when:** plugin events, domain events, UI updates, cross-plugin hooks.
**Avoid:** leaks (unsubscribe in `Unload`), exceptions in one subscriber breaking others.

```csharp
public sealed class DomainEvents
{
    private readonly List<Action<KitGranted>> _handlers = new();

    public IDisposable Subscribe(Action<KitGranted> handler)
    {
        _handlers.Add(handler);
        return new Subscription(() => _handlers.Remove(handler));
    }

    public void Publish(KitGranted evt)
    {
        foreach (var handler in _handlers.ToArray())
        {
            try { handler(evt); }
            catch (Exception ex) { /* log; isolate */ }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;
        public Subscription(Action dispose) => _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
```

#### Strategy

**Intent:** interchangeable algorithms behind one interface.
**Use when:** pricing, loot rolls, permission policies, notification channels, sorting.
**Avoid:** a strategy per trivial branch; a `switch` is fine for two stable cases.

```csharp
public interface IPricingStrategy
{
    int Calculate(ItemDefinition item, int quantity);
}

public sealed class FlatPricing : IPricingStrategy
{
    public int Calculate(ItemDefinition item, int quantity) => item.Price * quantity;
}

public sealed class DemandPricing : IPricingStrategy
{
    private readonly IMarket _market;
    public DemandPricing(IMarket market) => _market = market;

    public int Calculate(ItemDefinition item, int quantity)
        => (int)(_market.CurrentPrice(item) * quantity);
}
```

#### Command

**Intent:** encapsulate a request as an object.
**Use when:** undo/redo, queues, audit logs, deferred execution, admin actions.
**Avoid:** turning every method into a command class.

#### State

**Intent:** behaviour changes with state, without huge conditionals.
**Use when:** multi-step wizards, encounter phases, UI flows, connection states.
**Avoid:** state machines with 2 states and no transitions.

#### Chain of Responsibility

**Intent:** pass a request along handlers until one handles it.
**Use when:** permission/validation pipelines, chat filters, damage modifiers.
**Avoid:** chains with unclear ordering or silent swallowing.

```csharp
public interface IDamageModifier
{
    DamageContext Apply(DamageContext context);
}

public sealed class ArmorModifier : IDamageModifier { /* ... */ }
public sealed class EventModifier : IDamageModifier { /* ... */ }
```

#### Template Method

**Intent:** fixed algorithm skeleton with overridable steps.
**Use when:** plugin base classes with lifecycle hooks (load/validate/apply).
**Avoid:** inheritance-heavy designs where composition would do.

#### Visitor

**Intent:** add operations to a type hierarchy without changing it.
**Use when:** traversing CUI trees, reward graphs, command ASTs.
**Avoid:** stable small hierarchies where a switch is simpler.

#### Mediator

**Intent:** centralize communication between components.
**Use when:** many plugins/modules interact and direct references explode.
**Avoid:** a mediator that becomes a message bus god object.

#### Memento

**Intent:** capture/restore state.
**Use when:** undo, snapshots, admin preview/rollback.
**Avoid:** storing references to live Unity objects in mementos.

#### Null Object

**Intent:** a do-nothing implementation instead of null checks.
**Use when:** optional collaborators (logging, notifications, metrics).
**Avoid:** hiding configuration errors behind a silent no-op.

#### Repository / Unit of Work

**Intent:** abstract persistence; group changes into a transaction.
**Use when:** data access is non-trivial or must be tested without a DB.
**Avoid:** a repository per table that just wraps an ORM; EF Core's `DbContext` is already a UoW.

#### Specification

**Intent:** compose business rules as objects.
**Use when:** complex filters (loot eligibility, kit permissions) need reuse/testing.
**Avoid:** specifications that duplicate simple LINQ.

#### Options / Configuration Object

**Intent:** group settings instead of parameter lists.
**Use when:** >3 related parameters, or config binding.
**Avoid:** mutable global options.

### 3.4 Pattern selection guide

| Symptom | Pattern |
|---|---|
| `new` scattered with branching | Factory |
| Many optional constructor args | Builder / Options |
| Repeated permission/log/metrics wrappers | Decorator |
| `switch` on type/kind growing every release | Strategy / State / Visitor |
| Subscribers must react to changes | Observer |
| Complex subsystem used by many plugins | Facade |
| Multiple storage/transport backends | Bridge / Adapter |
| Undo/queue/audit of operations | Command / Memento |
| Validation pipeline | Chain of Responsibility |
| Expensive hot-path allocations | Object Pool / Flyweight |
| Persistence leaking into domain | Repository + UoW |

---

## 4. SOLID

### 4.1 Single Responsibility Principle (SRP)

A class should have one reason to change. "Reason" means an actor/stakeholder, not "one method".

Bad:

```csharp
public sealed class KitPlugin : CarbonPlugin
{
    private void GiveKit(BasePlayer player, string kit)
    {
        // parse config
        // check permission
        // roll items
        // spawn items
        // write audit log
        // send CUI
    }
}
```

Good: split into `KitConfig`, `KitPermissionPolicy`, `KitRoller`, `KitGiver`, `KitAudit`, `KitUi`,
and let the plugin orchestrate.

Test: can you describe the class without "and"? Can two developers change it for different reasons?

### 4.2 Open/Closed Principle (OCP)

Open for extension, closed for modification. Add a new payment/notification/loot type without
editing the core.

```csharp
public interface IReward
{
    void Grant(BasePlayer player);
}

public sealed class ItemReward : IReward { /* ... */ }
public sealed class CurrencyReward : IReward { /* ... */ }

// new reward type = new class, no switch edit
```

Do not force OCP everywhere; premature abstraction is as costly as rigid code. Apply it where change
is likely (content types, integrations), not where it is not.

### 4.3 Liskov Substitution Principle (LSP)

Subtypes must be usable wherever the base type is expected, without surprising behaviour.

Violations in plugins:

- a `BaseEntity` subclass that throws on `Kill()`,
- an `IStorage` implementation that silently ignores writes,
- a `PlayerView` that returns `0` for `Id` when the player is a bot.

Rules:

- Honour preconditions/postconditions and invariants.
- Do not strengthen preconditions or weaken postconditions.
- Prefer composition to inheritance when behaviour diverges.
- Document "not supported" explicitly rather than throwing deep inside.

### 4.4 Interface Segregation Principle (ISP)

Clients should not depend on methods they do not use. Split fat interfaces.

```csharp
// Bad: one interface forces every implementation to stub methods.
public interface IEntityService
{
    void Spawn();
    void Kill();
    void Save();
    void SendUi();
}

// Good: role interfaces.
public interface ISpawnable { void Spawn(); }
public interface IKillable { void Kill(); }
public interface ISaveable { void Save(); }
public interface IUiPresenter { void SendUi(BasePlayer player); }
```

In Rust plugins, keep interfaces small and adapter-specific; a `IPlayerView` with 30 members is a
warning sign.

### 4.5 Dependency Inversion Principle (DIP)

High-level policy should not depend on low-level details; both depend on abstractions.

```csharp
public interface IKitStore
{
    Kit? Find(string name);
}

public sealed class KitService
{
    private readonly IKitStore _store;
    public KitService(IKitStore store) => _store = store;
}
```

In a plugin, wire dependencies in `OnServerInitialized`/`Loaded`, not inside domain classes. Do not
use a DI container in-game if a few constructor calls suffice; keep the composition root explicit.

### 4.6 SOLID in practice

- SRP first: it usually fixes the rest.
- DIP at boundaries (IO, time, permissions, UI), not for every class.
- LSP is about behaviour, not inheritance syntax.
- ISP keeps test doubles small.
- OCP is a direction, not a mandate; refactor to it when the second variation appears.

---

## 5. Advanced async

### 5.1 The model

- `async`/`await` is a compiler transformation over `Task`/`ValueTask`.
- Async is **not** a thread; an async method runs on the caller's context until it awaits, then
  continues on a captured context (or thread pool if none).
- In Unity/Rust, the main thread has a `SynchronizationContext`; continuation after `await` usually
  returns there — unless you `ConfigureAwait(false)`.
- Blocking on async (`.Result`, `.Wait()`) can deadlock and always hurts the server.

### 5.2 Rules

1. **Async all the way.** Do not mix sync and async at the same layer.
2. **No `.Result`/`.Wait()`/`GetAwaiter().GetResult()`** on the main thread.
3. **`CancellationToken` last parameter**, named `cancellationToken`, forwarded everywhere.
4. **`ConfigureAwait(false)`** in library/Core code; do **not** use it before touching Unity/Rust
   objects (you need the main thread).
5. **`async void` only for event/framework handlers** (`OnServerInitialized`, `Unload`), and always
   wrap the body in try/catch.
6. **Return `Task`, not `void`.** Return `Task<T>` for results.
7. **`ValueTask`** only when a hot path usually completes synchronously; await once, never cache,
   never block.
8. **Avoid `Task.Run` for I/O.** It just moves the wait to a pool thread; use real async I/O.
9. **Bound concurrency.** `Task.WhenAll` over 10k tasks is a denial-of-service on yourself.
10. **Timeouts everywhere** for network/IO (`CancellationTokenSource.CancelAfter`).
11. **Observe exceptions.** Await tasks or attach continuations; unobserved exceptions are silent.

### 5.3 Patterns

**Sequential vs concurrent**

```csharp
// Sequential: each waits for the previous.
foreach (var id in ids)
    await _store.SaveAsync(id, ct);

// Concurrent with a bound: at most 8 in flight.
using var gate = new SemaphoreSlim(8);
var tasks = ids.Select(async id =>
{
    await gate.WaitAsync(ct);
    try { await _store.SaveAsync(id, ct); }
    finally { gate.Release(); }
});

await Task.WhenAll(tasks);
```

**Timeout**

```csharp
public static async Task<T> WithTimeout<T>(
    this Task<T> task,
    TimeSpan timeout,
    CancellationToken cancellationToken)
{
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var delay = Task.Delay(timeout, cts.Token);

    var completed = await Task.WhenAny(task, delay).ConfigureAwait(false);
    if (completed != task)
    {
        throw new TimeoutException($"Operation exceeded {timeout}.");
    }

    cts.Cancel(); // stop the delay
    return await task.ConfigureAwait(false);
}
```

**Producer/consumer**

```csharp
var queue = new BlockingCollection<WorkItem>(boundedCapacity: 1024);

var worker = Task.Run(async () =>
{
    foreach (var item in queue.GetConsumingEnumerable(ct))
    {
        await ProcessAsync(item, ct);
    }
});

queue.Add(new WorkItem(...));  // blocks when full (backpressure)
queue.CompleteAdding();
await worker;
```

**Cancellation on unload**

```csharp
private CancellationTokenSource? _cts;
private Task? _worker;

private void OnServerInitialized()
{
    _cts = new CancellationTokenSource();
    _worker = RunAsync(_cts.Token);
}

private async Task RunAsync(CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            await ProcessTickAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    catch (Exception ex) { Logger.Error("Worker failed", ex); }
}

private void Unload()
{
    _cts?.Cancel();
    try { _worker?.Wait(TimeSpan.FromSeconds(5)); } catch { } // bounded shutdown only
    _cts?.Dispose();
    _cts = null;
    _worker = null;
}
```

**TaskCompletionSource**

```csharp
var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

// elsewhere:
tcs.TrySetResult(true);

// consumer:
await tcs.Task;
```

`RunContinuationsAsynchronously` prevents the completing thread from running continuations inline
(a classic deadlock/latency source).

**Async streams** (only if `IAsyncEnumerable` is available)

```csharp
public async IAsyncEnumerable<Order> ReadOrdersAsync(
    [EnumeratorCancellation] CancellationToken ct)
{
    while (await _reader.MoveNextAsync(ct))
    {
        yield return _reader.Current;
    }
}
```

### 5.4 Custom awaitables (rare, but senior-level)

`await` works on any type with a `GetAwaiter()` method returning an awaiter that implements
`INotifyCompletion` (or `ICriticalNotifyCompletion`) and exposes `IsCompleted`, `GetResult()` and
`OnCompleted(Action)`.

```csharp
using System.Runtime.CompilerServices;

public readonly struct DelayAwaiter : INotifyCompletion
{
    private readonly Task _task;
    public DelayAwaiter(Task task) => _task = task;
    public bool IsCompleted => _task.IsCompleted;
    public void OnCompleted(Action continuation) => _task.ContinueWith(_ => continuation());
    public void GetResult() => _task.GetAwaiter().GetResult();
}

public static DelayAwaiter GetAwaiter(this TimeSpan delay)
    => new(Task.Delay(delay));
```

Use custom awaitables only for:
- unifying callback-based APIs (`WaitForEndOfFrame`, Unity coroutines, event-based async),
- allocation-free hot paths (with a struct awaiter + pooling),
- adapting non-`Task` completion sources.

Do **not** hand-roll awaiters to "avoid Task"; correctness (exceptions, cancellation, sync
completion) is hard. Prefer `TaskCompletionSource` wrappers:

```csharp
public static Task<bool> WaitForClickAsync(Button button, CancellationToken ct)
{
    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var registration = ct.Register(() => tcs.TrySetCanceled(ct));

    void OnClick() => tcs.TrySetResult(true);
    button.onClick.AddListener(OnClick);

    return AwaitAndCleanup();

    async Task<bool> AwaitAndCleanup()
    {
        try { return await tcs.Task.ConfigureAwait(false); }
        finally { button.onClick.RemoveListener(OnClick); }
    }
}
```

### 5.5 Parallel loops on .NET Framework

`Parallel.For`/`Parallel.ForEach`/`Parallel.Invoke` exist on net48 and can use multiple cores, but:

- The Rust server is single-threaded for a reason; parallel work must not touch Unity/Rust objects.
- Parallel loops block the caller until done — never call them on the main thread for long work.
- Exceptions are wrapped in `AggregateException`; unwrap and log.
- Use `ParallelOptions { MaxDegreeOfParallelism = N, CancellationToken = ct }`.
- For I/O, use async + `SemaphoreSlim`, not `Parallel.*`.

```csharp
var options = new ParallelOptions
{
    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
    CancellationToken = ct
};

Parallel.ForEach(records, options, record =>
{
    // pure CPU work only; no BasePlayer/Unity access
    Process(record);
});
```

### 5.6 Async anti-patterns

| Anti-pattern | Why it hurts | Fix |
|---|---|---|
| `async void` (except handlers) | Unobservable exceptions, no completion | `async Task` |
| `.Result`/`.Wait()` | Deadlocks, thread starvation | `await` |
| `Task.Run` around sync I/O | Pool exhaustion, no real concurrency | async I/O |
| Fire-and-forget `_ = DoAsync()` | Lost exceptions, unload races | track + await/log |
| `Task.Delay` as a timer | Drift, no lifecycle | plugin timer/CTS loop |
| Unbounded `Task.WhenAll` | Memory/connection spikes | `SemaphoreSlim` cap |
| `ConfigureAwait(false)` then touch `BasePlayer` | Off-thread Unity access | configure only in Core |
| `CancellationToken.None` everywhere | Cannot stop on unload | thread the token |

### 5.7 Async in plugins vs libraries

- **Plugin hooks** are `void`; use `async void` only there, with try/catch and a tracked task.
- **Core libraries** should be `async Task` and take `CancellationToken`.
- **Harmony patches** must stay synchronous; never await inside a patch. If work is async, enqueue
  it and return.
- **Timers** are main-thread callbacks; do not run long work inside them.

---

## 6. Multithreading and concurrency

### 6.1 Threads vs tasks

| Primitive | Use |
|---|---|
| `Thread` | Long-running, blocking loop (rare; prefer tasks). |
| `Task` | Async work / pool work. |
| `TaskCreationOptions.LongRunning` | Dedicated thread for a blocking loop. |
| `ThreadPool` | Short CPU-bound work; do not block it. |
| `Timer`/plugin timers | Main-thread periodic work. |

The Rust server main thread is not a pool thread. Offload CPU work, but marshal results back.

### 6.2 Locks

```csharp
private readonly object _gate = new();
private readonly Dictionary<ulong, int> _scores = new();

public void AddScore(ulong playerId, int delta)
{
    lock (_gate)
    {
        _scores[playerId] = _scores.TryGetValue(playerId, out var current)
            ? current + delta
            : delta;
    }
}
```

Rules:

- Lock on a **private, dedicated, readonly object**; never `this`, `typeof(T)`, strings or public
  objects.
- Keep critical sections tiny; no I/O, no callbacks, no `await` inside `lock` (CS1996).
- Use one lock per invariant group; enforce a global lock order to avoid deadlocks.
- Prefer immutable data + atomic swaps to locks where possible.
- `System.Threading.Lock` (C# 13/.NET 9) is **not available**; `lock (object)` compiles to
  `Monitor.Enter/Exit`.

### 6.3 Interlocked and volatile

```csharp
private int _count;
private long _total;

Interlocked.Increment(ref _count);
Interlocked.Add(ref _total, delta);
var snapshot = Interlocked.CompareExchange(ref _count, 0, 0);
```

- `Interlocked` for counters/flags; `volatile` only for a single read/write flag, not for
  compound operations.
- `Interlocked.Exchange` is useful for idempotent disposal (`Interlocked.Exchange(ref _x, null)`).
- Do not assume `long` reads are atomic on 32-bit; use `Interlocked.Read`.

### 6.4 Reader/writer and semaphores

```csharp
private readonly ReaderWriterLockSlim _rw = new(LockRecursionPolicy.NoRecursion);

public T Read<T>(Func<State, T> read)
{
    _rw.EnterReadLock();
    try { return read(_state); }
    finally { _rw.ExitReadLock(); }
}

public void Write(Action<State> write)
{
    _rw.EnterWriteLock();
    try { write(_state); }
    finally { _rw.ExitWriteLock(); }
}
```

- `ReaderWriterLockSlim` wins only when reads dominate and hold times are short.
- `SemaphoreSlim` for async throttling (`WaitAsync`) and cross-task mutual exclusion.
- `ManualResetEventSlim`/`CountdownEvent` for coordination, not for per-frame loops.

### 6.5 Concurrent collections

- `ConcurrentDictionary<TKey,TValue>` — `GetOrAdd`, `AddOrUpdate`, `TryRemove`.
- `ConcurrentQueue<T>` / `ConcurrentStack<T>` — lock-free-ish queues.
- `BlockingCollection<T>` — bounded producer/consumer with backpressure.
- `ConcurrentBag<T>` — unordered, thread-local-ish; rarely the right choice.
- Composite operations on concurrent collections are **not atomic**; use `GetOrAdd` with a factory
  that cannot run twice for side effects (it can), or a lock.

### 6.6 Thread safety by design

- Immutable objects are inherently safe; prefer them.
- Confine mutable state to one thread (main thread) and pass messages/queues.
- Use `[ThreadStatic]`/`AsyncLocal<T>` only when you understand the lifetimes.
- `Lazy<T>` defaults to `ExecutionAndPublication` (thread-safe); use
  `LazyThreadSafetyMode.PublicationOnly` only for idempotent factories.
- Double-checked locking is unnecessary with `Lazy<T>`.
- `static` mutable state is global shared state; avoid it in plugins (reload/assembly lifetime).

### 6.7 Deadlocks and starvation

- Lock ordering: acquire locks in the same global order everywhere.
- Do not call external code/user callbacks while holding a lock.
- Do not block a thread-pool thread on another task (`Task.Run(...).Result`).
- Bound queues; unbounded queues hide backpressure until OOM.
- On the main thread, never wait synchronously for a task that needs the main thread.

### 6.8 Unity main-thread affinity

Only the main thread may touch:

- `UnityEngine.Object` (GameObject, Transform, ScriptableObject),
- `BasePlayer`, `BaseEntity`, `BaseNetworkable`,
- CUI, `ServerMgr`, `ConVar` state,
- most Carbon/Oxide APIs.

Pattern:

```csharp
// Worker produces a result.
var result = await ComputeAsync(ct);

// Marshal to main thread (Carbon).
NextFrame(() => ApplyResult(result));
```

Do not use `SynchronizationContext.Post` blindly after unload; check a disposed/cancelled flag.

### 6.9 Concurrency checklist

- [ ] Every shared mutable field is guarded or immutable.
- [ ] No locks around I/O/await/callbacks.
- [ ] Lock order documented; no nested locks without need.
- [ ] Bounded queues and concurrency.
- [ ] Cancellation on unload; tasks awaited/observed.
- [ ] No Unity object access off the main thread.
- [ ] Stress-tested with 2+ players and rapid reload.

---

## 7. Secure coding

### 7.1 Threat model for a Rust server plugin

- **Untrusted:** every client, every command/RPC argument, every web request, every third-party
  DLL/plugin, every config file edited by an admin, every response from an external API.
- **Trusted but fallible:** admins, RCON users, the plugin author.
- **Assets:** player accounts/items, server availability, API keys, database credentials, admin
  powers, player data (GDPR).

### 7.2 OWASP Top 10:2025

The current list (no 2026 edition):

| ID | Category |
|---|---|
| A01 | Broken Access Control (includes SSRF) |
| A02 | Security Misconfiguration |
| A03 | Software Supply Chain Failures |
| A04 | Cryptographic Failures |
| A05 | Injection |
| A06 | Insecure Design |
| A07 | Authentication Failures |
| A08 | Software and Data Integrity Failures |
| A09 | Security Logging and Alerting Failures |
| A10 | Mishandling of Exceptional Conditions |

Use it as a review lens, not a checklist to "pass".

### 7.3 Input validation

- **Allow-list** over deny-list: accepted shapes, not rejected patterns.
- Validate **type, length, range, format, encoding** at the boundary.
- Canonicalize before validation (trim, Unicode normalize where relevant, resolve paths).
- Reject unexpected nulls/empty/oversized values.
- Never trust `BasePlayer`, `Item`, or IDs supplied by the client without an existence/ownership
  check.
- Parse numbers with `TryParse` + range; do not `int.Parse` client input.
- Cap collection sizes and string lengths before allocating.

```csharp
public static bool TryParseAmount(string? raw, out int amount)
{
    amount = 0;
    if (string.IsNullOrWhiteSpace(raw) || raw.Length > 9) return false;
    if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out amount)) return false;
    return amount is > 0 and <= 10_000;
}
```

### 7.4 Output encoding (XSS)

Even though a Rust plugin rarely renders HTML, companion web panels/dashboards do.

- Encode per context: HTML body, attribute, JavaScript, CSS, URL.
- HTML entity encoding: `&` → `&amp;`, `<` → `&lt;`, `>` → `&gt;`, `"` → `&quot;`,
  `'` → `&#x27;`.
- Always quote attributes; unquoted attributes break encoding assumptions.
- Use `textContent`/`setAttribute`, never `innerHTML` with untrusted data.
- Treat React `dangerouslySetInnerHTML`, Angular `bypassSecurityTrust*`, Lit `unsafeHTML`, Vue `v-html`
  as escape hatches requiring sanitization (DOMPurify) and a threat review.
- Validate URLs (allow `https`, expected hosts) before putting them in `href`/`src`.
- CUI rich text (`<color>`, `<size>`) is a similar injection surface for player names; strip or
  escape markup.

### 7.5 CSRF (web panels)

- Use the framework's anti-forgery (ASP.NET Core antiforgery).
- Synchronizer token per session (or per request for high-value actions).
- `SameSite=Lax` or `Strict` cookies; `Secure`; `HttpOnly`.
- No state-changing `GET`; use POST/PUT/DELETE.
- Check `Origin`/`Referer` or Fetch Metadata (`Sec-Fetch-Site`) as defence in depth.
- **XSS defeats CSRF tokens**; fix XSS first.
- Re-authenticate for sensitive operations (key rotation, wipes).

### 7.6 SQL injection

- **Parameterized queries** are the primary control.
- Never concatenate values into SQL; never interpolate identifiers.
- Allow-list table/column/sort names if they must be dynamic.
- Use least-privilege DB accounts (no `DROP`, no cross-schema).
- Do not surface DB errors to players.
- ORMs do not make you immune: raw SQL/`FromSqlRaw` with concatenation is still injectable.

```csharp
using var cmd = new MySqlCommand(
    "SELECT id, name FROM kits WHERE owner_id = @owner AND name = @name", connection);
cmd.Parameters.AddWithValue("@owner", ownerId);
cmd.Parameters.AddWithValue("@name", name);
```

### 7.7 Authentication and authorization

- **AuthN** = who you are; **AuthZ** = what you may do.
- In plugins, Steam identity is the authN; permissions/groups/auth level are authZ.
- Check authorization **server-side** on every command, RPC and UI action.
- Default deny; explicit allow.
- Do not use UI visibility as authorization.
- Log authorization failures (rate-limited) and admin actions.
- Session/token handling (web panels): short-lived tokens, rotation, revocation, hashing at rest.

### 7.8 Password storage (companion services)

If you store passwords (web panel/admin tool), never store reversible/plaintext.

- **Argon2id**: 19 MiB memory, t=2, p=1 (OWASP baseline); 46 MiB/t=1 alternative.
- **scrypt**: 2^17, r=8, p=1.
- **bcrypt**: cost ≥10; input ≤72 bytes; beware null-byte truncation.
- **PBKDF2-HMAC-SHA256**: 600,000 iterations (SHA-512 220k; SHA-1 1.4M legacy).
- Salt: ≥128-bit random, unique per password.
- Pepper: optional, stored separately (KMS/secret store), not in the DB dump.
- Use a vetted library; do not hand-roll crypto.
- Constant-time comparison for tokens/hashes.
- Rate-limit login attempts; lockout with backoff; generic error messages.

### 7.9 Secrets management

- No secrets in source, config JSON, logs, CUI, error messages or Git history.
- Carbon Vault (`{factory:id}`) for plugin secrets; environment variables for services.
- Rotate keys; separate per environment; least privilege.
- Redact tokens/passwords in logs (`***`), including URLs with query strings.
- Never commit `.env`, `appsettings.*.json` with secrets, or database dumps.
- Treat CI logs as public.

### 7.10 TLS and transport

- HTTPS/TLS 1.2+ for all external calls; validate certificates; do not disable validation.
- Certificate pinning only with a rotation plan.
- RCON is plain WebSocket (`ws://`); bind to localhost/VPN or terminate TLS in a proxy.
- Do not invent crypto; use `SslStream`/`HttpClient` defaults.

### 7.11 Deserialization and file handling

- Prefer explicit DTOs over `object`/dynamic; validate after deserialization.
- Cap JSON depth/size; Newtonsoft `MaxDepth` and a size limit.
- Never deserialize untrusted types with `TypeNameHandling.All`.
- Path traversal: combine with a fixed root, `Path.GetFullPath`, verify the result stays under the
  root.
- Zip bombs: limit entry count/size (`ZipArchive` checks).
- Uploaded files: content-type + magic bytes + size + antivirus where applicable.

### 7.12 Exception handling and logging

- Do not leak stack traces or SQL to clients.
- Fail closed for security decisions; fail open for cosmetics.
- Log security events: auth failures, privilege changes, destructive actions, suspicious rates.
- Include correlation IDs (player ID, request ID) but not secrets.
- Alert on spikes; logs nobody reads are not monitoring.
- Keep a **separate exception log** per plugin with fingerprints, stacks and repeat counts
  (`Exceptions.md`); `Carbon.Core.log` alone is not an incident record.
- Sanitize CR/LF and delimiters before logging (log injection); cap field lengths.
- Never log passwords, tokens, session IDs, connection strings or full config dumps.
- A logging failure must not break the application; test read-only/full-disk paths.
- Define retention and access controls; logs may contain personal data.
- Log security events (auth/permission failures, destructive actions) separately from exception
  noise, so alerts are actionable.

### 7.13 Supply chain

- Pin plugin/HarmonyMod dependencies; verify hashes.
- Review third-party DLLs (they run with full trust).
- Keep Carbon/Rust/Harmony updated; read release notes.
- Generate an SBOM for companion services; scan dependencies in CI.
- Do not load assemblies from untrusted paths or URLs.

### 7.14 Access control in a plugin

- Register permissions explicitly; default deny.
- Separate `use`, `admin`, destructive permissions.
- Check ownership: `target.userID == caller.userID` unless an explicit admin override.
- Protected commands for UI-only operations; still validate.
- Never trust a hidden button or a client-side check.

### 7.15 GDPR/privacy

- Collect the minimum (SteamID, timestamps, action).
- Document retention; delete on request.
- Hash/pseudonymize where full identity is not needed.
- Do not log chat contents unless required and disclosed.

---

## 8. Advanced databases

### 8.1 Choosing a store

| Store | Use in this context |
|---|---|
| JSON files (`DataFileSystem`) | Per-plugin config/state, small/medium data, human-editable. |
| SQLite | Single-server relational data, WAL, zero admin, great for logs/economy. |
| MySQL/MariaDB | Multi-server/shared data, concurrent writers, reporting. |
| Redis | Cache, rate limits, ephemeral locks, queues (not durable system of record). |
| Message broker | Async work, integrations, outbox relay. |

Do not use EF Core in a plugin (needs .NET 10). Use provider libraries available on net48
(MySql.Data/MySqlConnector, System.Data.SQLite/Microsoft.Data.Sqlite as compatible) or plain
`DbConnection`. For companion services, EF Core 10 is appropriate.

### 8.2 Schema design

- Model entities and relationships explicitly; avoid one giant JSON column for queryable data.
- Primary keys: stable, narrow, immutable (`BIGINT`/`CHAR(36)`), not natural mutable strings.
- Foreign keys + constraints enforce invariants at the DB, not only in code.
- Normalize to remove update anomalies; denormalize deliberately for read performance.
- Use `NOT NULL` by default; allow null only with meaning.
- Store UTC timestamps; use `DATETIME(6)`/`TIMESTAMP` consistently.
- Money: integer minor units or `DECIMAL`, never `float`.
- Soft deletes only if you need history; otherwise hard delete with audit.
- Version rows for optimistic concurrency (`row_version`/`updated_at`).

### 8.3 Indexing

- Index predicates and join keys, not every column.
- Composite index order matters: equality columns first, then range, then sort.
- Covering indexes avoid lookups but cost write space.
- Selectivity matters: a boolean index is often useless.
- Every index slows inserts/updates; measure.
- Avoid functions on indexed columns in `WHERE` (`WHERE DATE(created_at) = ...`).
- `LIKE '%term'` cannot use a B-tree index; use full-text search if needed.
- Watch implicit conversions (string vs int) that disable indexes.

### 8.4 Query optimization

1. Capture the slow query and its plan (`EXPLAIN`/`EXPLAIN ANALYZE`).
2. Check row estimates vs actual; stale statistics are common.
3. Remove `SELECT *`; project only needed columns.
4. Fix N+1 (batch/`IN`/join).
5. Make predicates SARGable (no functions/arithmetic on columns).
6. Paginate with keyset (`WHERE id > @last ORDER BY id LIMIT n`) for large sets.
7. Avoid `OFFSET` deep pagination.
8. Use `EXISTS` instead of `COUNT(*)` for existence.
9. Batch inserts in a transaction; avoid one round-trip per row.
10. Re-measure; keep the change only if the plan improves.

### 8.5 Transactions and isolation

- Wrap multi-statement invariants in a transaction.
- Keep transactions short; never hold one across an `await`/HTTP call.
- Know your isolation level (MySQL default REPEATABLE READ; SQLite serializable-ish with WAL).
- Handle deadlocks (retry with backoff) and unique-constraint races.
- Use optimistic concurrency (`UPDATE ... WHERE version = @v`) for read-modify-write.
- For inventory/economy, make the DB the source of truth or reconcile explicitly.

### 8.6 Stored procedures

Use when:

- the logic must run close to data and be shared by multiple apps,
- you need set-based performance,
- you want a permission boundary (execute-only).

Avoid when:

- it hides business logic from version control/tests,
- it duplicates logic that belongs in the domain,
- debugging/observability is poor in your environment.

If used: version them, review them, test them, and keep them small.

### 8.7 ORM / EF Core (companion services)

For .NET 10 services (not plugins):

- EF Core 10 is LTS (Nov 2025 → Nov 2028), requires .NET 10.
- Query only what you need: `Select` projection, `AsNoTracking` for reads.
- Avoid lazy loading in web request paths; explicit `Include`/projection.
- Use `ExecuteUpdate`/`ExecuteDelete` for bulk operations (non-expression lambdas are new in EF 10).
- Named query filters (`IgnoreQueryFilters(["Name"])`) replace the single global filter in EF 10.
- Migrations in CI, reviewed, with a rollback plan; never `EnsureCreated` in production.
- Log slow queries (`Microsoft.EntityFrameworkCore.Database.Command`), but redact parameters.
- Raw SQL: parameterize; `FromSqlRaw` with concatenation is flagged by analyzers.
- Keep `DbContext` short-lived; it is not thread-safe.

### 8.8 SQLite specifics

- WAL mode: readers do not block writers; `-wal`/`-shm` files appear next to the DB.
- Checkpointing happens automatically (~1000 pages); long readers delay it.
- One writer at a time; use a single writer queue.
- WAL does not work on network filesystems; keep the DB local.
- `SQLITE_BUSY` → set a busy timeout and retry.
- Back up with the backup API or a checkpoint + file copy while stopped.

### 8.9 Migrations and backups

- Forward-only numbered migrations; never edit an applied migration.
- Back up before migrating; test the migration on a restored copy.
- Make migrations idempotent where possible; guard with schema version.
- Keep data and schema changes in the same transaction when the engine supports DDL transactions.
- Document rollback (restore backup) and data-loss risk.
- Verify backups by restoring them periodically.

---

## 9. Software architecture

### 9.1 Layering

```
Presentation (CUI/commands/RPC)
        ↓
Application (use cases/orchestration)
        ↓
Domain (rules/entities/value objects)
        ↑
Infrastructure (Rust/Carbon/DB/HTTP adapters)
```

Dependencies point inward: domain knows nothing about Carbon, CUI, SQL or HTTP. This is what makes
`MyPlugin.Core` testable.

### 9.2 MVC

- **Model** = domain/state, **View** = rendering, **Controller** = input → model → view.
- In a plugin: commands/hooks are controllers; CUI is the view; domain is the model.
- Keep views dumb: no business rules in CUI builders.
- Controllers validate input and call use cases; they do not contain rules.

### 9.3 MVVM

- **Model** = data/rules, **View** = UI, **ViewModel** = presentation state/commands.
- Fits CUI/LUI well: the ViewModel exposes formatted text/visibility and command names; the view
  serializes it.
- Use observable state or explicit re-render; avoid two-way binding complexity in a game UI.

```csharp
public sealed class KitViewModel
{
    public string Title { get; }
    public IReadOnlyList<KitRow> Rows { get; }
    public string CloseCommand => "chat.say /kit close";
}
```

### 9.4 Hexagonal (ports and adapters)

- **Domain** at the centre.
- **Ports** = interfaces the domain needs (`IKitStore`, `IPlayerView`, `IClock`).
- **Adapters** = implementations (Carbon, JSON, MySQL, HTTP).
- The composition root wires adapters to ports at startup.
- Benefits: testability, framework independence, easy Oxide/Carbon duality.
- Cost: more interfaces/files; use it where the domain is non-trivial, not for a 100-line plugin.

### 9.5 DDD

- **Bounded context**: a model with a precise meaning (Economy, Kits, Moderation). Do not share one
  model across contexts.
- **Entity**: identity + lifecycle (`PlayerAccount`).
- **Value object**: immutable, equality by value (`Money`, `ItemStack`).
- **Aggregate**: consistency boundary; one root, referenced by ID (`Order` + lines).
- **Domain event**: something that happened (`KitGranted`), used to decouple side effects.
- **Repository**: persistence for aggregates, not a query layer for every table.
- **Domain service**: logic that does not belong to one entity.
- **Anti-corruption layer**: translate Rust/Carbon types at the boundary.

Keep DDD proportional: a plugin with complex economy rules benefits; a chat formatter does not.

### 9.6 CQRS

- Separate write model (commands, invariants) from read model (queries, projections).
- Use when read/write shapes differ strongly or read scale is high.
- Avoid for simple CRUD; it adds eventual consistency and infrastructure.

### 9.7 Modular monolith

- One deployable, clear modules with explicit interfaces; no cross-module reach-ins.
- Best default for a plugin suite: simpler than microservices, still decomposable later.
- Enforce boundaries with namespaces/projects/internal visibility.
- Share only contracts, not implementations.

### 9.8 Architecture for a plugin (concrete)

```
MyPlugin/                  (Carbon adapter: hooks, commands, CUI, config)
  ├── Commands/            parse + authorize + call use case
  ├── Hooks/               translate game events to domain calls
  └── Ui/                  view models + CUI rendering
MyPlugin.Core/             (domain + application)
  ├── Model/               entities, value objects, rules
  ├── Services/            use cases
  └── Ports/               interfaces (stores, clock, notifier)
MyPlugin.Infrastructure/   (adapters: JSON/MySQL/HTTP)
```

### 9.9 Architecture decision records (ADRs)

For each significant choice, record: context, decision, alternatives, consequences, date. Keep them
in `docs/adr/`. This is how future-you knows why a patch exists.

---

## 10. Distributed systems

### 10.1 Fundamentals

- **CAP**: under a partition, choose consistency or availability. Be explicit.
- **Latency is not zero**; design timeouts and partial failure.
- **Exactly-once delivery does not exist** end-to-end; design **idempotent** consumers.
- **Ordering** is per-partition/per-queue, not global.
- **Retries amplify load**; use backoff + jitter + budgets.
- **The network can duplicate, reorder, delay and drop** messages.

### 10.2 Reliability patterns

| Pattern | Purpose |
|---|---|
| Timeout | Bound every remote call. |
| Retry + exponential backoff + jitter | Transient failures without stampede. |
| Circuit breaker | Stop hammering a failing dependency. |
| Bulkhead | Isolate pools/queues per dependency. |
| Idempotency key | Safe duplicate processing. |
| Transactional outbox | Atomically persist state + "message to send". |
| Inbox/dedupe table | Reject already-processed messages. |
| Saga | Multi-step distributed workflow with compensations. |
| Dead-letter queue | Park poison messages for inspection. |

### 10.3 Outbox (the workhorse)

1. In one DB transaction: update domain state and insert a row in `outbox`.
2. A relay polls `outbox` (`SELECT ... FOR UPDATE SKIP LOCKED`) and publishes.
3. Mark published; retry with backoff.
4. Consumers dedupe by message ID (`processed_messages` primary key) in the same transaction as
   their state change.

This gives at-least-once delivery with idempotent effects — the practical "exactly once".

### 10.4 Redis

- **Cache-aside**: read cache → miss → DB → populate with TTL.
- **Distributed lock**: `SET key token NX PX ttl`; release with a Lua compare-and-delete; assume
  locks can expire.
- **Rate limiting**: token bucket/counter with expiry.
- **Idempotency**: `SET idempotency:{key} 1 NX EX 86400`.
- **Pub/sub**: fire-and-forget; no durability. Use Streams/queues if you need delivery.
- Redis is not a system of record unless you configure persistence and accept its trade-offs.

### 10.5 RabbitMQ

- Exchanges (direct/topic/fanout) route to queues; bindings define routing.
- **Publisher confirms** for reliable publish; `BasicProperties.Persistent` + durable queues for
  durability.
- **Manual ack** after processing; `BasicQos` prefetch to bound in-flight work.
- **Quorum queues** for replicated durability; classic queues for transient work.
- **DLX** for rejected/expired messages; alert on DLQ depth.
- Consumers must be idempotent; redelivery is normal.
- In .NET 7+, `RabbitMQ.Client` v7 uses `CreateChannelOptions` for publisher confirmations.

### 10.6 Sagas and compensation

- Split a multi-service operation into local transactions + messages.
- Every step needs a compensating action (refund, release, cancel).
- Persist saga state; make steps idempotent; time out stuck sagas.
- Prefer orchestration (explicit state machine) for complex flows; choreography for simple ones.

### 10.7 Observability

- **Logs**: structured, correlation ID, no secrets; one event per line.
- **Metrics**: counters/histograms/gauges with bounded labels (no player IDs as labels).
- **Traces**: W3C `traceparent`; span per dependency; sample aggressively.
- **Health checks**: liveness vs readiness; do not make readiness depend on non-critical systems.
- **Alerting**: symptoms (error rate, latency, queue depth), not causes.

### 10.8 Cross-server Rust setups

- Use a shared DB/queue only for data that must be shared; keep gameplay local for latency.
- Make cross-server actions idempotent (a player may disconnect/reconnect).
- Stamp events with server ID + UTC time; handle clock skew.
- Avoid distributed locks for gameplay; prefer ownership by server/shard.
- Expect Carbon.Bridge/HTTP to be lossy; reconcile periodically.

### 10.9 SOA, microservices and the modular monolith

- **SOA**: coarse-grained services communicating over a bus/contracts; shared enterprise concerns.
  Good when integration boundaries are organisational, not technical.
- **Microservices**: independently deployable services per bounded context, own data, explicit
  contracts, heavy operational investment. Justified by independent scaling/ownership/deploy cadence,
  not by "clean code".
- **Modular monolith**: one deployable with enforced module boundaries. Best default for a plugin
  suite and most game backends; extract a service only when a module needs a different scaling or
  failure profile.

Decision prompts:

| Question | If yes |
|---|---|
| Do modules need independent deploys/teams? | Microservices |
| Is one module CPU/IO-bound far beyond the others? | Extract that service |
| Is the domain still being discovered? | Modular monolith |
| Do you lack ops maturity (tracing, CI/CD, on-call)? | Monolith first |
| Do you need shared contracts across organisations? | SOA/API gateway |

In Rust server land, a single plugin plus a small web API and a worker is usually the right shape.
Carbon.Bridge/HTTP are integration seams, not a reason to build a service mesh.

### 10.10 When not to distribute

- One server + SQLite is simpler and faster than a cluster.
- Microservices add network failure, versioning and ops cost; a modular monolith is usually right.
- Introduce a queue only when you need decoupling/backpressure/durability, not "because scale".

---

## 11. Review checklists

### 11.1 Language and design

- [ ] C# 10-safe; no unavailable BCL APIs.
- [ ] Nullable enabled; boundaries validated.
- [ ] SRP respected; no "and" classes.
- [ ] Dependencies point inward; Core has no Rust references.
- [ ] Patterns justified; no speculative abstraction.
- [ ] Exceptions handled at boundaries; no swallow-all.
- [ ] `throw;` used (not `throw ex;`); `ExceptionDispatchInfo` where rethrowing later.
- [ ] Fatal/corrupted-state exceptions are not "recovered".
- [ ] Global guard installed and removed; separate exception log with dedupe/rotation.
- [ ] Logger never throws; read-only/full-disk tested.
- [ ] No secrets/PII in logs; CR/LF sanitized; retention/access defined.
- [ ] Disposable resources owned/disposed.

### 11.2 Async/concurrency

- [ ] No `.Result`/`.Wait()` on the main thread.
- [ ] Every task awaited/observed; cancellation on unload.
- [ ] No `async void` outside handlers.
- [ ] No `await` inside `lock`.
- [ ] Bounded concurrency/queues.
- [ ] No Unity objects touched off-thread.
- [ ] Lock order documented; no lock on `this`/strings.

### 11.3 Security

- [ ] All input validated (type/length/range/format).
- [ ] Authorization server-side on every action.
- [ ] SQL parameterized; identifiers allow-listed.
- [ ] Output encoded; no `innerHTML` with untrusted data.
- [ ] CSRF protection on web panels.
- [ ] Secrets in Vault/env, never logs/config.
- [ ] TLS validated; no disabled certificate checks.
- [ ] Passwords hashed with Argon2id/bcrypt/PBKDF2 + salt.
- [ ] Rate limits and audit logs for sensitive actions.
- [ ] Third-party DLLs reviewed/pinned.

### 11.4 Data

- [ ] Schema constraints/FKs; UTC timestamps; integer money.
- [ ] Indexes match query patterns; no functions on indexed columns.
- [ ] No N+1; projections; keyset pagination.
- [ ] Transactions short; deadlock retries.
- [ ] Migrations versioned/tested; backups verified.
- [ ] SQLite WAL/local disk; single writer.

### 11.5 Operations

- [ ] Config versioned/migrated.
- [ ] Logs structured, no secrets, actionable.
- [ ] Metrics/alerts for errors, latency, queue depth.
- [ ] Runbook for update day and rollback.
- [ ] Compatibility matrix (Rust/Carbon/Harmony) in README.

---

## 12. Sources

Checked 2026-10-17. Dates are publication/update dates where available.

### Runtime and language

- Microsoft Learn, *.NET Framework 4.8 documentation* and *.NET language versioning* — the plugin
  runtime is 4.8; C# 10 is the safe baseline.
- Microsoft Learn, *What's new in C# 15 / .NET 11* (RC, 2026) — .NET 11 GA Nov 2026; features are
  not runnable on Mono/.NET Framework.
- Microsoft Learn, *What's new in EF Core 10* (Nov 2025, LTS to Nov 2028) — .NET 10-only; vector
  search, `LeftJoin`/`RightJoin`, named query filters, non-expression `ExecuteUpdateAsync`.
- Carbon source, `src/.msbuild/Common.props` (`LangVersion=preview`, `TargetFramework=net48`,
  `AllowUnsafeBlocks`) and `src/Carbon/src/Threads/ScriptCompilationThread.cs`
  (`CSharpParseOptions(LanguageVersion.Preview)`) — compiler vs runtime distinction.
- .NET Blog / InfoQ, *C# 15 preview* (2026) — union types, closed hierarchies, extension indexers,
  collection-expression arguments.

### Testing and libraries

- xUnit.net, *v3 getting started/migration* and release notes (3.0.0 Jul 2025; 3.2.0 Nov 2025;
  4.0.0 Aug 2026) — Exe projects, `IAsyncLifetime` `ValueTask`, `Assert.Skip*`, MTP v2 on .NET 10.
- Microsoft Learn, *.NET unit testing best practices* — no `[SetUp]`/`[TearDown]` in xUnit;
  constructor + `IDisposable`.
- InfoQ, *FluentAssertions v8 switches to commercial licence* (Jan 2025) — Xceed licence,
  ~$129.95/dev/year; FluentAssertions 7.2.1 remains Apache-2.0.
- AwesomeAssertions (GitHub) — Apache-2.0 fork, drop-in API (9.6.0, 2026).
- Cwalina, Barton, Abrams, *Framework Design Guidelines*, 3rd ed., Addison-Wesley, 2020,
  ISBN 978-0-13-5896464.

### Concurrency and async

- Microsoft Learn, *Managed threading basics* — .NET 9/C# 13 dedicated `System.Threading.Lock`
  (~25% faster); not available on net48.
- Microsoft Learn, *Task-based asynchronous pattern (TAP)* — `Async` suffix, `Task`/`ValueTask`,
  `CancellationToken`, no `out`/`ref`.
- Microsoft Learn, *lock statement* — `lock (object)` → `Monitor.Enter/Exit`; `System.Threading.Lock`
  compiles to `EnterScope()`.
- Microsoft Learn, *Parallel programming / PLINQ* — do not use `Parallel.ForEachAsync` on net48.

### Security

- OWASP, *Top 10:2025* (A01–A10, current edition) — no 2026 edition as of Oct 2026.
- OWASP Cheat Sheet Series: *Cross Site Scripting Prevention* (encoding table, quoted attributes,
  `textContent`, framework escape hatches), *Cross-Site Request Forgery Prevention* (synchronizer
  token, double-submit, SameSite, Fetch Metadata, no state-changing GET), *Password Storage*
  (Argon2id 19 MiB/t=2/p=1, bcrypt ≥10 ≤72 B, PBKDF2-SHA256 600k, 128-bit salt, pepper), *SQL
  Injection Prevention* (parameterization first).
- Microsoft Learn, *ASP.NET Core security topics* (antiforgery, data protection, secrets).

### Databases

- Microsoft Learn, *EF Core 10 what's new/breaking changes*, *Efficient querying* — indexes,
  projection, `Take`, keyset pagination.
- SQLite, *Write-Ahead Logging* — readers don't block writers, checkpointing (~1000 pages), local
  filesystem only, `-wal`/`-shm`.
- MySQL 8.0 Reference Manual, *Optimization and Indexes*, *InnoDB locking*.
- Martin Kleppmann, *Designing Data-Intensive Applications* (2017) — still the best conceptual
  grounding for distributed data.

### Distributed systems / observability

- Microsoft Learn, *.NET distributed systems guidance* (timeouts, retries, circuit breakers,
  idempotency, outbox).
- RabbitMQ .NET client docs (v7) — publisher confirms, `BasicQos`, quorum queues, DLX.
- Redis docs — `SET NX PX`, pub/sub vs streams, persistence trade-offs.
- Serilog best practices (message templates, `[LoggerMessage]` source generation, no interpolation,
  exception object) and OpenTelemetry .NET docs (W3C `traceparent`, `Activity`, sampling,
  low-cardinality attributes, no manual `IsEnabled` checks before `[LoggerMessage]`).
- Carbon profiler docs (`c.profile`, `carbon/config.profiler.json`) — in-game performance workflow.
- Microsoft Learn, *Best practices for exceptions* / *AppDomain.UnhandledException* /
  *TaskScheduler.UnobservedTaskException* / *ExceptionDispatchInfo* — boundary handling, notification-
  only last chance, stack preservation.
  <https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions>
- Microsoft Learn, CA1031 (do not catch general exception types), CA2200 (rethrow to preserve stack).
- OWASP, *Logging Cheat Sheet* — exclude secrets/PII, sanitize CR/LF, logging failure isolation,
  retention, access control, verification.
  <https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html>
- Unity 6, `Application.logMessageReceivedThreaded` — thread-safe handler, no Unity APIs.
  <https://docs.unity3d.com/ScriptReference/Application-logMessageReceivedThreaded.html>
- Carbon source (`main`, 2026-10-17) — hook/command/timer containment, `Logger.OnErrorCallback`,
  `InitTaskExceptions`, `LogToFile`, `FileLogger`; details in `Exceptions.md` §3.
- Harmony 2 finalizer docs / `MethodPatcher.cs` v2.3.6 — patch exceptions are not caught without a
  finalizer.
- Rust "Built Different" (June 2026) — Unity 6, minimum Windows versions; server remains mostly
  single-threaded; monthly update cadence.

> **Recency note:** most sources are 2024–2026 and current. Deliberately flagged exceptions:
> the `Rust.HarmonyMods` sample repo (≈2022, Harmony 1.x-era tooling), the Codefling "C# 10" Carbon
> blurb (matches the practical baseline but not the compiler's `Preview` setting), and the
> `Carbon.Documentation` "integrating modules — COMING SOON" placeholder. See
> `RESEARCH_REVIEW.MD` for the full audit.

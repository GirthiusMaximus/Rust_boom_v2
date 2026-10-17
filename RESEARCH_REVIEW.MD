# Research Review — Recency and Accuracy Audit

> **Scope.** Date/currency audit of every source used in `Senior_C.MD`, `CarbonMod.MD`, `Rust.MD`,
> `CARBON_PLUGIN_PLAYBOOK.MD`, `Exceptions.md` and `DontJustRollWithIt.MD`, plus the corrections made
> when source and documentation disagreed.
>
> **Audit date:** 2026-10-17. Repository heads were checked with `git ls-remote` the same day.

---

## 1. Verdict

**The corpus is current for its purpose.** The security, C#, .NET ecosystem, Carbon, Harmony and
Rust-engine material is 2024–2026 and reflects the live state of the platform as of October 2026.

Three items are deliberately retained despite being older or incomplete, because they are the only
authoritative source for their subject:

| Item | Why retained | How it is used |
|---|---|---|
| `Facepunch/Rust.HarmonyMods` sample repo (≈2022) | Only public sample of the HarmonyMod project shape/loader contract | Interface and csproj **shape** only; do not copy target framework/Harmony version. |
| Codefling "C# 10 + Harmony 2.0" Carbon blurb | Vendor marketing claim | Treated as the *practical* language baseline; the actual compiler is `LanguageVersion.Preview`. |
| `Carbon.Documentation` module-integration page ("COMING SOON") | Only in-repo module doc | Module behaviour is inferred from source/owner docs; flagged as incomplete. |

No operational/protocol source is older than 2022; two canonical books (2017, 2020) are included deliberately. No 2026 source contradicts the retained runtime model.

---

## 2. Source-by-source audit

### 2.1 C# / .NET platform

| Source | Date | Status | Notes |
|---|---|---|---|
| Microsoft Learn, .NET Framework 4.8 docs | maintained | ✅ current | Defines the plugin BCL ceiling. |
| Microsoft Learn, C# language versioning | maintained | ✅ current | C# 10 baseline for plugin code. |
| Microsoft Learn, *What's new in .NET 10* | Nov 2025 | ✅ current | LTS; supported to 10 Nov 2028. |
| Microsoft Learn, *What's new in EF Core 10* | Nov 2025 | ✅ current | .NET 10-only; not usable in plugins. |
| Microsoft Learn, *C# 15 / .NET 11* preview docs | 2026 (RC) | ⚠️ preview | GA Nov 2026; not runnable on Mono. Flagged in `Senior_C.MD` §0.3. |
| Microsoft Learn, managed threading / `lock` | 2024–2026 | ✅ current | `System.Threading.Lock` is .NET 9/C# 13; **not** in plugins. |
| Microsoft Learn, TAP guidance | maintained | ✅ current | Naming, cancellation, no `out`/`ref`. |
| .NET Blog / InfoQ, C# 15 features | 2026 | ⚠️ preview | Context only. |
| *Framework Design Guidelines*, 3rd ed. | 2020 | ✅ canonical | Stable API-design guidance; not time-sensitive. |

### 2.2 Testing and libraries

| Source | Date | Status | Notes |
|---|---|---|---|
| xUnit.net v3 migration/what's-new | 2025–2026 | ✅ current | 3.0.0 Jul 2025, 3.2.0 Nov 2025, 4.0.0 Aug 2026. |
| xUnit.net v3 4.0 release notes | Aug 2026 | ✅ current | MTP v2, Native AOT; .NET 10 SDK opt-in. |
| Microsoft Learn, unit-testing best practices | maintained | ✅ current | No `[SetUp]`/`[TearDown]` in xUnit. |
| InfoQ, FluentAssertions v8 licence change | Jan 2025 | ✅ current | Xceed commercial (~$129.95/dev/yr); v7.2.1 Apache-2.0. |
| AwesomeAssertions repo/releases | 2026 | ✅ current | Apache-2.0 fork, 9.6.0. |
| Serilog best-practice guides | 2024–2026 | ✅ current | Templates, `[LoggerMessage]`, exception object. |
| OpenTelemetry .NET docs | 2025–2026 | ✅ current | `Activity`, `traceparent`, sampling, low cardinality. |

### 2.3 Security

| Source | Date | Status | Notes |
|---|---|---|---|
| OWASP Top 10:2025 | Nov 2025 | ✅ current | A01–A10 as used; **no 2026 edition** exists. |
| OWASP XSS Prevention Cheat Sheet | maintained (2024–2026) | ✅ current | Encoding table, quoted attributes, `textContent`, framework escape hatches. |
| OWASP CSRF Prevention Cheat Sheet | maintained | ✅ current | Synchronizer token, double-submit, SameSite, Fetch Metadata, no GET mutations. |
| OWASP Password Storage Cheat Sheet | maintained | ✅ current | Argon2id 19 MiB/t=2/p=1; bcrypt ≥10 ≤72 B; PBKDF2-SHA256 600k; 128-bit salt. |
| OWASP SQL Injection Prevention | maintained | ✅ current | Parameterization first; allow-list identifiers. |
| Microsoft Learn, ASP.NET Core security | maintained | ✅ current | Antiforgery, data protection, secrets. |

### 2.4 Databases

| Source | Date | Status | Notes |
|---|---|---|---|
| EF Core 10 what's-new / breaking changes | Nov 2025 | ✅ current | Vector, `LeftJoin`/`RightJoin`, named filters, non-expression `ExecuteUpdateAsync`. |
| EF Core efficient-querying | maintained | ✅ current | Indexes, projection, keyset pagination. |
| SQLite WAL documentation | maintained | ✅ current | Readers/writers, checkpointing, local FS only. |
| MySQL 8.0 manual | maintained | ✅ current | InnoDB isolation/locking. |
| *Designing Data-Intensive Applications* | 2017 | ✅ canonical | Conceptual; not superseded. |

### 2.5 Carbon / Oxide

| Source | Date | Status | Notes |
|---|---|---|---|
| `CarbonCommunity/Carbon` `main` | inspected 2026-10-17 | ✅ current | `CarbonPlugin.cs`, `DataFileSystem.cs`, `Components/Harmony.cs`, `Switches.cs`, `HarmonyEntrypoint.cs`, `HarmonyConverter.cs`, `ClientEntity.cs`, `CuiHelper.cs`, `Entrypoint.cs`. |
| `CarbonCommunity/Carbon.Documentation` `main` | inspected 2026-10-17 | ✅ current | Dev/owner/tutorial pages; module integration page still "COMING SOON". |
| carbonmod.gg docs | 2025–2026 | ✅ current | Commands, hooks, permissions, config, extensions, LUI, Vault, conditionals, `.cszip`, profiler, modifiers. |
| Carbon README | 2026 | ✅ current | Components, branches, build model. |
| OxideMod/Oxide.Rust `src` | inspected 2026-10-17 | ✅ current | `RustCui.cs`, `RustPlugin.cs` (`SendConsoleCommand`). |
| `Facepunch/Rust.Community` | inspected 2026-10-17 | ✅ current | `CommunityEntity` CUI transport, `SendDestroyUIs`. |
| Codefling Carbon product page | 2024–2026 | ⚠️ marketing | "C# 10 + Harmony 2.0" is a support statement, not the compiler config. |

### 2.6 Harmony / HarmonyMod

| Source | Date | Status | Notes |
|---|---|---|---|
| `harmony.pardeike.net` v2 patching/priorities/injections | maintained (2.3+) | ✅ current | Static patch methods, priority 400, injection names. |
| Facepunch, *Meta Madness* (Harmony 1.2 → 2.3) | 2024-05-02 | ✅ current | `using HarmonyLib`; recompile required. No later Harmony major verified. |
| `Facepunch/HarmonyMods` decompiled `HarmonyLoader`/`IHarmonyModHooks` | server build, checked 2026 | ✅ current | Loader order, `com.facepunch.rust_dedicated.<name>`, `harmony_log.txt`, `Assembly.Load(byte[])`. |
| `Facepunch/Rust.HarmonyMods` sample repo | ≈2022 | ❌ stale for build settings | net4.6.2 + Harmony 1.x era; use interface/csproj shape only. |
| Facepunch July 2022 update (`harmony.load`/`harmony.unload`) | 2022-07 | ⚠️ older, still true | Commands exist and persist across restarts; verified in loader source. |

### 2.7 Rust engine / operations

| Source | Date | Status | Notes |
|---|---|---|---|
| Rust "Built Different" / Unity 6 | Jun 2026 | ✅ current | Min Windows Server 2019 / Win10 21H1; ≥1000 Hz mouse issue; opt-in navmesh. |
| Rust "Meta Madness" | May 2024 | ✅ current | Harmony 2.3 upgrade. |
| RustRelay wiki | 2026 | ✅ current | Packet types, `RRTS` framing, X25519/AES-256-GCM. |
| Protobuf wiki | 2026 | ✅ current | Wire format = protobuf. |
| Rust Hooks wiki | 2026 | ✅ current | Return semantics (`false` blocks, `null` continues). |
| WebRCON (`Facepunch/webrcon`, setup guides) | 2023–2026 | ✅ current | `+rcon.web 1`, `ws://host:28016/<password>`, JSON envelope; ports 28015/28016/28017/28082. |
| Rust update cadence | maintained | ✅ current | First Thursday monthly. |
| Custom-asset/prefab threads (Codefling, uMod, Reddit) | 2022–2026 | ✅ current consensus | No client asset streaming; prefab must exist client-side. |
| Carbon-for-Client reports | 2024–2026 | ⚠️ WIP | Doorstop + EAC off; no stable contract. |

### 2.8 Exception handling and logging

| Source | Date | Status | Notes |
|---|---|---|---|
| Microsoft Learn, *Best practices for exceptions* | maintained (2026) | ✅ current | Catch only recoverable; `throw;`; `ExceptionDispatchInfo`; crashed app vs undefined behaviour. |
| Microsoft Learn, *AppDomain.UnhandledException* | maintained | ✅ current | Notification only; default handler terminates; handlers run under locks; not for corrupted state. |
| Microsoft Learn, *TaskScheduler.UnobservedTaskException* | maintained | ✅ current | Escalation policy; .NET 4.5+ no termination by default; `SetObserved`. |
| Microsoft Learn, CA1031 / CA2200 | maintained | ✅ current | General catch and rethrow analyzers. |
| OWASP, *Logging Cheat Sheet* | maintained (2024–2026) | ✅ current | Secret/PII exclusion, CR/LF sanitization, failure isolation, retention, access control. |
| Carbon `main` (2026-10-17): `HookCallerInternal.cs`, `Oxide/Command.cs`, `Timer.Scheduler.cs`, `RustPlugin.cs`, `Logger.cs`, `FileLogger.cs`, `Community.cs` | checked 2026-10-17 | ✅ current | Verified catch/no-rethrow semantics, timer destruction, `LogToFile`, `OnErrorCallback`, `InitTaskExceptions`. |
| Carbon `main` loader: `Modding/ModLoader.cs`, `Oxide/Plugin.cs`, `Loaders/ScriptLoader.cs` | checked 2026-10-17 | ✅ current | Only the plugin constructor is wrapped; `Init`/`Loaded`/`Unload` go through `CallHook`; the `Load()` virtual is a direct call whose failure is logged as `Failed to compile`. |
| Harmony 2 docs, *Finalizer* | maintained | ✅ current | Finalizer wraps patches in try/catch; `__exception`; suppress/rethrow. |
| Harmony source `MethodPatcher.cs` @ `v2.3.6.0` | 2023–2024 | ✅ current for 2.3 | No catch around prefix/postfix without finalizers. |
| Harmony upstream tags | 2026 | ⚠️ version boundary | `v2.4.x` and `v3.0.0-rc.*` exist upstream; Rust's shipped `0Harmony.dll` must be checked, not assumed. |
| Unity 6, `Application.logMessageReceivedThreaded` | Unity 6.6 docs | ✅ current | Handler may run on any thread; no Unity APIs; thread-safe. |

---

## 3. Corrections made during compilation

Documentation and source disagreed in several places. Source was treated as authoritative; the
public docs are cited with a caveat where they are behind.

| Topic | What the docs/summary said | What the source shows | Resolution |
|---|---|---|---|
| Plugin lifecycle errors | Carbon catches plugin load failures | Only the constructor is wrapped. `Init`/`Loaded`/`Unload`/`OnServerInitialized` are hooks; the `Load()` virtual is a direct call, and a throw there is caught by `ScriptLoader` and logged as `Failed to compile '<file>'`, leaving the plugin half-loaded | Added to `Exceptions.md` §3 and `CarbonMod.MD` §3.2; playbook §11.2 now names `Load()`. |
| Plugin language level | "C# 10 + Harmony 2.0" | `LanguageVersion.Preview` in the script compiler; `net48` target | Documented C# 10 as the safe baseline; noted preview syntax parses but runtime polyfills decide. |
| Hook IDs | "Rust StringPool hashing" | MD5(name) first 4 bytes little-endian (verified `CanUseUI` = 1307002116, `OnDestroyUI` = 503981600) | Corrected in `CarbonMod.MD` §4.1. |
| Bridge server API | `Bridge.Server ??= new(); Bridge.Server.Start(port, password, ip, messages)` | Current source has no `Bridge.Server`; `BridgeServer` is abstract with `Start(BridgeServerInfo)` | Documented the source API and flagged the docs as stale. |
| Permissions storage | "SQLite by default" | Default is protobuf; SQLite is opt-in via `c.migrate_perms_sql` | Corrected path/command and lock behaviour. |
| `harmony.mods` output | "lists hooks and patches" | Columns: `name`, `hooks`, `methods` (patched method count) | Clarified in `CarbonMod.MD` §13.3. |
| Harmony class staticness | "patch classes must be static" | Patch **methods** must be static; the class only needs a `[HarmonyPatch]` | Corrected in `CarbonMod.MD` §5.1. |
| Loader dependencies | "skips 0Harmony/Mono.Cecil/Unity/Rust" | Skips names starting `System.`, `Microsoft.`, `Newtonsoft.`, `UnityEngine.` | Corrected in `CarbonMod.MD` §13.2. |
| Mod assembly path | `Assembly.Location` of the mod | Loader uses `Assembly.Load(byte[])`; `Location` is empty | Config/data path sample now derives from the game assembly. |
| CUI batch destroy | `CuiHelper.SendDestroyUIs` | `SendDestroyUIs` is on `CommunityEntity`; `CuiHelper` wraps single `DestroyUi` | Corrected in `CarbonMod.MD` §10.1. |
| LUI parent API | `CreateParent(CUI.ClientPanels.Overlay)` | Requires a `LuiPosition` argument | Sample fixed. |
| ClientEntity RPC | "override `OnRpc` on the plugin" | `OnRpc` is a virtual method on `ClientEntity`; subclass it | Sample fixed. |
| Vault add syntax | key/value order implied | `c.vault_add <key> <value> [encrypted|true] [factory|global]` | Corrected. |
| Permissions path | `<root>/server/<identity>/carbon.perms.db` | `ConVar.Server.filesStorageFolder/carbon.perms.db` (same folder by default) | Kept path; changed "default" to "opt-in". |
| Hook failures | "Carbon logs and the plugin keeps working" | Carbon catches, logs and **does not rethrow**; the hook's work is skipped and `Can*` may allow the action | Documented semantics + wrap every hook (`CarbonMod.MD` §4.6). |
| Timer failures | "Carbon catches timer exceptions" | Carbon catches, logs **and destroys the timer** | Documented the silent-stop trap (`CarbonMod.MD` §9.5). |
| Command failures | "Player sees an error" | Carbon catches/logs; **no player feedback** | Documented boundary reply pattern (`CarbonMod.MD` §6.4). |
| Harmony patch exceptions | "Harmony catches patch errors" | Harmony 2 has **no** catch without a finalizer; exception enters the game method's caller | Documented try/catch + finalizer (`Exceptions.md` §6.7, `CarbonMod.MD` §13.10). |
| Logging | "`Carbon.Core.log` is enough" | Carbon's log is global and rotated; per-plugin `LogToFile`/`ExceptionLog` needed for analysis | Added `Exceptions.md` + `CarbonMod.MD` §19. |

---

## 4. Residual risks / things to re-check

1. **Rust method signatures** are not an official SDK. Every `HarmonyPatch`, `BasePlayer`,
   `BaseNetworkable` and proto field must be re-verified after each monthly update.
2. **Harmony version** — Rust shipped 2.3 as of the 2024 upgrade; upstream now has `v2.4.x` and
   `v3.0.0-rc.*`. Check the actual `0Harmony.dll` file version in `RustDedicated_Data/Managed`
   before compiling or citing semantics; do not assume 2.3 or 2.4.
3. **`LanguageVersion.Preview`** — a future Roslyn update can accept syntax that the net48 runtime
   cannot execute. Test features in-game, not just in the IDE.
4. **Carbon docs lag source** (Bridge is the clearest example). Prefer source for API shape.
5. **Carbon module integration** remains undocumented; do not couple to module internals.
6. **Carbon-for-Client** is WIP; no server plugin should depend on it.
7. **.NET 11 / C# 15** is RC at audit time; GA is Nov 2026. Do not cite preview behaviour as stable.
8. **Third-party libraries** (FluentAssertions/AwesomeAssertions, xUnit, RabbitMQ.Client) change
   licences/APIs; pin and review before upgrading.
9. **RustRelay** is a supported observability feature but its packet type numbers/endpoints can
   change; pin the client to the server build.
10. **Prefab paths** are build- and OS-sensitive (case). Re-validate on Linux after updates.
11. **Exception-log failure modes** — read-only directory, full disk, 100 concurrent writers and a
    throwing formatter must be tested on the target Linux server (`Exceptions.md` §12).
12. **Global guard limits** — `AppDomain.UnhandledException` cannot prevent termination; raw threads,
    stack overflow and native crashes remain fatal. Do not promise otherwise.

---

## 5. Verification methods

- `git ls-remote` against `CarbonCommunity/Carbon`, `CarbonCommunity/Carbon.Documentation`,
  `Facepunch/Rust.Community`, `Facepunch/Rust.HarmonyMods`, `OxideMod/Oxide.Rust` — all reachable;
  heads captured 2026-10-17.
- Direct source reads via the GitHub contents API (file text, not summaries) for every API claim
  marked "source".
- Cross-checks between Carbon source and docs; where they diverged, the source was used and the
  divergence recorded above.
- Hash verification of Carbon's hook-ID scheme against IDs embedded in `CuiHelper` (`CanUseUI`,
  `OnDestroyUI`).
- Exception semantics verified line-by-line in `HookCallerInternal.cs`, `Oxide/Command.cs`,
  `Timer.Scheduler.cs`, `RustPlugin.cs`, `Logger.cs`, `FileLogger.cs`, `Community.cs`.
- Harmony patch catch semantics verified in `MethodPatcher.cs` at `v2.3.6.0` (no catch around
  prefix/postfix; finalizer wraps).
- Harmony upstream tags checked with `git ls-remote` (`v2.4.3`, `v3.0.0-rc.3` exist).

---

## 6. Sign-off

| Document | Reviewed | Notes |
|---|---|---|
| `Senior_C.MD` | ✅ 2026-10-17 | Runtime caveats (net48, no `System.Threading.Lock`, no EF Core 10) explicit. |
| `CarbonMod.MD` | ✅ 2026-10-17 | Source-checked against Carbon `main`; docs discrepancies flagged. |
| `Rust.MD` | ✅ 2026-10-17 | Protocol/asset claims labelled build-specific; RustRelay cited. |
| `CARBON_PLUGIN_PLAYBOOK.MD` | ✅ 2026-10-17 | Process guidance; exception standard added (§11); build specifics flagged as version-sensitive. |
| `Exceptions.md` | ✅ 2026-10-17 | Carbon/Harmony semantics source-checked; code targets net48/C# 10; guard limits stated honestly. |
| `DontJustRollWithIt.MD` | ✅ 2026-10-17 | Evidence tags, question gates, environment profile, iteration/hand-off/feedback templates. |
| `RESEARCH_REVIEW.MD` | ✅ 2026-10-17 | This file. |

**Bottom line:** use these documents as the reference set for implementation. The exception standard
is part of the definition of done, not an optional extra. Re-run the audit after each Rust forced
update, or when Carbon/Harmony ship a new major build.

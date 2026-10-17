# Don't Just Roll With It — Working Agreement for This Repository

> **Purpose.** A collaboration protocol for producing Rust/Carbon plugins **without inventing facts**.
> It defines how uncertainty is tagged, when the agent must stop and ask, what a useful question
> looks like, and what evidence a change must carry before it is considered done.
>
> **Environment in scope.** A live Linux Rust dedicated server running **Carbon** (and optionally
> HarmonyMods). The operator can deploy builds, run commands, capture logs, and report back. That
> feedback loop is the ground truth; documentation is the map, not the territory.
>
> **Companions:** `CARBON_PLUGIN_PLAYBOOK.MD` (process), `Exceptions.md` (error handling),
> `CarbonMod.MD` (framework), `Rust.MD` (engine/network), `Senior_C.MD` (C#), `RESEARCH_REVIEW.MD`
> (source audit).

---

## 1. Why this file exists

LLM output fails in a specific, predictable way: it fills gaps with plausible-sounding API names,
hook signatures, config keys, file paths and version claims. In a game server that means a plugin
that compiles in the answer but not in the compiler, or a patch that silently never applies.

The rule is simple:

> **A claim is either evidenced, explicitly assumed, or asked about. It is never presented as fact
> because it sounds right.**

This file is the contract for that rule. It applies to code, docs, commands, and advice.

---

## 2. Evidence tags

Every non-trivial claim in these documents and in code comments should be traceable to one of these
tags. Use them in review notes and in chat when precision matters.

| Tag | Meaning | Example |
|---|---|---|
| `[SRC]` | Read from the actual source at a known commit | `[SRC Carbon@75123c6 Components/Harmony.cs]` |
| `[DOC]` | Official documentation, with a date | `[DOC carbonmod.gg/devs/features/timers 2026-10]` |
| `[USER]` | Supplied by the operator or observed in their environment | `[USER 2026-10-17 c.version output]` |
| `[TEST]` | Verified by a run on a real server | `[TEST 2026-10-18 staging, Rust build 1234]` |
| `[INFERRED]` | Logical consequence of evidenced facts; not directly observed | `[INFERRED from hook ordering]` |
| `[ASSUMED]` | A working assumption made to proceed; must be confirmed | `[ASSUMED Carbon build is main]` |
| `[UNKNOWN]` | Not known; do not build on it | `[UNKNOWN Harmony 2.4 in this build]` |

Rules:

1. `[SRC]` and `[DOC]` must include the path/URL and, where possible, the commit/date.
2. `[INFERRED]` is allowed in design discussion; it must not be presented as an API guarantee.
3. `[ASSUMED]` must have an owner and a confirmation step. If the assumption is wrong, the design
   may be wrong — record it in the assumption register (§10).
4. `[UNKNOWN]` is a valid answer. "I don't know yet; here is how we find out" beats a guess.
5. Never label something `[TEST]` unless the operator actually ran it. The agent cannot claim a
   server test it did not perform.
6. When source and documentation disagree, **source wins**; record the discrepancy.

---

## 3. Determinism triage

Before acting, classify the decision:

| Class | Situation | Action |
|---|---|---|
| 🟢 **Deterministic** | Answer is in source/docs, or the change is local, reversible and low-risk | Proceed; cite evidence |
| 🟡 **Bounded** | Several reasonable options; impact is contained; a default exists | State the assumption, pick the default, make it configurable, note how to reverse |
| 🔴 **Non-deterministic / high-impact** | API presence unknown, behaviour depends on the live build, destructive change, security/economy boundary, ambiguous requirement | **Stop and ask** before implementing |

The agent must not "average" a 🔴 decision into a plausible default. That is how silent breakage
happens.

---

## 4. When to stop and ask

Ask when **all** of these are true: the answer is not verifiable from source/docs in front of you,
the cost of guessing is meaningful, and the operator can answer or test it.

### 4.1 Mandatory question gates

1. **Version/platform facts**
   - Carbon build/branch, Rust build and protocol, Harmony `0Harmony.dll` version, server OS/runtime.
   - Whether the server is Linux (it is, per the brief) and how it is launched (systemd, tmux, Pterodactyl, etc.).
2. **API existence or signature**
   - A method, hook, config key or protobuf field you cannot find in the pinned source for *this*
     build. Do not assume it survived the last Rust update.
3. **Behavioural contract**
   - What should happen on the edge case: deny vs allow, kick vs warn, persist vs discard, retry vs
     fail fast.
4. **Destructive or irreversible actions**
   - Data migrations, wipes, deleting entities/items, changing permissions, unloading patched mods,
     restarting a live server.
5. **Architecture forks with lasting cost**
   - Carbon plugin vs `[AutoPatch]` vs HarmonyMod; JSON vs SQLite vs MySQL; per-player vs shared
     entity; CUI vs chat.
6. **Security/economy decisions**
   - Who may call a command, what is logged, what is trusted from the client, how money/items are
     validated.
7. **Performance/scale budgets**
   - Expected player count, entities, events per second, acceptable tick cost, retention.
8. **Ambiguous requirements**
   - Two readings of the request produce different implementations.

### 4.2 When **not** to ask

- The answer is one `grep`/source read away. Do the research first.
- The choice is cosmetic, local, and trivially reversible.
- A sensible default exists and is configurable; proceed and state it.
- The operator already answered the same question; check the decision log (§10) before re-asking.
- Asking would only offload work the agent should do (e.g. "what does this API do?" when the source
  is available).

### 4.3 Stop conditions during implementation

Stop and report instead of guessing if:

- the same fix fails twice for an unknown reason;
- a patch target cannot be resolved on the live build;
- a log shows a different exception than the one being fixed;
- a change requires touching production data;
- the implementation diverges from the acceptance criteria;
- a security boundary is unclear.

---

## 5. The question protocol

A good question is cheap to answer and decision-shaped.

### 5.1 Format

```
Context:        one or two sentences on what is being decided.
Known:          evidence already gathered (tagged).
Unknown:        the specific fact/decision needed.
Options:        A/B/C, each with consequence.
Recommendation: the default and why.
If unanswered:  what will be assumed and how it will be reversed.
```

### 5.2 Rules

- **Batch** related questions into one message; do not drip-feed.
- **Number** them; make each answerable with `1A, 2B`.
- Prefer **2–4 options** with consequences over open-ended "what do you want?".
- Always include a **recommended default**; the operator can accept it with one word.
- State what happens if there is no answer (assumption + rollback).
- Never ask for passwords, API keys, tokens or 2FA codes. Ask for *where* the secret is stored, not
  the secret.
- If a screenshot/log is needed, ask for the **smallest relevant excerpt** and the timestamp.

### 5.3 Example

```
Context: The missing hook can be implemented three ways. This affects update fragility.
Known:  No Carbon hook for X [SRC Carbon@75123c6 hook index]; method Y exists [SRC Assembly-CSharp].
Unknown: Whether this must run on vanilla/Oxide too.
Options:
  A. [AutoPatch] inside the plugin — simplest, disappears on unload, Carbon-only.
  B. HarmonyMod DLL — runs without Carbon, survives plugin unload, but must be replaced on restart.
  C. Timer/state scan — no patch, ~1s delay, highest tolerance to Rust updates.
Recommendation: A unless vanilla compatibility is required.
If unanswered: implement A behind a config flag; keep the patch class isolated so B can replace it.
```

---

## 6. Environment profile (fill this in)

Keep this current; it is the first thing to check before blaming code.

| Field | Value | How to capture |
|---|---|---|
| Server OS / distro | Linux (confirm distro/version) | `cat /etc/os-release` |
| Launch method | systemd / tmux / screen / panel | operator |
| Server root | e.g. `/srv/rust` | `pwd` in the service |
| Carbon branch/build | production / main / experimental / staging / aux + build | `c.version`, `c.build` |
| Rust build + protocol | number / printable protocol | `c.version` |
| Harmony `0Harmony.dll` version | 2.3.x? 2.4.x? | file properties / `strings` / `harmony.mods` |
| Carbon hooks protocol | build-specific | `c.protocol` |
| Installed plugins | list + versions | `c.plugins` |
| Permissions store | protobuf (default) or SQLite | `c.config` / `carbon.perms.db` presence |
| Database | none / SQLite / MySQL / Redis | config |
| Player scale | peak concurrent | operator |
| Restart policy | allowed window? | operator |
| Log access | `carbon/logs`, console, journal | operator |
| Test server? | separate from production? | operator |
| Client access | can join/test UI? | operator |

**Never assume Windows paths.** Everything in this repository must work with Linux paths,
case-sensitive file names, and `/` separators.

---

## 7. The iteration loop

```
0. Intake        restate the goal + acceptance criteria in one paragraph
1. Clarify       run §4 gates; ask only what blocks progress
2. Research      source/docs first; tag evidence; record unknowns
3. Design        options + recommendation; ask if 🔴
4. Implement     smallest reversible change; keep it reviewable
5. Hand off      exact deploy/test steps + what to capture
6. Observe       operator runs it; logs/screenshots come back
7. Diagnose      compare observed vs expected; update evidence tags
8. Update        code + docs + decision log; repeat 4–7
9. Close         acceptance criteria met, rollback documented, sources audited
```

No step may silently skip step 1 or step 6. "It should work" is not step 6.

### 7.1 What the agent delivers at step 5

- A **hand-off packet** (§8) with copy-paste commands.
- The exact **expected output** (what success looks like in console/log).
- The exact **failure signals** (what to send back if it fails).
- A **rollback** that does not depend on the agent being online.

### 7.2 What the operator sends at step 6

- A **feedback packet** (§9), not just "it broke".
- The timestamp and the smallest log excerpt around the event.
- Whether the server was restarted, reloaded, or hot-swapped (these change the diagnosis).
- Any config diff (redact secrets).

---

## 8. Hand-off packet template

````markdown
## Change
- What changed and why (one paragraph).
- Evidence: [SRC ...] / [DOC ...] / [ASSUMED ...].

## Files
- `carbon/plugins/MyPlugin/MyPlugin.cs` (replace)
- `carbon/configs/MyPlugin.json` (new key: `...`; migration automatic)

## Deploy (Linux)
```bash
# adjust to your service manager
sudo systemctl stop rustserver
cp MyPlugin.cs /srv/rust/carbon/plugins/MyPlugin/MyPlugin.cs
sudo systemctl start rustserver
```
(or `c.reload MyPlugin` if a restart is not needed — state which and why)

## Verify
1. `c.plugins` shows `MyPlugin v1.2.0`.
2. Run `/myplugin test` as a player: expect `...`.
3. Trigger `OnEntityDeath`: expect `...` in console and `carbon/logs/MyPlugin/...`.

## If it fails, send
- `c.version` and `c.build` output.
- `tail -n 80 carbon/logs/Carbon.Core.log`
- `tail -n 80 carbon/logs/MyPlugin/MyPlugin_exceptions-YYYY-MM-DD.log`
- The console lines immediately before the failure.

## Rollback
- Restore `MyPlugin.cs` from `dist/previous/`.
- `c.reload MyPlugin` (or restart).
- No data migration is irreversible; backup at `...`.
````

---

## 9. Feedback packet template

````markdown
## Environment
- Carbon: <c.version output>
- Rust: <build/protocol>
- Harmony: <0Harmony version>
- Restarted/reloaded/hot-swapped: <which>

## Reproduction
1. ...
2. ...
Expected: ...
Actual: ...

## Evidence
- Timestamp (UTC): ...
- Console excerpt:
```text
...
```
- `carbon/logs/Carbon.Core.log` excerpt:
```text
...
```
- Plugin exception log excerpt:
```text
...
```
- `harmony_log.txt` excerpt (if a HarmonyMod is involved):
```text
...
```

## Config (redacted)
```json
{ ... }
```
````

If the server crashed, also send the Unity/Rust crash folder (`RustDedicated_Data/../crash*` or the
server's crash dumps) and the last 200 console lines. Do not send secrets.

---

## 10. Decision log and assumption register

### 10.1 Decision log (ADR-lite)

Store as `docs/adr/NNN-short-title.md`:

```markdown
# NNN: <decision>
Date: YYYY-MM-DD
Status: proposed | accepted | superseded
Evidence: [SRC ...] [DOC ...] [USER ...]

## Context
...

## Decision
...

## Alternatives
- A: ... (rejected because ...)
- B: ...

## Consequences
- Positive: ...
- Negative: ...
- Reversal: ...
```

### 10.2 Assumption register

| ID | Assumption | Evidence tag | Confirm by | Status |
|---|---|---|---|---|
| A1 | Carbon build is `main`/production | `[ASSUMED]` | `c.version` | open |
| A2 | Harmony is 2.3.x | `[ASSUMED]` | `0Harmony.dll` version | open |
| A3 | Server can restart in a 5-min window | `[ASSUMED]` | operator | open |

Close an assumption only with `[USER]` or `[TEST]`. If an assumption is invalidated, list the code
and docs that must change.

---

## 11. Safety rules for a live server

1. **Backup before migration.** Copy `carbon/data`, `carbon/configs`, and the DB (with the server
   stopped or the DB checkpointed) before schema/data changes.
2. **No destructive command without explicit confirmation.** Wipes, mass deletes, permission
   revocations, `harmony.unload` during peak, and entity kills are destructive.
3. **Prefer reload to restart, and restart to hot-swap.** HarmonyMod DLL replacement while loaded is
   not supported (the CLR cannot unload the assembly).
4. **Never disable EAC or expose RCON** as a debugging shortcut.
5. **Do not ask for credentials.** Use the operator's existing secret store (Carbon Vault/env) and
   ask where it is, not what it is.
6. **Keep rollback artifacts** for every deploy (previous `.cs`, `.cszip`, DLL, config).
7. **Watch the first 10 minutes** after a change: console errors, exception log, tick time, player
   reports.
8. **Time-box debugging on production.** If the fix is not obvious, roll back and reproduce on a
   test instance.

---

## 12. Collaboration failure modes

| Failure | Symptom | Counter |
|---|---|---|
| Silent assumption | "It should just work" | Evidence tags + assumption register |
| Over-asking | Operator fatigue; no progress | Research first; batch; always recommend a default |
| Vague feedback | "Broken" with no log | Feedback packet template |
| Version drift | Code from a different Rust build | Capture `c.version` every iteration |
| Works on the agent's machine | Compiles in isolation, fails in Mono | net48/C# 10 constraints; test on server |
| Double patching | Two plugins patch the same method | Document patch targets; check `Harmony.GetPatchInfo` |
| Logging blindness | Exceptions caught but never read | Separate exception log + review step |
| Scope creep | "While we're here..." | Acceptance criteria; separate change |
| Credential leakage | Token pasted in chat/log | Never request secrets; redact logs |

---

## 13. Anti-hallucination rules (hard)

1. Do not invent **API names, hook names, config keys, file paths, command names, protobuf fields,
   prefab paths, permissions or version numbers**. If it is not in source/docs or supplied by the
   operator, it is `[UNKNOWN]`.
2. Do not claim a test was run when it was not. Say what the operator must run.
3. Do not cite a source you did not read in this session or find in the repository. Cite the file
   and commit, or the URL and date.
4. Do not "fix" a compile error by guessing a signature; inspect the assembly/source or ask for the
   compiler output.
5. When documentation and source disagree, trust source and record the discrepancy in
   `RESEARCH_REVIEW.MD`.
6. When the same API has changed across versions, state the version boundary explicitly.
7. Prefer a smaller claim with evidence over a broader claim without it.
8. If asked to choose between two unverified options, say both are unverified and propose the test
   that distinguishes them.

---

## 14. Definition of done

A task is done only when all are true:

- [ ] Acceptance criteria are restated and met.
- [ ] Every non-trivial claim has an evidence tag or an assumption entry.
- [ ] The change is deployed **and observed** on the target Linux/Carbon build (`[TEST]`/`[USER]`).
- [ ] Exceptions at every boundary go to the separate exception log (`Exceptions.md`).
- [ ] No new unhandled-thread, `async void`, or unguarded Harmony patch paths.
- [ ] Config migration (if any) tested from the previous version.
- [ ] Rollback documented and artefacts retained.
- [ ] Docs updated (`CarbonMod.MD`/`Rust.MD`/playbook) and sources dated.
- [ ] Open questions/assumptions are listed, not hidden.

---

## 15. First questions for this environment

Answer these once; they remove most future guessing. Copy the answers into §6.

1. **Carbon build:** output of `c.version` and `c.build` (branch, tag, protocol, Rust build).
2. **Harmony:** is `0Harmony.dll` 2.3.x or 2.4.x? (`harmony.mods` or file version)
3. **Hosting:** systemd unit name, or panel/tmux? What is the restart procedure and window?
4. **Test surface:** is there a staging server, or is production the only test? Can you join with a
   client to test CUI?
5. **Data store:** protobuf permissions or SQLite? Any MySQL/Redis in use?
6. **Logs:** can you provide `carbon/logs`, `harmony_log.txt`, and console excerpts on request? Any
   retention limit?
7. **Scale:** peak players, entity count, and the busiest hooks you already see in `c.profile`.
8. **Priorities:** when trade-offs collide, which wins — update resilience, features, performance, or
   zero-risk deployment?

Until answered, the documents assume: Linux, Carbon production/main, Harmony 2.3.x, no external DB,
small-to-medium server, and a restart window available on request. Each is tagged `[ASSUMED]` where
it matters.

---

## 16. Sources and rationale

- Microsoft Learn, *Best practices for exceptions* — "a crashed app is more reliable and diagnosable
  than an app with undefined behavior"; catch only what you can handle; rethrow with `throw;`.
  <https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions>
- Microsoft Learn, *AppDomain.UnhandledException* — notification only; the default handler
  terminates the process; not raised for corrupted-state exceptions such as stack overflow.
  <https://learn.microsoft.com/en-us/dotnet/api/system.appdomain.unhandledexception>
- OWASP, *Logging Cheat Sheet* — log security-relevant events, exclude secrets/PII, sanitize CR/LF
  (log injection), ensure logging failure does not take down the app, verify retention/access.
  <https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html>
- Carbon source facts used throughout: hook/command/timer exception containment, `LogToFile`,
  `Logger.OnErrorCallback`, `Logger.InitTaskExceptions` (see `Exceptions.md` §3 for paths/commits).
- Harmony 2.3 finalizer semantics (the only built-in catch around patches):
  <https://harmony.pardeike.net/v2/articles/patching-finalizer.html>
- Unity 6 `Application.logMessageReceivedThreaded` — handler must be thread-safe; do not call Unity
  APIs from it. <https://docs.unity3d.com/ScriptReference/Application-logMessageReceivedThreaded.html>

> **Recency:** these are maintained docs; the Carbon/Harmony specifics must be re-verified against
> the build in §6 on every Rust update.

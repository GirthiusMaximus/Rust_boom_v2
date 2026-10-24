# Compile check — TheatricalNukeProbe

**Rule:** no build is presented to the operator until both checks below pass. Run them from the repo root.

```
python3 probes/precheck.py              # must print errors=0
python3 probes/precheck.py --selftest   # every SELFTEST line must say PASS, and baseline errors=0
```

Then read every `WARN` line and decide whether it needs a change. Only after that: commit, push, and present.

## Limits

- The authoring sandbox has no C# compiler and no Carbon, Oxide or Rust assemblies. A clean run is **not** a guarantee that the plugin compiles. It catches the errors we have seen, plus common C# traps.
- The operator's compile message is the final gate. Every new compile error they report gets an entry in the table below and a matching rule in `probes/precheck.py`, with a self-test fault.
- The header, the `Info` attribute and the `Version` constant must agree. The banner in the game shows the version actually loaded, so check it after every reload.

## Errors seen

| ID | Compiler message | Where it hit | Cause | Fix | Rule (precheck) |
|---|---|---|---|---|---|
| CE-1 | CS0104: `Harmony` is an ambiguous reference between `Carbon.Components.Harmony` and `HarmonyLib.Harmony` | v0.11.12 to v0.11.13 (`using Carbon.Components;`) | Importing a whole namespace that contains a type with a name the plugin already uses. | Do not import `Carbon.Components` whole. Alias only the types used: `CUI`, `LuiPosition`, `LuiOffset`. | §3 colliding imports; §4 LUI alias check; self-test "CS0104" |
| CE-2 | CS0173: Type of conditional expression cannot be determined because there is no implicit conversion between `NetworkableId` and `uint` | v0.11.15, line 2332. Ten copies of `cond ? net.ID : 0u` in the file. | A conditional whose branches have unrelated types. | Compare or print with `.ToString()` and use `"none"` in the other branch. | §10 CS0173 pattern; warning on bare `.net.ID`; self-test "CS0173" |
| CE-3 | Version shown in the banner does not match the file | v0.11.8 banner while the file said v0.11.13 | A stale copy of the file was loaded, or the header and `Info` were not updated together. | Version written in three places, checked by the script. Confirm the banner after each reload. | §1 version consistency; self-test "version drift" |
| CE-4 | `CuiHelper` and CUI types not found | v0.6.1 (recorded in the brief change log) | `using Oxide.Game.Rust.Cui;` missing. | Add the using. | §5 Oxide CUI names; self-test "missing Oxide CUI using" |
| CE-5 | Probe present in the switch but not in the list, or the reverse | Caught in review before any build | Two places to update for each new probe. | Update both `case` and `KnownIds` together. | §6 dispatch vs KnownIds; self-test "probe missing from KnownIds" |

## Preventive rules (no operator report yet)

- **CS0128 / CS0136:** a local declared twice in one method, or shadowing a local in an enclosing block. Loop variables are exempt because they are scoped to the loop. Rule §11; self-test "duplicate local" and "shadowed local".
- **Bracket balance:** rule §2.
- **Duplicate method signatures:** rule §9.
- **Ambiguous `Random` and `Object`** when both `System` and `UnityEngine` are imported: rule §4.
- **Missing namespaces** for LINQ, `StringBuilder`, `List<>`, `System.IO`: rule §5.
- **Named arguments to Carbon or Harmony calls:** only verified names pass without a warning. Anything new gets a warning until it is confirmed (rule §8).
- **Blocking calls** (`Thread.Sleep`, `.Result`, `.Wait()`) on the server thread: warning, rule §12.

## Adding an entry

1. The operator reports the compiler message and line.
2. Add a row to the table with the cause and fix.
3. Add a rule to `probes/precheck.py` that catches the pattern.
4. Add a self-test fault that injects the pattern into the real source.
5. Run both checks, then commit.

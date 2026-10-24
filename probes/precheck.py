#!/usr/bin/env python3
"""Pre-presentation compile checks for TheatricalNukeProbe.cs.

No C# compiler, Carbon or Rust assemblies are available in the authoring sandbox, so this script
checks for the compile and runtime errors we have actually hit (see COMPILE_CHECK.MD).

    python3 probes/precheck.py              check the plugin; exit 1 on any ERROR
    python3 probes/precheck.py --selftest   inject each known fault and confirm it is caught

Every new compile error the operator reports gets a rule here and a line in COMPILE_CHECK.MD.
"""
import re
import sys
from pathlib import Path

PLUGIN = Path(__file__).with_name("TheatricalNukeProbe.cs")

# Namespaces that collide with names the plugin uses (CS0104).
COLLIDING_IMPORTS = {
    "Carbon.Components": "brings in Carbon's Harmony type (CS0104). Alias only the types needed.",
    "System.Numerics": "brings in Vector3 (CS0104 with UnityEngine.Vector3).",
    "System.Timers": "brings in Timer (clashes with other Timer names).",
    "System.Threading": "brings in Timer (clashes with System.Timers and Carbon timers).",
    "UnityEngine.UI": "brings in InputField and Button names that clash with Rust CUI names.",
}

# Named arguments that are verified against an API we have seen in source or in the Carbon/LUI reference.
VERIFIED_NAMED = {"worldPositionStays", "charLimit", "alignment", "isProtected", "prefix", "postfix"}
DOCS_ONLY_NAMED = {"isProtected"}  # from the Carbon LUI guide; confirm in game

# Oxide CUI types that need `using Oxide.Game.Rust.Cui;`.
OXIDE_CUI_NAMES = ["CuiHelper", "CuiElementContainer", "CuiPanel", "CuiLabel", "CuiButton", "CuiInputField"]

NO_TIMEOUT_OK = {"ProbeBoxTest"}  # ends itself and opens the observation box at once

DECL_RE = re.compile(
    r"(?<![\w.])(var|int|float|double|bool|string|char|long|uint|ulong|byte|Vector3|Quaternion|"
    r"BaseEntity|BasePlayer|Transform|GameObject|StringBuilder|List<[\w<>]+>|[A-Z]\w*)\s+([a-z_]\w*)\s*(?:=|;|\bin\b)")
METHOD_RE = re.compile(
    r"\b(?:private|public|internal|protected)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],.?]+\s+(\w+)\s*\(([^()]*)\)\s*\{")


def strip_code(src):
    """Remove comments and string/char literal contents, keeping line numbers and brackets."""
    out = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if src.startswith("//", i):
            j = src.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
        elif src.startswith("/*", i):
            j = src.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("".join("\n" if ch == "\n" else " " for ch in src[i:j]))
            i = j
        elif c == '"':
            j = i + 1
            while j < n and src[j] != '"':
                j += 2 if src[j] == "\\" else 1
            j = min(j + 1, n)
            out.append('""' + "".join("\n" if ch == "\n" else " " for ch in src[i + 1:j - 1]) if j - i > 2 else '""')
            i = j
        elif c == "'":
            j = i + 1
            while j < n and src[j] != "'":
                j += 2 if src[j] == "\\" else 1
            j = min(j + 1, n)
            out.append("''")
            i = j
        else:
            out.append(c)
            i += 1
    return "".join(out)


def check_source(src):
    """Return a list of (level, message) for one plugin source text."""
    problems = []

    def err(msg):
        problems.append(("ERROR", msg))

    def warn(msg):
        problems.append(("WARN", msg))

    code = strip_code(src)
    lines = src.splitlines()

    def line_of(pos):
        return code.count("\n", 0, pos) + 1

    # 1. Version consistency
    header = re.search(r"TheatricalNukeProbe\.cs.*?v(\d+\.\d+\.\d+)", lines[0] if lines else "")
    info = re.search(r'\[Info\("TheatricalNukeProbe",\s*"[^"]*",\s*"([\d.]+)"\)\]', src)
    const = re.search(r'Version = "([\d.]+)"', src)
    versions = {
        "header": header.group(1) if header else None,
        "Info": info.group(1) if info else None,
        "Version": const.group(1) if const else None,
    }
    if len(set(versions.values())) != 1 or None in versions.values():
        err(f"version mismatch: {versions}")

    # 2. Brackets balance
    for open_c, close_c in ["{}", "()", "[]"]:
        depth = 0
        for ch in code:
            if ch == open_c:
                depth += 1
            elif ch == close_c:
                depth -= 1
                if depth < 0:
                    err(f"unbalanced '{close_c}'")
                    break
        if depth != 0:
            err(f"unbalanced '{open_c}{close_c}': depth {depth} at end of file")

    # 3. Using directives (CS0104, duplicates)
    usings = re.findall(r"^\s*using\s+([A-Za-z_][\w.]*)\s*;", code, re.M)
    seen = set()
    for ns in usings:
        if ns in seen:
            err(f"duplicate using {ns};")
        seen.add(ns)
        if ns in COLLIDING_IMPORTS:
            err(f"using {ns}; {COLLIDING_IMPORTS[ns]}")
    aliases = dict(re.findall(r"^\s*using\s+(\w+)\s*=\s*([\w.]+)\s*;", code, re.M))

    # 4. Names that resolve to two types when both namespaces are imported (CS0104)
    if "System" in seen and "UnityEngine" in seen and re.search(r"(?<![\w.])Random\s*[.(]", code):
        err("bare 'Random' with using System and using UnityEngine: ambiguous. Qualify it.")
    if re.search(r"(?<![\w.])Object\s*\.", code):
        err("bare 'Object.' resolves to System.Object. Use UnityEngine.Object explicitly.")
    for name in ["CUI", "LuiPosition", "LuiOffset"]:
        if re.search(rf"(?<![\w.]){name}\b", code) and name not in aliases:
            err(f"'{name}' used without a using alias (Carbon.Components is not imported whole).")

    # 5. Namespaces needed by names in use
    needs = {
        r"\.(Select|Where|ToList|Any|Take|Distinct|OrderBy|FirstOrDefault|Count)\(": "System.Linq",
        r"\bStringBuilder\b": "System.Text",
        r"\bList<": "System.Collections.Generic",
        r"\bFile\.|\bDirectory\.|\bPath\.": "System.IO",
    }
    for pattern, ns in needs.items():
        if re.search(pattern, code) and ns not in seen:
            err(f"uses {pattern} but 'using {ns};' is missing.")
    for name in OXIDE_CUI_NAMES:
        if re.search(rf"(?<![\w.]){name}\b", code) and "Oxide.Game.Rust.Cui" not in seen:
            err(f"uses {name} but 'using Oxide.Game.Rust.Cui;' is missing.")

    # 6. Dispatch and KnownIds (labels are read from src; code has them stripped)
    known_match = re.search(r"KnownIds\s*=\s*\{([^}]*)\}", src)
    known = set(re.findall(r'"([^"]+)"', known_match.group(1))) if known_match else set()
    dispatched = dict(re.findall(r'case\s+"([^"]+)":\s*(Probe\w+)\(', src))
    for pid in sorted(set(dispatched) - known):
        err(f"probe '{pid}' has a case but is missing from KnownIds")
    for pid in sorted(known - set(dispatched)):
        warn(f"probe '{pid}' is in KnownIds with no direct case (check it is handled elsewhere)")
    for pid, method in dispatched.items():
        if not re.search(rf"\bvoid\s+{method}\s*\(", code):
            err(f"probe '{pid}' calls {method}, which is not defined")
    for m in re.finditer(r"private\s+void\s+(Probe\w+)\s*\(ProbeRun run, BasePlayer p\)\s*\{", code):
        depth, j = 1, m.end()
        while j < len(code) and depth:
            depth += {"{": 1, "}": -1}.get(code[j], 0)
            j += 1
        if "TimeoutSeconds" not in code[m.end():j] and m.group(1) not in NO_TIMEOUT_OK:
            warn(f"{m.group(1)} never sets run.TimeoutSeconds; it will use the 300 s default")

    # 7. ConsoleCommand constants are unique
    cmds = re.findall(r"\[ConsoleCommand\((\w+)\)\]", code)
    for c in sorted({c for c in cmds if cmds.count(c) > 1}):
        err(f"ConsoleCommand constant {c} used on more than one handler")

    # 8. Named arguments: only verified or documented names
    for name in set(re.findall(r"[,(]\s*([a-z]\w*)\s*:\s*(?!:)", code)):
        if name in {"case", "default", "new", "return", "where", "in"}:
            continue
        if name not in VERIFIED_NAMED:
            warn(f"named argument '{name}:' is not in the verified list; confirm it exists in the API")
        elif name in DOCS_ONLY_NAMED:
            warn(f"named argument '{name}:' comes from the Carbon LUI guide only; confirm in game")

    # 9. Duplicate method signatures (same name and parameter count)
    sigs = {}
    for m in re.finditer(r"(?:private|public|internal)\s+(?:static\s+)?[\w<>\[\],\s]+?\s+(\w+)\s*\(([^)]*)\)\s*(?:=>|\{)", code):
        params = m.group(2).strip()
        key = (m.group(1), 0 if not params else params.count(",") + 1)
        sigs[key] = sigs.get(key, 0) + 1
    for (name, count), n in sigs.items():
        if n > 1:
            err(f"method {name} with {count} parameter(s) is defined {n} times")

    # 10. CS0173: a conditional that mixes NetworkableId with uint (or any non-string) is not allowed.
    for m in re.finditer(r"\?\s*[\w.]+\.ID\s*:\s*0u\b", code):
        err(f"line {line_of(m.start())}: conditional mixes NetworkableId and uint (CS0173). Use .ToString() and \"none\".")
    for m in re.finditer(r"\.net\.ID\b(?!\.ToString\(\))", code):
        warn(f"line {line_of(m.start())}: '.net.ID' used without .ToString(); fine in a string, a compile error in a conditional.")

    # 11. CS0128 / CS0136: a local declared twice in one method, or shadowing an enclosing local.
    method_braces = {}
    for m in METHOD_RE.finditer(code):
        params = []
        for part in m.group(2).split(","):
            words = part.strip().split()
            if words:
                params.append(words[-1])
        method_braces[m.end() - 1] = params
    tokens = []
    for i, ch in enumerate(code):
        if ch == "{":
            tokens.append((i, "{"))
        elif ch == "}":
            tokens.append((i, "}"))
    for m in DECL_RE.finditer(code):
        before = code[max(0, m.start() - 12):m.start()]
        if re.search(r"\b(for|foreach)\s*\($", before) or re.search(r"\b(for|foreach)\s*\(\s*$", before):
            continue  # loop variables are scoped to the loop, not the enclosing block
        tokens.append((m.start(), ("decl", m.group(2))))
    tokens.sort(key=lambda t: t[0])
    stack = []
    for pos, tok in tokens:
        if tok == "{":
            if pos in method_braces:
                kind = "method"
            elif any(f["kind"] == "method" for f in stack):
                kind = "block"
            else:
                kind = "class"
            scope = {"kind": kind, "names": {}}
            if kind == "method":
                for pn in method_braces[pos]:
                    scope["names"][pn] = pos
            stack.append(scope)
        elif tok == "}":
            if stack:
                stack.pop()
        else:
            name = tok[1]
            if not any(f["kind"] == "method" for f in stack):
                continue
            method_index = max(i for i, f in enumerate(stack) if f["kind"] == "method")
            for f in stack[method_index:]:
                if name in f["names"]:
                    err(f"line {line_of(pos)}: local '{name}' is already declared in this method (CS0128/CS0136)")
                    break
            stack[-1]["names"][name] = pos

    # 12. Blocking calls on the server thread
    for pattern, why in [(r"Thread\.Sleep", "blocks the server thread"),
                         (r"\.Result\b", "blocks on an async result"),
                         (r"\.Wait\(\)", "blocks on a task")]:
        if re.search(pattern, code):
            warn(f"{pattern} found: {why}")

    return problems, versions


def selftest():
    """Inject each known fault into the real source and confirm the checker reports an ERROR for it."""
    src = PLUGIN.read_text(encoding="utf-8-sig")
    faults = {
        "CS0104 whole Carbon.Components import": ("using CUI = Carbon.Components.CUI;", "using Carbon.Components;"),
        "version drift": ('Version = "', 'Version = "0.0.1" + "'),
        "probe missing from KnownIds": ('"FLIGHT", "SCALES" }', '"SCALES" }'),
        "CS0173 NetworkableId vs uint": ('var parentId = parent.net != null ? parent.net.ID.ToString() : "none";',
                                         "var parentId = parent.net != null ? parent.net.ID : 0u;"),
        "CS0128 duplicate local": ("        var half = MapHalfSize();\n        var launch = InFront(p, 30f, 0f);",
                                   "        var half = MapHalfSize();\n        var launch = InFront(p, 30f, 0f);\n        var half = 1f;"),
        "CS0136 shadowed local": ("        var half = MapHalfSize();\n        var launch = InFront(p, 30f, 0f);",
                                  "        var half = MapHalfSize();\n        var launch = InFront(p, 30f, 0f);\n        if (launch.x > 0) { var half = 1f; }"),
        "missing Oxide CUI using": ("using Oxide.Game.Rust.Cui;", ""),
    }
    failed = 0
    for name, (old, new) in faults.items():
        if src.count(old) < 1:
            print(f"SELFTEST SKIP {name}: anchor text not found")
            failed += 1
            continue
        mutated = src.replace(old, new, 1)
        problems, _ = check_source(mutated)
        caught = any(level == "ERROR" for level, _ in problems)
        print(f"SELFTEST {'PASS' if caught else 'FAIL'} {name}")
        failed += 0 if caught else 1
    base, _ = check_source(src)
    base_errors = [m for lvl, m in base if lvl == "ERROR"]
    print(f"SELFTEST baseline (real file): errors={len(base_errors)}")
    failed += 1 if base_errors else 0
    return 1 if failed else 0


def main():
    if "--selftest" in sys.argv:
        return selftest()
    src = PLUGIN.read_text(encoding="utf-8-sig")
    problems, versions = check_source(src)
    errors = [m for lvl, m in problems if lvl == "ERROR"]
    for lvl, msg in problems:
        print(f"{lvl}: {msg}")
    print(f"version={versions['header']}; errors={len(errors)}; warnings={len(problems) - len(errors)}")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())

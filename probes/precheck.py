#!/usr/bin/env python3
"""Pre-presentation checks for TheatricalNukeProbe.cs.

No C# compiler or Carbon/Rust assemblies are available in the authoring sandbox, so this script
catches the mistakes we have actually made (see the ERRORS_SEEN list) and the usual C# traps.
Run before handing a probe build to the operator:   python3 probes/precheck.py
Exit code 1 means at least one ERROR. WARN lines need a human decision.
"""
import re
import sys
from pathlib import Path

PLUGIN = Path(__file__).with_name("TheatricalNukeProbe.cs")

# Each entry is a mistake that reached the operator's server or nearly did.
ERRORS_SEEN = [
    "CS0104 ambiguous 'Harmony' (v0.11.8 to v0.11.13): 'using Carbon.Components;' imports Carbon's Harmony type.",
    "Version drift: header comment, Info attribute and Version constant disagree.",
    "Stuck observation box: buttons never reached the plugin (LUI protected commands).",
    "Keyboard captured by the box overlay (chat commands unusable).",
    "Blank button labels (text child not filling the button).",
    "Dispatch / KnownIds mismatch: probe listed but not handled, or handled but not listed.",
    "Unverified named arguments to Carbon API calls (only documented names are allowed).",
]

# Namespaces that collide with names the plugin uses.
COLLIDING_IMPORTS = {
    "Carbon.Components": "brings in Carbon's Harmony type (CS0104). Alias only the types needed.",
    "System.Numerics": "brings in Vector3 (CS0104 with UnityEngine.Vector3).",
    "System.Timers": "brings in Timer (clashes with Carbon's timer use in some contexts).",
    "System.Threading": "brings in Timer (clashes with System.Timers and Carbon timers).",
    "UnityEngine.UI": "brings in InputField/Button names used by Rust's CUI (not needed here).",
}

# Named arguments we have seen in the Carbon/LUI reference or the Rust API.
VERIFIED_NAMED = {"worldPositionStays", "charLimit", "alignment", "isProtected", "prefix", "postfix"}  # prefix/postfix: HarmonyLib Patch()
DOCS_ONLY_NAMED = {"isProtected"}  # documented in the Carbon LUI guide, not yet confirmed in game

problems = []  # (level, message)


def err(msg):
    problems.append(("ERROR", msg))


def warn(msg):
    problems.append(("WARN", msg))


def strip_code(src):
    """Remove comments and string/char literals, keeping line numbers."""
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
            out.append('""' + "".join("\n" if ch == "\n" else " " for ch in src[i + 2:j - 1]) if j - i > 2 else '""')
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


def main():
    src = PLUGIN.read_text(encoding="utf-8-sig")
    code = strip_code(src)
    lines = src.splitlines()

    # 1. Version consistency
    header = re.search(r"TheatricalNukeProbe\.cs.*?v(\d+\.\d+\.\d+)", lines[0])
    info = re.search(r'\[Info\("TheatricalNukeProbe",\s*"[^"]*",\s*"([\d.]+)"\)\]', src)
    const = re.search(r'Version = "([\d.]+)"', src)
    versions = {
        "header": header.group(1) if header else None,
        "Info": info.group(1) if info else None,
        "Version": const.group(1) if const else None,
    }
    if len(set(versions.values())) != 1 or None in versions.values():
        err(f"version mismatch: {versions}")

    # 2. Brackets balance (strings and comments removed first)
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

    # 3. Using directives: collisions and duplicates
    usings = re.findall(r"^\s*using\s+([A-Za-z_][\w.]*)\s*;", code, re.M)
    seen = set()
    for ns in usings:
        if ns in seen:
            err(f"duplicate using {ns};")
        seen.add(ns)
        if ns in COLLIDING_IMPORTS:
            err(f"using {ns}; {COLLIDING_IMPORTS[ns]}")
    aliases = dict(re.findall(r"^\s*using\s+(\w+)\s*=\s*([\w.]+)\s*;", code, re.M))

    # Bare names that resolve to two types when both namespaces are imported
    if "System" in seen and "UnityEngine" in seen:
        if re.search(r"(?<![\w.])Random\s*[.(]", code):
            err("bare 'Random' with using System and using UnityEngine: ambiguous (System.Random vs UnityEngine.Random). Qualify it.")
    if re.search(r"(?<![\w.])Object\s*\.", code):
        err("bare 'Object.' resolves to System.Object. Use UnityEngine.Object explicitly.")

    # LUI types used must be aliased (we do not import Carbon.Components whole)
    for name in ["CUI", "LuiPosition", "LuiOffset"]:
        if re.search(rf"(?<![\w.]){name}\b", code) and name not in aliases:
            err(f"'{name}' used without a using alias (Carbon.Components is not imported whole).")

    # Extension and generic types need their namespace imported
    needs = {
        r"\.(Select|Where|ToList|Any|Take|Distinct|OrderBy|FirstOrDefault|Count)\(": "System.Linq",
        r"\bStringBuilder\b": "System.Text",
        r"\bList<": "System.Collections.Generic",
        r"\bFile\.|\bDirectory\.|\bPath\.": "System.IO",
    }
    for pattern, ns in needs.items():
        if re.search(pattern, code) and ns not in seen:
            err(f"uses {pattern} but 'using {ns};' is missing.")

    # 4. Dispatch vs KnownIds
    known_match = re.search(r"KnownIds\s*=\s*\{([^}]*)\}", src)  # src, not code: strings are stripped in code
    known = set(re.findall(r'"([^"]+)"', known_match.group(1))) if known_match else set()
    dispatched = dict(re.findall(r'case\s+"([^"]+)":\s*(Probe\w+)\(', src))  # src: labels are stripped in code
    for pid in sorted(set(dispatched) - known):
        err(f"probe '{pid}' has a case but is missing from KnownIds")
    for pid in sorted(known - set(dispatched)):
        warn(f"probe '{pid}' is in KnownIds with no direct case (check it is handled elsewhere)")

    # 5. Handler methods exist, and each Probe method sets a timeout (or is a deliberate stub)
    for pid, method in dispatched.items():
        if not re.search(rf"\bvoid\s+{method}\s*\(", code):
            err(f"probe '{pid}' calls {method}, which is not defined")
    # Probes that end themselves (no timeout needed): ProbeBoxTest ends the run and opens the box at once.
    SELF_ENDING = {"ProbeBoxTest"}
    for m in re.finditer(r"private\s+void\s+(Probe\w+)\s*\(ProbeRun run, BasePlayer p\)\s*\{", code):
        start = m.end()
        depth, j = 1, start
        while j < len(code) and depth:
            depth += {"{": 1, "}": -1}.get(code[j], 0)
            j += 1
        body = code[start:j]
        if "TimeoutSeconds" not in body and m.group(1) not in SELF_ENDING:
            warn(f"{m.group(1)} never sets run.TimeoutSeconds; it will use the 300 s default")

    # 6. Console command names are unique
    cmds = re.findall(r'\[ConsoleCommand\((\w+)\)\]', code)
    dupes = {c for c in cmds if cmds.count(c) > 1}
    for c in sorted(dupes):
        err(f"ConsoleCommand constant {c} used on more than one handler")

    # 7. Named arguments in calls: only documented names
    for name in set(re.findall(r"[,(]\s*([a-z]\w*)\s*:\s*(?!:)", code)):
        if name in {"case", "default", "new", "return", "where", "in"}:
            continue
        if name not in VERIFIED_NAMED and name not in {"sb", "x", "y", "z"}:
            warn(f"named argument '{name}:' is not in the verified list; confirm it exists in the API")
        elif name in DOCS_ONLY_NAMED:
            warn(f"named argument '{name}:' comes from the Carbon LUI guide only; confirm in game")

    # 8. Duplicate method definitions (same name and parameter count)
    sigs = {}
    for m in re.finditer(r"(?:private|public|internal)\s+(?:static\s+)?[\w<>\[\],\s]+?\s+(\w+)\s*\(([^)]*)\)\s*(?:=>|\{)", code):
        name, params = m.group(1), m.group(2).strip()
        count = 0 if not params else params.count(",") + 1
        key = (name, count)
        sigs[key] = sigs.get(key, 0) + 1
    for (name, count), n in sigs.items():
        if n > 1 and name not in {"ErrorLog"}:
            err(f"method {name} with {count} parameter(s) is defined {n} times")

    # 9. Forbidden calls that block or cause trouble on the server thread
    for pattern, why in [(r"Thread\.Sleep", "blocks the server thread"),
                         (r"\.Result\b", "blocks on an async result"),
                         (r"\.Wait\(\)", "blocks on a task")]:
        if re.search(pattern, code):
            warn(f"{pattern} found: {why}")

    # Report
    errors = [m for lvl, m in problems if lvl == "ERROR"]
    for lvl, msg in problems:
        print(f"{lvl}: {msg}")
    print(f"version={versions['header']}; checks done; errors={len(errors)}; warnings={len(problems) - len(errors)}")
    print("Mistakes this script is built to catch:")
    for item in ERRORS_SEEN:
        print("  - " + item)
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())

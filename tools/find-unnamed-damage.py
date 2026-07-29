#!/usr/bin/env python3
"""Find damage the meter would file under "(none)".

A hit with a real player dealer but no card source is named from the game's
executing-model stack, which only holds anything if the hook dispatcher in
Hook.cs pushed the model onto it. Roughly a fifth of them do. So any model that
deals damage out of one of the other four fifths arrives anonymous, and needs an
entry in Patches/UnpushedSourcePatches.cs.

This derives that list from a decompile rather than from memory, because the
hand-maintained version has now been caught incomplete twice - Outbreak, then
Sleight of Flesh. Run it after a game update:

    ilspycmd -p -o out -r lib lib/sts2.dll
    tools/find-unnamed-damage.py out

Anything it prints that UnpushedSourcePatches does not cover is either a new
"(none)" row or a deliberate exclusion; the exclusions and why they are excluded
are listed in that file's own comment. Report only, no exit code games - the
answer needs a human deciding which of the two it is.
"""
import re
import sys
from pathlib import Path

HOOK = "MegaCrit.Sts2.Core.Hooks/Hook.cs"
MODEL_DIRS = (
    "MegaCrit.Sts2.Core.Models.Powers",
    "MegaCrit.Sts2.Core.Models.Relics",
    "MegaCrit.Sts2.Core.Models.Orbs",
)


def hook_dispatchers(root):
    """Every Hook.cs dispatcher, split by whether it pushes onto the model stack."""
    src = (root / HOOK).read_text()
    starts = [
        (m.group(1), m.start())
        for m in re.finditer(r"\n\tpublic static (?:async )?Task(?:<[^>]+>)? (\w+)\(", src)
    ]
    pushes, silent = set(), set()
    for i, (name, start) in enumerate(starts):
        end = starts[i + 1][1] if i + 1 < len(starts) else len(src)
        (pushes if "PushModel" in src[start:end] else silent).add(name)
    return pushes, silent


def method_body(src, from_index):
    """The braced body starting at or after from_index, by brace matching."""
    open_brace = src.find("{", from_index)
    if open_brace < 0:
        return ""
    depth = 0
    for i in range(open_brace, len(src)):
        if src[i] == "{":
            depth += 1
        elif src[i] == "}":
            depth -= 1
            if depth == 0:
                return src[open_brace : i + 1]
    return src[open_brace:]


def damaging_overrides(root, silent):
    """Models that call CreatureCmd.Damage from inside a hook nothing pushes for."""
    found = []
    for folder in MODEL_DIRS:
        for path in sorted((root / folder).glob("*.cs")):
            src = path.read_text()
            for m in re.finditer(r"public override (?:async )?Task(?:<[^>]+>)? (\w+)\(", src):
                hook = m.group(1)
                if hook not in silent:
                    continue
                body = method_body(src, m.end())
                for call in re.findall(r"CreatureCmd\.Damage\([^;]*", body, re.S):
                    found.append((path.stem, hook, " ".join(call.split())))
    return found


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "out")
    if not (root / HOOK).is_file():
        sys.exit(f"{root}/{HOOK} not found - pass the ilspycmd output directory")

    pushes, silent = hook_dispatchers(root)
    print(f"Hook.cs dispatchers: {len(pushes)} push a model, {len(silent)} do not\n")

    print("Damage dealt from a hook nothing pushes for:")
    for cls, hook, call in damaging_overrides(root, silent):
        print(f"  {cls:24s} {hook}")
        print(f"      {call[:150]}")

    print(
        "\nNot every line is a bug. Three things to check against the call text before adding one:\n"
        "  - a dealer of null is not this mechanism at all; SourceAttribution books those as DoTs\n"
        "  - whether the power sits on a player or an enemy, which decides whether the hit is booked\n"
        "    as damage dealt - and is a runtime fact, not something this script can read\n"
        "  - whether an outer frame already pushed something (Thunder fires inside OrbCmd.Evoke's push,\n"
        "    so it is misnamed after the orb rather than anonymous, and pushing here would not win)"
    )


if __name__ == "__main__":
    main()

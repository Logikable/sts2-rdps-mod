#!/usr/bin/env python3
"""Find attribution the meter would get wrong: damage with no name, and block with the wrong owner.

A hit with a real player dealer but no card source is named from the game's
executing-model stack, read off the PlayerChoiceContext the damage call was
handed. Two independent things can leave that empty, and a model needs an entry
in Patches/UnpushedSourcePatches.cs if either one does:

  - the hook's dispatcher in Hook.cs never calls PushModel. Roughly four fifths
    of them don't.
  - the model passes CreatureCmd.Damage a *newly constructed* context instead of
    the one it was given. A new context's stack is empty, so it does not matter
    what the game pushed onto the real one.

The second is the one to keep in mind while reading the output, because a line
can be flagged FRESH-CTX while its hook says PUSHES and still be a real gap -
that combination is exactly what hid Black Hole.

Damage calls are found anywhere in the class, not just in the override's own
body, and traced back to whichever override reaches them. That matters: Black
Hole deals its damage from a private helper the two hooks share, and an earlier
version of this script - which read override bodies only - could not see the
call at all.

This derives the list from a decompile rather than from memory, because the
hand-maintained version has now been caught incomplete three times - Outbreak,
then Sleight of Flesh, then Black Hole. Run it after a game update:

    ilspycmd -p -o out -r lib lib/sts2.dll
    tools/find-attribution-gaps.py out

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

BLOCK_DIRS = MODEL_DIRS + ("MegaCrit.Sts2.Core.Models.Potions",)

# Block granted to one of these is the granter's own, so crediting the wearer is right and needs no patch.
SELF_TARGETS = ("base.Owner", "base.Owner.Creature", "creature", "player.Creature")


def hook_dispatchers(root):
    """Every hook the game dispatches, split by whether its dispatcher pushes onto the model stack.

    Most hooks get a Hook.cs method of their own, but not all: the "Late"/"Early" variants are invoked inline from
    inside a sibling dispatcher (AfterSideTurnEndLate lives in the body of AfterSideTurnEnd). Matching only top-level
    methods left 18 hook names classified as neither pushing nor silent, and a hook in neither set was skipped
    outright - so damage dealt from one was invisible rather than merely unclassified. Inline call sites are therefore
    resolved to the dispatcher whose body they sit in, and anything still unresolved is reported as unknown rather
    than dropped.
    """
    src = (root / HOOK).read_text()
    starts = [
        (m.group(1), m.start())
        for m in re.finditer(r"\n\tpublic static (?:async )?Task(?:<[^>]+>)? (\w+)\(", src)
    ]
    pushes, silent = set(), set()
    spans = []
    for i, (name, start) in enumerate(starts):
        end = starts[i + 1][1] if i + 1 < len(starts) else len(src)
        pushed = "PushModel" in src[start:end]
        (pushes if pushed else silent).add(name)
        spans.append((start, end, pushed))

    # Inline dispatch: `item.SomeHook(...)` inside a dispatcher body inherits that dispatcher's push behaviour.
    for m in re.finditer(r"\.(\w+)\(", src):
        name = m.group(1)
        if name in pushes or name in silent:
            continue
        for start, end, pushed in spans:
            if start <= m.start() < end:
                (pushes if pushed else silent).add(name)
                break

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


def methods(src):
    """Every method in the file: name -> list of bodies (overloads share a name)."""
    found = {}
    for m in re.finditer(r"(public override|private|protected|internal|public)[^\n;{=]*?\s(\w+)\(", src):
        found.setdefault(m.group(2), []).append(method_body(src, m.end()))
    return found


def damaging_overrides(root, pushes, silent):
    """Models whose damage would reach the funnel with an empty model stack.

    Reached-through-a-helper counts: the call is matched anywhere in the class and traced back to the override that
    calls it, because Black Hole deals its damage from a private helper and reading override bodies alone missed it
    entirely. A fresh context counts even when the hook pushes, for the reason in the module docstring.
    """
    found = []
    for folder in MODEL_DIRS:
        for path in sorted((root / folder).glob("*.cs")):
            src = path.read_text()
            if "CreatureCmd.Damage" not in src:
                continue

            bodies = methods(src)
            damaging = {n for n, bs in bodies.items() if any("CreatureCmd.Damage" in b for b in bs)}

            for m in re.finditer(r"public override (?:async )?Task(?:<[^>]+>)? (\w+)\(", src):
                hook = m.group(1)

                body = method_body(src, m.end())
                callees = set(re.findall(r"(\w+)\(", body))
                for reached in sorted(d for d in damaging if d == hook or d in callees):
                    for reached_body in bodies[reached]:
                        for call in re.findall(r"CreatureCmd\.Damage\([^;]*", reached_body, re.S):
                            call = " ".join(call.split())
                            fresh = re.match(r"CreatureCmd\.Damage\(\s*new\s", call) is not None
                            if hook in pushes and not fresh:
                                continue  # the push is standing on the context the damage travels on
                            via = None if reached == hook else reached
                            state = "pushes" if hook in pushes else ("silent" if hook in silent else "unknown")
                            found.append((path.stem, hook, via, state, fresh, call))
    return found


def foreign_block_grants(root):
    """Sources that put block on somebody other than their own owner.

    Those are the ones whose credit cannot come from the wearer. A potion is fine either way - PotionSource knows the
    thrower - so they are listed and marked rather than skipped, since it is the one distinction here that is a fact
    about the file rather than about runtime.
    """
    found = []
    for folder in BLOCK_DIRS:
        kind = folder.rsplit(".", 1)[-1]
        for path in sorted((root / folder).glob("*.cs")):
            src = path.read_text()
            for m in re.finditer(r"CreatureCmd\.GainBlock\(([^;]*?)\)\s*;", src, re.S):
                args = " ".join(m.group(1).split())
                target = args.split(",")[0].strip()
                if target not in SELF_TARGETS:
                    found.append((path.stem, kind, target))
    return found


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else "out")
    if not (root / HOOK).is_file():
        sys.exit(f"{root}/{HOOK} not found - pass the ilspycmd output directory")

    pushes, silent = hook_dispatchers(root)
    print(f"Hook.cs dispatchers: {len(pushes)} push a model, {len(silent)} do not\n")

    print("Damage that would reach the funnel with an empty model stack:")
    for cls, hook, via, state, fresh, call in damaging_overrides(root, pushes, silent):
        where = f"{hook} -> {via}()" if via else hook
        why = {
            "pushes": "hook PUSHES but ctx is FRESH",
            "silent": "silent hook, fresh ctx" if fresh else "silent hook",
            "unknown": "UNKNOWN hook - no dispatcher found, check by hand",
        }[state]
        print(f"  {cls:24s} {where}")
        print(f"      [{why}]")
        print(f"      {call[:150]}")

    print(
        "\nNot every line is a bug. Things to check against the call text before adding one:\n"
        "  - a dealer of null is not this mechanism at all; SourceAttribution books those as DoTs\n"
        "  - whether the power sits on a player or an enemy, which decides whether the hit is booked\n"
        "    as damage dealt - and is a runtime fact, not something this script can read\n"
        "  - a hit whose target is the dealer's own side is never booked, so it has no row to misname\n"
        "    (Constrict, Disintegration, Magic Bomb)\n"
        "  - whether an outer frame already pushed something, and if so whether the push is still standing\n"
        "    when the hook runs - OrbCmd.Evoke pushes the evoked orb but pops it the line before dispatching,\n"
        "    which is why Thunder was anonymous rather than named after the orb"
    )

    print("\nBlock granted to something other than the granter's own owner:")
    for cls, kind, target in foreign_block_grants(root):
        aside = "  (a potion - PotionSource already knows the thrower)" if kind == "Potions" else ""
        print(f"  {cls:24s} [{kind}] target={target}{aside}")

    print(
        "\nThese cannot be credited to whoever wears the block. A potion is already handled; anything else\n"
        "needs an entry in Patches/ForeignBlockPatches.cs, unless the receiver is an enemy (Rampart blocks\n"
        "Turret Operators), whose block the meter does not report."
    )


if __name__ == "__main__":
    main()

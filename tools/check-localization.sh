#!/usr/bin/env bash
# Checks the translation tables under localization/ against the English one, which is the source of truth: every table
# must be valid JSON, must not carry keys English does not have (a typo, or a key that was renamed - it would silently
# never be used), and must use exactly the placeholders its English string does.
#
# The placeholder check is the one worth running: a string with a stray or missing {0} throws when it is formatted,
# which the meter swallows and draws unformatted - so the mistake shows up as a wrong-looking label in game rather
# than as an error anywhere. A key a translation is missing is only a warning; the meter falls back to English.
set -euo pipefail

cd "$(dirname "$0")/.."

python3 - "$@" <<'PY'
import json
import pathlib
import re
import sys

here = pathlib.Path("localization")
placeholders = lambda text: sorted(re.findall(r"\{(\d+)\}", text))

english = json.loads((here / "eng.json").read_text(encoding="utf-8"))
failed = False

for path in sorted(here.glob("*.json")):
    if path.name == "eng.json":
        continue

    try:
        table = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        print(f"FAIL  {path.name}: not valid JSON - {error}")
        failed = True
        continue

    broken = False
    for key, text in table.items():
        if key not in english:
            print(f"FAIL  {path.name}: key '{key}' is not in eng.json, so nothing will ever ask for it")
            broken = True
        elif placeholders(text) != placeholders(english[key]):
            print(f"FAIL  {path.name}: key '{key}' uses {placeholders(text)}, "
                  f"but English uses {placeholders(english[key])}")
            broken = True

    for key in english:
        if key not in table:
            print(f"warn  {path.name}: no translation for '{key}' - it will show in English")

    failed = failed or broken
    if not broken:
        translated = sum(1 for key in english if key in table)
        print(f"ok    {path.name}: {translated}/{len(english)} keys")

sys.exit(1 if failed else 0)
PY

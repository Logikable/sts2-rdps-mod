#!/usr/bin/env bash
# Copies the currently-installed game's sts2.dll into lib/ as lib/sts2-<version>.dll, so the binding verifier can
# check that game version offline from then on (see tools/binding-verifier). Run it once whenever you switch the game
# to a version you want covered - a new beta, say. The current build version is already covered by lib/sts2.dll, so
# capturing it again is redundant but harmless.
#
# Only the assembly is captured, not the whole install: the verifier needs nothing else, and the game itself can only
# be launched for a real in-game test on the version currently installed anyway. The captured dll is gitignored -
# MegaCrit's assembly is not redistributable.
set -euo pipefail

cd "$(dirname "$0")/.."

GAME="/mnt/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2"
SRC="$GAME/data_sts2_windows_x86_64/sts2.dll"
RELEASE_INFO="$GAME/release_info.json"

if [ ! -f "$SRC" ]; then
  echo "sts2.dll not found at $SRC - is the game installed?" >&2
  exit 1
fi

VERSION=$(python3 -c "import json; print(json.load(open('$RELEASE_INFO'))['version'].lstrip('v'))")
DEST="lib/sts2-$VERSION.dll"

cp "$SRC" "$DEST"
echo "captured game $VERSION -> $DEST"
echo "run tools/binding-verifier/verify.sh to check every captured version"

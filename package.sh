#!/usr/bin/env bash
# Builds the mod and packages the release zip that gets attached to Nexus Mods.
#
# The zip holds a single RdpsMeter/ folder with just the manifest and the dll -
# no .pdb - so unzipping it into the game's mods folder installs the mod. Prints
# the path it wrote, so callers can pick the zip up without rebuilding the name.
set -euo pipefail

cd "$(dirname "$0")"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

OUT=".godot/mono/temp/bin/Release"

# Built artifacts stay in the repo's own gitignored dist/, not in anyone's
# Downloads folder - nothing downstream needs a Windows path, and a release zip
# is build output rather than something to keep. Override with PACKAGE_DEST.
DEST="${PACKAGE_DEST:-dist}"
mkdir -p "$DEST"
DEST=$(cd "$DEST" && pwd)

# The game version the mod is built against, read from the reference dll's own
# game install rather than hardcoded, so the zip name can't drift from reality.
GAME_VERSION=$(python3 -c "
import json
with open('/mnt/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/release_info.json') as f:
    print(json.load(f)['version'].lstrip('v'))
")
MOD_VERSION=$(python3 -c "
import json
with open('RdpsMeter.json') as f:
    print(json.load(f)['version'])
")

dotnet build -c Release >/dev/null

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/RdpsMeter"
cp "$OUT/RdpsMeter.dll" RdpsMeter.json "$STAGE/RdpsMeter/"

ZIP="$DEST/RdpsMeter-$GAME_VERSION-$MOD_VERSION.zip"
rm -f "$ZIP"
(cd "$STAGE" && zip -qr "$ZIP" RdpsMeter)

echo "$ZIP"

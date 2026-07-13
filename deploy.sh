#!/usr/bin/env bash
# Builds the mod and installs it into the game's mods folder.
set -euo pipefail

cd "$(dirname "$0")"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
# This WSL image only has C/POSIX locales; without this the dotnet CLI
# crashes at startup trying to resolve the console encoding.
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

GAME_DIR="/mnt/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2"
MOD_DIR="$GAME_DIR/mods/RdpsMeter"

dotnet build -c Release

# Godot.NET.Sdk redirects build output under .godot/ rather than bin/.
OUT=".godot/mono/temp/bin/Release"

mkdir -p "$MOD_DIR"
cp "$OUT/RdpsMeter.dll" "$OUT/RdpsMeter.pdb" RdpsMeter.json "$MOD_DIR/"

echo "Deployed to $MOD_DIR"

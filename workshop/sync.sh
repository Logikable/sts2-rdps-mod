#!/usr/bin/env bash
# Builds the mod and refreshes the Steam Workshop upload workspace with it.
#
# The workspace lives on the Windows side because the uploader is a Windows exe
# talking to the Steam client; this repo keeps the parts worth versioning
# (workshop.json, the preview image) and copies them over. Only ever copies in -
# never deletes - so mod_id.txt, written by the first upload and needed by every
# update, survives. That file is copied back here as a backup, since losing it
# means the next upload publishes a second, unrelated Workshop item.
set -euo pipefail

cd "$(dirname "$0")"

REPO=".."
WORKSPACE="/mnt/c/Users/Sean/sts2-workshop/RdpsMeter"
OUT="$REPO/.godot/mono/temp/bin/Release"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

(cd "$REPO" && dotnet build -c Release)

mkdir -p "$WORKSPACE/content"
cp workshop.json image.png "$WORKSPACE/"
cp "$OUT/RdpsMeter.dll" "$REPO/RdpsMeter.json" "$WORKSPACE/content/"

# Extra screenshots, if there are any. Steam keys these by filename and drops
# any that disappear, so the whole directory is mirrored or left alone entirely.
if [ -d previews ]; then
  mkdir -p "$WORKSPACE/previews"
  cp previews/* "$WORKSPACE/previews/" 2>/dev/null || true
fi

if [ -f "$WORKSPACE/mod_id.txt" ]; then
  cp "$WORKSPACE/mod_id.txt" .
  echo "workshop item: $(cat mod_id.txt)"
fi

echo "Workspace ready at $WORKSPACE"
echo "Version: $(grep '"version"' "$REPO/RdpsMeter.json")"
echo
echo "To publish, from a Windows terminal in C:\\Users\\Sean\\sts2-workshop :"
echo "  .\\ModUploader.exe upload -w RdpsMeter"

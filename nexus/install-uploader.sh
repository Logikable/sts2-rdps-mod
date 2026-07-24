#!/usr/bin/env bash
# Fetches the prebuilt bundle of Nexus Mods' upload action, which publish.sh
# runs under node. Pinned to a tag: this is the client for their upload API and
# an unpinned bundle would change under us without warning.
set -euo pipefail

TAG="${1:-v1.0.0-beta.9}"
DEST="$HOME/.local/share/nexus-upload-action"

mkdir -p "$DEST"
gh api "repos/Nexus-Mods/upload-action/contents/dist/index.js?ref=$TAG" --jq '.content' \
  | base64 -d > "$DEST/index.js"
echo "$TAG" > "$DEST/VERSION"

echo "Installed Nexus upload action $TAG to $DEST"

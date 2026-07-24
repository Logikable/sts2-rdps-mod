#!/usr/bin/env bash
# Packages the current build and uploads it to the Nexus Mods page as a new
# version of the mod's main file.
#
# Nexus has no plain REST upload endpoint; the supported path is their v3 upload
# API, and the only published client for it is their GitHub Action. That action
# ships a prebuilt bundle and reads its inputs from INPUT_* environment
# variables, so it runs here under plain node - no CI, no npm install. Install
# or update the bundle with ./install-uploader.sh.
#
# Usage:  ./publish.sh [--dry-run]
set -euo pipefail

cd "$(dirname "$0")"

# The file on the mod page that new versions are attached to. Adding a version
# to this id is what makes Nexus show an update to people who already have it;
# uploading a fresh file instead would leave subscribers on the old one. Find it
# under "API Info" on the mod page's Files tab.
FILE_ID=6547
ACTION="$HOME/.local/share/nexus-upload-action/index.js"
KEY_FILE="$HOME/.config/nexus/api_key"

DRY_RUN=false
if [ "${1:-}" = "--dry-run" ]; then
  DRY_RUN=true
fi

if [ ! -f "$ACTION" ]; then
  echo "Uploader missing - run ./install-uploader.sh first." >&2
  exit 1
fi

API_KEY="${NEXUS_API_KEY:-$(cat "$KEY_FILE" 2>/dev/null || true)}"
if [ -z "$API_KEY" ]; then
  echo "No API key. Put one in $KEY_FILE (chmod 600) or set NEXUS_API_KEY." >&2
  echo "Keys come from https://www.nexusmods.com/settings/api-keys" >&2
  exit 1
fi

ZIP=$(../package.sh)
VERSION=$(python3 -c "
import json
with open('../RdpsMeter.json') as f:
    print(json.load(f)['version'])
")
echo "Packaged $ZIP (version $VERSION)"

if [ "$DRY_RUN" = true ]; then
  echo "Dry run - not uploading. Would add version $VERSION to file $FILE_ID."
  exit 0
fi

# The action reads the zip relative to the working directory.
cd "$(dirname "$ZIP")"

INPUT_API_KEY="$API_KEY" \
INPUT_FILE_ID="$FILE_ID" \
INPUT_FILENAME="$(basename "$ZIP")" \
INPUT_VERSION="$VERSION" \
INPUT_DISPLAY_NAME="RdpsMeter $VERSION" \
INPUT_CATEGORY="main" \
INPUT_UPDATE_MOD_VERSION="true" \
INPUT_ARCHIVE_EXISTING_VERSION="true" \
INPUT_PRIMARY_MOD_MANAGER_DOWNLOAD="true" \
  node "$ACTION"

echo "Uploaded version $VERSION to https://www.nexusmods.com/slaythespire2/mods/1385"

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

GAME_DOMAIN=slaythespire2
MOD_PAGE_ID=1385

# Only a file whose name starts with this is ever uploaded to. The file id must
# be resolved through the v3 API, NOT taken from the older v1 API: v1 file ids
# are numbered per mod while v3 ids are global, so v1's id for this mod points
# at a stranger's file in v3. Uploading there would have been a real mistake,
# caught only because that account happened to refuse us.
FILE_NAME_PREFIX=RdpsMeter

API=https://api.nexusmods.com/v3
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

get() {
  curl -sS -H "apikey: $API_KEY" -H "User-Agent: RdpsMeter-publish" "$API$1"
}

# The page id in the mod's URL is game-scoped; the file listing is keyed by the
# mod's global id, so look that up first.
MOD_UID=$(get "/games/$GAME_DOMAIN/mods/$MOD_PAGE_ID" | python3 -c "
import json, sys
print(json.load(sys.stdin)['data']['id'])
")

# Newest active file whose name is ours. Adding a version to an existing file is
# what pushes an update to people who already have it; a brand new file would
# leave them behind.
read -r FILE_ID FILE_LABEL <<<"$(get "/mods/$MOD_UID/files" | python3 -c "
import json, sys
prefix = '$FILE_NAME_PREFIX'
files = [f for f in json.load(sys.stdin)['data']['mod_files']
         if f['is_active'] and f['name'].startswith(prefix)]
if not files:
    raise SystemExit(f'No active file on the mod page starts with {prefix!r}')
newest = max(files, key=lambda f: f['last_file_uploaded_at'])
print(newest['id'], newest['name'])
")"

echo "Mod $MOD_PAGE_ID ($MOD_UID) -> file $FILE_ID '$FILE_LABEL'"

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

# Name the file version after the zip itself (RdpsMeter-<game>-<mod>), so the
# Nexus file name carries the game version too, not just the mod version.
DISPLAY_NAME="$(basename "$ZIP" .zip)"

INPUT_API_KEY="$API_KEY" \
INPUT_FILE_ID="$FILE_ID" \
INPUT_FILENAME="$(basename "$ZIP")" \
INPUT_VERSION="$VERSION" \
INPUT_DISPLAY_NAME="$DISPLAY_NAME" \
INPUT_CATEGORY="main" \
INPUT_UPDATE_MOD_VERSION="true" \
INPUT_ARCHIVE_EXISTING_VERSION="true" \
INPUT_PRIMARY_MOD_MANAGER_DOWNLOAD="true" \
  node "$ACTION"

echo "Uploaded version $VERSION to https://www.nexusmods.com/$GAME_DOMAIN/mods/$MOD_PAGE_ID"

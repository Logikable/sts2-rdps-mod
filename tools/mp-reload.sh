#!/usr/bin/env bash
# Reproduces the "disconnect and rejoin" report the only way this build allows: the party quits and comes back.
#
# Mid-run rejoining is not implemented in 0.111.0 - a reconnect is answered with NotImplementedException - so the
# reachable flow is: play a co-op fight and leave a save (phase A), then have the host load that saved run from the
# main menu while the other player joins the load lobby (phase B). Phase B is what runs
# RunManager.SetUpSavedMultiplayer, which the meter prefixes to reload the run's breakdown from disk.
#
# Phase A must write a save, so it runs with --rdps-mp-save=true. Every other session leaves the save folder alone.
set -uo pipefail

cd "$(dirname "$0")/.."

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

GAME_DIR="/mnt/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2"
MOD_DIR="$GAME_DIR/mods/RdpsMeter"
VERSION=$(python3 -c "import json; print(json.load(open('$GAME_DIR/release_info.json'))['version'].lstrip('v'))")
STS2="lib/sts2-$VERSION.dll"

SCENARIO="${1:-crossbuff}"
CHARACTER="${2:-0}"
HOST_METER="${HOST_METER:-on}"
CLIENT_METER="${CLIENT_METER:-off}"
CLIENT_ID="${CLIENT_ID:-1000}"
OUT="${OUT:-/tmp/rdps-mp-reload}"
mkdir -p "$OUT"

echo "== building harness against $STS2 =="
dotnet build -c Release -p:Harness=true -p:Sts2Ref="$STS2" >/dev/null || exit 1
OUT_BIN=".godot/mono/temp/bin/Release"
mkdir -p "$MOD_DIR"
cp "$OUT_BIN/RdpsMeter.dll" "$OUT_BIN/RdpsMeter.pdb" RdpsMeter.json "$MOD_DIR/"
rm -f "$MOD_DIR/autotest.marker"

kill_game() { /mnt/c/Windows/System32/taskkill.exe /IM SlayTheSpire2.exe /F >/dev/null 2>&1 || true; }
trap kill_game EXIT

# One phase: launch both peers, wait for both, then leave them stopped for the next phase.
phase() {
  local name="$1" flow="$2" timeout="$3" save="$4"
  shift 4
  kill_game; sleep 2
  echo "== phase $name (flow=$flow, save=$save) =="
  local common=("--rdps-mp-flow=$flow" "--rdps-scenario=$SCENARIO" "--rdps-character=$CHARACTER"
                "--rdps-mp-timeout=$timeout" "--rdps-mp-save=$save" "$@")
  # --fastmp with no value is enough for the host: what it buys is PlatformType.None in StartHostAsync, so the lobby
  # is ENet rather than Steam. The client asks for =join, which makes the game open and dial the join screen itself.
  local client_fastmp=--fastmp
  [ "$flow" = reload ] && client_fastmp=--fastmp=join
  ( cd "$GAME_DIR" && exec ./SlayTheSpire2.exe --fastmp --rdps-mp=host   "--rdps-meter=$HOST_METER" \
      "${common[@]}" ) >"$OUT/$name-host.log" 2>&1 </dev/null &
  local h=$!
  sleep 8
  ( cd "$GAME_DIR" && exec ./SlayTheSpire2.exe "$client_fastmp" --rdps-mp=client "--rdps-meter=$CLIENT_METER" \
      "--clientId=$CLIENT_ID" "${common[@]}" ) >"$OUT/$name-client.log" 2>&1 </dev/null &
  local c=$!
  ( sleep "$((timeout + 180))"; kill_game ) & local w=$!
  wait "$h"; wait "$c"; kill "$w" 2>/dev/null
}

phase a fresh  "${PHASE_A_TIMEOUT:-200}" true
phase b reload "${PHASE_B_TIMEOUT:-180}" false

echo
echo "===================== verdict ====================="
for f in a-host a-client b-host b-client; do
  v=$(grep -o "=== MP [A-Z]* ===.*" "$OUT/$f.log" 2>/dev/null | tail -1)
  echo "$f: ${v:-NO SENTINEL (crashed or never got there)}"
  echo "   divergence=$(grep -c 'State divergence' "$OUT/$f.log" 2>/dev/null)" \
       "internal-error=$(grep -c 'Exception loading multiplayer run\|ReturnToMainMenuWithInternalError' "$OUT/$f.log" 2>/dev/null)"
done
echo
echo "-- anything the mod complained about --"
grep -hn "\[RdpsMeter\] \(Could not\|Failed\)" "$OUT"/*.log | head -20 || true
echo "-- the game's own load-path errors --"
grep -hn "Exception loading multiplayer run\|Failed to load multiplayer save\|Multiplayer run save validation failed" "$OUT"/*.log | head -20 || true

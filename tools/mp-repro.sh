#!/usr/bin/env bash
# Runs a scripted two-peer co-op session against the installed game and reports what the two peers logged.
#
# Both processes run the same harness build of the mod; what differs is the command line. By default the host is
# metered and the client is not, because the game only forbids a mods mismatch between peers for mods that declare
# themselves gameplay-affecting - the meter declares it is not, so "one player has it, the other doesn't" is the
# configuration players actually hit, and it is the only one in which a difference the meter causes surfaces as a
# checksum divergence rather than being computed identically on both sides.
#
# Judged from the logs, never from the screen. Each peer prints MP READY / MP COMBAT / MP COMPLETE / MP FAILED, and
# the game prints its own "State divergence detected!" when the peers stop agreeing.
set -uo pipefail

cd "$(dirname "$0")/.."

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

GAME_DIR="/mnt/c/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2"
MOD_DIR="$GAME_DIR/mods/RdpsMeter"
VERSION=$(python3 -c "import json; print(json.load(open('$GAME_DIR/release_info.json'))['version'].lstrip('v'))")
STS2="lib/sts2-$VERSION.dll"

SCENARIO="${1:-echoform}"
CHARACTER="${2:-4}"        # 0 Ironclad, 1 Silent, 2 Regent, 3 Necrobinder, 4 Defect (Echo Form is a Defect card)
HOST_METER="${HOST_METER:-on}"
CLIENT_METER="${CLIENT_METER:-off}"
TIMEOUT="${TIMEOUT:-300}"

OUT="${OUT:-/tmp/rdps-mp}"
mkdir -p "$OUT"

if [ ! -f "$STS2" ]; then
  echo "No captured assembly for game $VERSION - run tools/capture-sts2.sh first" >&2
  exit 1
fi

echo "== building harness against $STS2 =="
dotnet build -c Release -p:Harness=true -p:Sts2Ref="$STS2" >/dev/null || exit 1

OUT_BIN=".godot/mono/temp/bin/Release"
mkdir -p "$MOD_DIR"
cp "$OUT_BIN/RdpsMeter.dll" "$OUT_BIN/RdpsMeter.pdb" RdpsMeter.json "$MOD_DIR/"
# Deliberately no autotest.marker: that arms the single-player self-test, which spawns fake players mid-combat and
# would be an enormous divergence of its own. The two-peer harness is armed by --rdps-mp instead.
rm -f "$MOD_DIR/autotest.marker"

kill_game() { /mnt/c/Windows/System32/taskkill.exe /IM SlayTheSpire2.exe /F >/dev/null 2>&1 || true; }
trap kill_game EXIT
kill_game
sleep 1

# Launched inline rather than from a function returning the PID: a $(...) around the launch blocks until every
# writer to the substitution pipe is gone, and a Windows process started through WSL interop holds one for its whole
# life - so the host would start, the substitution would never return, and the client would never launch at all.
args() {
  echo --fastmp
  echo "--rdps-mp=$1"
  echo "--rdps-meter=$2"
  echo "--rdps-scenario=$SCENARIO"
  echo "--rdps-character=$CHARACTER"
  echo "--rdps-mp-timeout=$TIMEOUT"
}

echo "== launching host (meter=$HOST_METER) and client (meter=$CLIENT_METER), scenario=$SCENARIO =="
mapfile -t HOST_ARGS < <(args host "$HOST_METER")
mapfile -t CLIENT_ARGS < <(args client "$CLIENT_METER")

( cd "$GAME_DIR" && exec ./SlayTheSpire2.exe "${HOST_ARGS[@]}" ) >"$OUT/host.log" 2>&1 </dev/null &
HOST_PID=$!
sleep 8
( cd "$GAME_DIR" && exec ./SlayTheSpire2.exe "${CLIENT_ARGS[@]}" ) >"$OUT/client.log" 2>&1 </dev/null &
CLIENT_PID=$!

# A wall-clock cap on top of the per-peer timeout: a peer that wedges below the harness's own _Process - a hung
# await, a modal popup - never reaches its timeout check, and the launcher must still come back.
( sleep "$((TIMEOUT + 180))"; kill_game ) &
WATCHDOG=$!

wait "$HOST_PID"
wait "$CLIENT_PID"
kill "$WATCHDOG" 2>/dev/null

echo
for peer in host client; do
  echo "===================== $peer ====================="
  grep -nE "MP\(|=== MP |State divergence|StateDivergence|Unhandled|Exception|RdpsMeter\]" "$OUT/$peer.log" | tail -40
done

echo
echo "===================== verdict ====================="
for peer in host client; do
  if grep -q "=== MP COMPLETE ===" "$OUT/$peer.log"; then verdict="COMPLETE";
  elif grep -q "=== MP FAILED ===" "$OUT/$peer.log"; then verdict="FAILED";
  else verdict="NO SENTINEL (crashed or never got there)"; fi
  diverged=$(grep -c "State divergence" "$OUT/$peer.log")
  echo "$peer: $verdict; divergence lines: $diverged; log: $OUT/$peer.log"
done

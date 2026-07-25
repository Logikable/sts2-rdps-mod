#!/usr/bin/env bash
# Checks that every shipped Harmony patch still binds against each game assembly under lib/, without launching the
# game. Harmony resolves patch targets and their parameters by name at load time, so a game update can silently break
# a patch; this catches that for every game version whose sts2.dll is present, from the assemblies alone.
#
# Covers lib/sts2.dll (the version the mod is built against) plus any lib/sts2-<ver>.dll kept for older/newer builds.
# Add a version by dropping its sts2.dll in as lib/sts2-<ver>.dll. Exits non-zero if any patch fails to bind anywhere.
set -euo pipefail

cd "$(dirname "$0")/../.."

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

dotnet build -c Release >/dev/null
(cd tools/binding-verifier && dotnet build -c Release >/dev/null)

MOD=".godot/mono/temp/bin/Release/RdpsMeter.dll"
VERIFIER="tools/binding-verifier/bin/Release/net9.0/BindingVerifier.dll"

rc=0
for sts2 in lib/sts2.dll lib/sts2-*.dll; do
  [ -e "$sts2" ] || continue
  echo "=================================================="
  dotnet "$VERIFIER" "$MOD" "$sts2" lib || rc=1
done

echo "=================================================="
if [ "$rc" -eq 0 ]; then
  echo "ALL VERSIONS OK"
else
  echo "SOME VERSIONS FAILED - a patch will not bind on a game version above"
fi
exit "$rc"

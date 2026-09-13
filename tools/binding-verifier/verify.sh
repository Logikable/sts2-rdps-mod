#!/usr/bin/env bash
# Checks that the mod still works against each game assembly under lib/, without launching the game, in two passes.
#
# First it compiles the mod against each captured assembly in turn. A release is built against one version and runs on
# all of them, so anything the source touches directly has to exist on every one - and a member the reference has and
# an older build does not compiles clean and throws MissingMethodException in the player's game. That is how 0.1.28
# shipped a CardPlay.Player read in a Hook.BeforeCardPlayed prefix: on 0.107.1 every card played cost its energy and
# then hung, because the prefix threw before the play began. Differences between versions belong behind reflection or
# a Prepare gate, and this pass is what enforces that.
#
# Then it applies every shipped Harmony patch against each assembly. Harmony resolves patch targets and their
# parameters by name at load time, so a game update can silently break a patch; this catches that from the assemblies
# alone. The two passes see different things: the compiler reads patch bodies but not Harmony's name-based binding,
# and the verifier binds targets but never runs a body.
#
# Covers lib/sts2.dll (the version the mod is built against) plus any lib/sts2-<ver>.dll kept for older/newer builds.
# Add a version by dropping its sts2.dll in as lib/sts2-<ver>.dll. Exits non-zero if any version fails either pass.
set -euo pipefail

cd "$(dirname "$0")/../.."

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

MOD=".godot/mono/temp/bin/Release/RdpsMeter.dll"
VERIFIER="tools/binding-verifier/bin/Release/net9.0/BindingVerifier.dll"

rc=0

for sts2 in lib/sts2.dll lib/sts2-*.dll; do
  [ -e "$sts2" ] || continue
  echo "=================================================="
  echo "Compiling against $(basename "$sts2")"
  # The build's own exit status decides, not a grep over its output: a build that fails for some other reason prints
  # no error line and would otherwise read as a pass.
  if log=$(dotnet build -c Release -p:Sts2Ref="$sts2" 2>&1); then
    echo "ok"
  else
    echo "$log" | grep -E ": error " | sort -u || echo "$log" | tail -20
    echo "FAIL  the mod does not compile against this version"
    rc=1
  fi
done

# Leave the shipping build in place - built against the default reference - as the one the verifier binds.
dotnet build -c Release >/dev/null
(cd tools/binding-verifier && dotnet build -c Release >/dev/null)

for sts2 in lib/sts2.dll lib/sts2-*.dll; do
  [ -e "$sts2" ] || continue
  echo "=================================================="
  dotnet "$VERIFIER" "$MOD" "$sts2" lib || rc=1
done

echo "=================================================="
if [ "$rc" -eq 0 ]; then
  echo "ALL VERSIONS OK"
else
  echo "SOME VERSIONS FAILED - the mod will not compile against, or will not bind on, a game version above"
fi
exit "$rc"

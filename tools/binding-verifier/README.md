# Binding verifier

Checks that every Harmony patch the mod ships still **binds** against a given game
assembly, without launching the game.

## Why

Harmony resolves each patch's target method and its parameters *by name at load
time*. A game update that renames a method, changes an overload, adds a parameter,
or removes a type silently breaks a patch - the mod loads but that feature quietly
does nothing. Building against an older `sts2.dll` does **not** catch this: patch
targets use `nameof(...)`, so they compile fine and only fail when Harmony binds
them at runtime. That is exactly how the 0.107.1 `cardPlay` break slipped through.

The self-test harness catches numeric regressions, but only on the one game version
it can run on. This tool covers the other half - patch binding - for every version
whose assembly is on hand, from the assemblies alone.

## How it works

It loads the shipped `RdpsMeter.dll` against a chosen `sts2.dll`, then applies the
real Harmony patches over the mod's patch classes - the same thing the game's mod
loader does. A patch that `Prepare()`-gates itself out on a version is reported as
`skip`; only a thrown exception is a `FAIL`. No game, no Godot runtime, no Steam.

## Running

```
./verify.sh
```

Verifies against `lib/sts2.dll` (the version the mod is built against) and every
`lib/sts2-<ver>.dll` alongside it. Exits non-zero if any patch fails to bind on any
version.

## Adding a game version

Drop that build's `sts2.dll` into `lib/` as `lib/sts2-<ver>.dll` (they are
gitignored - MegaCrit's assembly is not redistributable). `verify.sh` picks it up
automatically.

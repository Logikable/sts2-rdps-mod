# rDPS Meter

FFXIV-style rDPS damage meter for Slay the Spire 2 co-op. Damage gained from a
teammate's buffs and debuffs (Vulnerable, Flanking, Poison, Doom, ...) is
credited to the player who applied them, so support play shows up on the meter.
A draggable in-combat overlay shows each player's rDPS and share of the team's
damage with an instant hover breakdown; it persists between fights and toggles
between the current combat and the running session total.

Built against **Slay the Spire 2 v0.109.0** (beta branch).

## Attribution model

- Counterfactual: when a hit resolves, the damage is recomputed with each
  externally-applied modifier removed; the difference is that modifier's
  contribution. If contributions overlap (stacked multipliers), they are
  scaled down proportionally so they sum to the total external gain.
- Personal buffs stay with the dealer; only modifiers applied by *another*
  player move on the meter.
- When several players contributed stacks to one debuff (e.g. Vulnerable),
  its contribution is split pro-rata by live stacks contributed.

## Building

The mod compiles against the game's own assembly, `sts2.dll`, which is
MegaCrit's proprietary code and is **not** included in this repository (it is
gitignored). Supply your own copy from your game install before building:

```
cp "<Slay the Spire 2>/data_sts2_windows_x86_64/sts2.dll" lib/sts2.dll
```

`<Slay the Spire 2>` is your Steam install directory, e.g.
`C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2`. Keep the copy
in sync with the game version you target — the reference DLL determines which
game build the mod is compiled against.

`./deploy.sh` then builds with the .NET 9 SDK at `~/.dotnet` and copies the mod
into the game's `mods/RdpsMeter/` folder. The other two dependencies in `lib/`
— `0Harmony.dll` (Harmony, MIT) and `GodotSharp.dll` (Godot, MIT) — are
redistributable and are checked in.

## Installing

Create `mods/RdpsMeter/` in your game install and drop in the built
`RdpsMeter.dll` (from `.godot/mono/temp/bin/Release/`) alongside
`RdpsMeter.json`, or just run `./deploy.sh`. Launch the game; the overlay
appears when a combat starts.

Packaged builds are named `RdpsMeter-<game version>-<mod version>.zip` and
contain a single `RdpsMeter/` folder with the DLL and manifest.

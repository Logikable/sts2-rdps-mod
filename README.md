# rDPS Meter

FFXIV-style rDPS damage meter for Slay the Spire 2 co-op. Damage gained from a
teammate's buffs and debuffs (Vulnerable, Flanking, ...) is credited to the
player who applied them, so support play shows up on the meter.

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

`./deploy.sh` builds with the .NET 9 SDK at `~/.dotnet` and copies the mod
into the game's `mods/RdpsMeter/` folder. `lib/` holds reference copies of
the game's own assemblies (sts2.dll, 0Harmony.dll, GodotSharp.dll) from
`data_sts2_windows_x86_64/`; refresh them after a game update.

## Status

Phase 1: logs every resolved damage event (dealer, target, settled damage,
source card, dealer NetId) and every real damage calculation with its
modifier list, to validate the attribution surface. No meter UI yet.

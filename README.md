# rDPS Meter

FFXIV-style rDPS damage meter for Slay the Spire 2 co-op. Damage gained from a
teammate's buffs and debuffs (Vulnerable, Flanking, Poison, Doom, ...) is
credited to the player who applied them, so support play shows up on the meter.
A draggable in-combat overlay shows each player's rDPS and share of the team's
damage with an instant hover breakdown; it persists between fights and opens on
the running session total, toggling to the current combat or any single fight.

Built against **Slay the Spire 2 v0.109.1** (beta branch). v0.109.0 ships a
code-identical assembly, so one build covers both.

## Attribution model

- Counterfactual: when a hit resolves, the damage is recomputed with each
  externally-applied modifier removed; the difference is that modifier's
  contribution. If contributions overlap (stacked multipliers), they are
  scaled down proportionally so they sum to the total external gain.
- Personal buffs stay with the dealer; only modifiers applied by *another*
  player move on the meter.
- When several players contributed stacks to one debuff (e.g. Vulnerable),
  its contribution is split pro-rata by live stacks contributed.
- Strength is credited to the card or potion that granted it (Coordinate,
  Blaze, a thrown Flex Potion), since every source stacks into one shared
  Strength pool and the pool's name alone would not say who did what. Other
  effects are named after themselves — a Vulnerable share reads "Vulnerable"
  whichever card applied it.

## Languages

The meter follows the language the game is set to. English and Simplified
Chinese (简体中文) are translated; any other language falls back to English.
Names the meter borrows from the game — cards, potions, relics, powers,
enemies — are always shown in the game's own words, and the overlay uses the
font your language needs, so non-Latin text renders properly.

Translations are the JSON tables in `localization/`, one per language, named
with the game's three-letter code (`eng`, `zhs`, `jpn`, `kor`, `fra`, ...).
To add one, copy `eng.json`, translate the values (leave the keys and the
`{0}`/`{1}` placeholders alone), and run `tools/check-localization.sh`. The
tables are compiled into the DLL, so adding a language means rebuilding.

Names taken from the game are recorded in the language they were seen in, so
switching language mid-run leaves fights and buff sources already recorded
under their old names; new ones use the new language.

To try a translation without rebuilding — or to reword one you dislike — drop
the same file in the game's user directory as
`rdps_meter/localization/<code>.json` (on Windows,
`%AppData%\SlayTheSpire2\rdps_meter\localization\`); it overrides the built-in
table key by key.

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

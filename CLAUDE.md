# CLAUDE.md — rDPS Meter mod

Launch `claude` from this directory (`/home/sean/sts2-mod/RdpsMeter`) so the
right project-memory bucket and this file load. Deep context lives in memory
(`sts2_rdps_mod.md`, `wsl_dotnet.md`).

## Git

I manage the whole repo. **Commit AND push after each change** — no approval
needed for this repo (unlike the `ms` project). One logical change per commit,
step by step. Never force-push (the Claude Code classifier blocks it; the user
runs those via `! ...`).

**Never re-add `lib/sts2.dll`** — it's MegaCrit proprietary, gitignored, and
purged from history. `0Harmony.dll` / `GodotSharp.dll` stay tracked (MIT).

## Releasing a new version

Every release ships to **all three**: GitHub, Steam Workshop, and Nexus Mods.
Don't do only one — do all three, or say explicitly which is blocked and why.

1. Bump `version` in `RdpsMeter.json`; update the `changeNote` in
   `workshop/workshop.json`.
2. Commit + push to origin.
3. **Nexus** (I can run this): `./nexus/publish.sh` (dry-run first with
   `--dry-run`). Fully automatable from WSL — uploader action + API key are
   already installed.
4. **Steam**: `bash workshop/sync.sh` to re-stage, then tell the user to run
   `.\ModUploader.exe upload -w RdpsMeter` from Windows
   `C:\Users\Sean\sts2-workshop` (I can't run the Windows uploader).

## Text on screen

Every string the meter shows goes through `Loc.T` and lives in
`localization/<lang>.json` (compiled into the dll as embedded resources) — never
inline in the UI code. Chinese (`zhs`) is the language that matters most after
English. Run `tools/check-localization.sh` after touching a table. Any new
`Label` needs `Loc.ApplyFont(label, "font")`, or non-Latin text draws as boxes.

## Building

Set `DOTNET_ROOT=$HOME/.dotnet`, `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, and
put `~/.dotnet` on PATH (`deploy.sh` / `package.sh` bake these in). Ship builds
are plain `-c Release` (harness compiled out); `-p:Harness=true` enables the
dev harness. Cross-version: run the binding verifier under `tools/` against each
captured `sts2.dll` before shipping.

## Overlay width

Both windows are one fixed width (`Width` in `RdpsOverlay.cs`). Text too long
for its space is cut short — never widen the window to fit content.

The width comes **only** from the panel's `CustomMinimumSize`. Row content
cannot influence it: each row is a plain `Control` whose children are anchored
`FullRect`, and a plain `Control`'s minimum size is just its
`CustomMinimumSize` — anchored children contribute nothing. So a long name or a
big number can overflow its column, but it can never reflow the window. Any
width change is therefore something assigning `CustomMinimumSize`, not content.

## Damage accounting

The game's `DamageResult` splits a swing into three **disjoint** parts:
`BlockedDamage`, `UnblockedDamage` (HP actually lost) and `OverkillDamage` (the
excess on a killing blow). All three together sum to the post-modifier pre-block
swing, which is also `HitAttribution.Total`.

The meter counts `Blocked + Unblocked` — damage into block is damage done;
overkill is not. That happens to match the game's own `DamageResult.TotalDamage`,
but keep computing it explicitly: the ledger's choice is a product decision, not
a wrapper around whatever that property happens to mean.

Because dealt then equals `Total` on every hit except a kill, the pre-block
attribution shares normally carry over unchanged, and on a killing blow they all
scale down in the same proportion as the wasted excess.

## Running the self-test in game

It does work, contrary to an earlier conclusion. Build `-p:Harness=true`, copy
the dll into the game's `mods/RdpsMeter/`, and drop an empty `autotest.marker`
beside it; the mod then starts a run, enters a combat, runs every scenario and
quits. Launch the game **directly** — `cd "<game dir>" && ./SlayTheSpire2.exe`
from WSL — and read stdout for `HARNESS COMPLETE` / `HARNESS FAILED`.

Two traps. `--headless` never reaches the main menu (exits 5 after ~2s), so the
harness never fires. And the launch is flaky: roughly half of attempts exit at
~3s having logged only ~65 lines, ending at the `SteamStatsManager` line — that
is a failed launch, not a failed test, so just retry until the log runs long.
Afterwards remove the marker and redeploy a plain `-c Release` build, or normal
play keeps auto-running the harness.

Two things a new scenario must respect. The fight has **one** enemy, and killing
it ends the combat — after which `CreatureCmd.Damage` stops running the hooks the
ledger listens on, so every later scenario silently records nothing. A scenario
needing a killing blow should hand a built `DamageResult` straight to
`ApplyHit` instead. And don't switch the harness to a multi-monster encounter to
get around that: the all-enemy effects (Outbreak) and the Doom kill are written
against a single enemy and both break.

## Checking a new game version

When the game updates, `tools/capture-sts2.sh` grabs the new `sts2.dll`. Three
checks answer "does the mod still work": rebuild against it (catches changed
signatures the mod calls directly), run the binding verifier (catches renamed
Harmony targets), and diff full decompiles of the old and new assemblies —

```
ilspycmd -p -o out-<ver> -r lib lib/sts2-<ver>.dll   # ~40s for a 9 MB assembly
diff -rq out-<old> out-<new>
```

— which is the only one that catches *behavioural* changes, like the new
multiplayer card 0.108.0 added. A patch release often touches only the `.pck`,
leaving the managed assembly identical apart from the commit hash in
`AssemblyInfo.cs`; that diff says so in seconds and no rebuild is needed.

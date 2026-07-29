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
   `workshop/workshop.json`. **Only** `changeNote` — every other field there is
   `null` on purpose, which the Steam uploader reads as "leave unchanged". The
   store listing (title, description, tags) is maintained on the Steam page
   itself; filling one of those in here overwrites the web copy on next upload.
   Nexus's page text is likewise web-only — `nexus/publish.sh` uploads a file
   and bumps the version, and never touches the description.
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

## Overlay drawing traps

Two that cost a round trip each, both silent — the code looks right and the
screen disagrees.

A **`MenuButton` constructs itself flat**, and a flat button draws no stylebox
at all. Style it however you like; nothing appears until `Flat = false`.

**Two translucent layers don't composite to the same shade as one.** The
breakdown's split bar draws its own segment over the fainter one behind it, so
the same colour drawn as a single solid bar comes out duller. Where two modes
must match, build the *same* bar and vary what is split off it — don't swap in
`EffectBackground` for `SplitBackground`.

## Overlay persistence

The window is not tied to combat. `RdpsOverlay.ShouldShow` asks the **run**
(`RunLedger.HasData`), never the picked view — so ending a fight, walking into a
shop, or picking a fight that happens to be empty must not make it vanish; only
a run with nothing recorded yet draws no window. At startup `LoadLastPlayed`
adopts the last run played, so the meter is readable from the main menu on.

Which run that is comes from `rdps_meter/last-run.txt`, not from file modified
times — those are second-resolution, so two runs saved in the same second would
order arbitrarily and any test over them would flake. The mtime scan is only the
fallback for a missing pointer.

On the Run History page the meter follows the map point being looked at
(`RunHistoryLink`). The two sides share **no key**: the ledger files a combat
under act/coord/room, while a saved `RunHistory` keeps only map point types,
rooms and monsters — no coordinates. What they share is **order**, so the nth
fight of act N on the page is the nth combat the ledger recorded for act N.
Count *combat rooms*, not map points (one point can hold several rooms), and
count per act so a missing early fight doesn't shift later acts. A page showing
a different run (`RunHistory.Seed != RunLedger.LoadedRunId`) never resolves to a
combat — an empty meter beats another run's numbers.

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

## Block accounting

Block counts **only when something hits it**. Nothing is booked as it is gained
— `BlockPool` just remembers the gain, itemized by who paid for it — and the
meter moves in `Creature.DamageBlockInternal`, the one place block is spent.
That is what makes overblock free: block still standing when the turn ends is
dropped unbooked.

Which gain gets the credit is two passes, in this order: the **wearer's own**
block first, oldest gain first (alone, that is plain FIFO across the turn's
cards, so the *later* excess is what goes uncounted); then whatever they could
not cover themselves, split **pro-rata** among the teammates who topped them up.

The pool is reconciled against the creature's real `Block` before every read, so
every path that removes block without telling us — the turn's own expiry, Expose,
Burrowed — needs no patch of its own.

Naming a gain is the awkward part. A card names itself through `cardSource`. A
potion comes from `PotionSource.Sole()`, because a thrown Block Potion reaches
the funnel knowing only its *receiver*, and in co-op that is not the thrower. A
relic or power has neither, and its hook is one the game never pushes onto its
own executing-model stack — so `BlockSource` reads the **call stack** in the
prefix of `CreatureCmd.GainBlock`, the last moment the granting model's frame is
still standing (the rest of that method is async). Push and pop pair exactly
because both sides run only for card-less gains, and a card preview — which
always carries its card — never touches either.

Dexterity is pooled the way Strength is: every source stacks into one
`DexterityPower`, so `PowerOwnershipPatches.GrantedBy` records what granted each
share and the meter can say "Dexterity Potion" instead of "Dexterity".

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

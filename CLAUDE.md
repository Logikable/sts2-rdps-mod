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

**Icons are drawn, never typed.** Every mark in the chrome — the arrowheads, the
picker's caret, the minimize plus and minus — is a polygon or a rect, because a
character is only as portable as the font behind it: those arrows were U+25C0 /
U+25B6 / U+25BE until a Linux install turned all three into hex-code boxes.
Don't reach for `Font.HasChar` to detect it: measured on Windows, where those
three characters drew as perfectly good arrowheads, it reports `False` for all
three — it answers for the font object you ask and not for the fallback chain
Godot draws through. A fallback conditioned on it would have fired on machines
that were fine. Drawing removes the dependency instead of detecting it, which is
the only reliable move. Keep `eng.json` pure ASCII for the same reason; non-Latin
text belongs in the other tables, where the game's substitute font does the work.

## Building

Set `DOTNET_ROOT=$HOME/.dotnet`, `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, and
put `~/.dotnet` on PATH (`deploy.sh` / `package.sh` bake these in). Ship builds
are plain `-c Release` (harness compiled out); `-p:Harness=true` enables the
dev harness. Cross-version: run the binding verifier under `tools/` against each
captured `sts2.dll` before shipping.

## Overlay width

Both windows are one fixed width (`Width` in `RdpsOverlay.cs`). Text too long
for its space is cut short — never widen the window to fit content. The one
exception is deliberate and content-independent: minimized, the panel's
`CustomMinimumSize` is reassigned to a `MinimizedSide` square. That is a mode
switch, not content reflow, which is the thing the rule forbids.

The width comes **only** from the panel's `CustomMinimumSize`. Row content
cannot influence it: each row is a plain `Control` whose children are anchored
`FullRect`, and a plain `Control`'s minimum size is just its
`CustomMinimumSize` — anchored children contribute nothing. So a long name or a
big number can overflow its column, but it can never reflow the window. Any
width change is therefore something assigning `CustomMinimumSize`, not content.

## Overlay drawing traps

Three that cost a round trip each, all silent — the code looks right and the
screen disagrees.

A **`MenuButton` constructs itself flat**, and a flat button draws no stylebox
at all. Style it however you like; nothing appears until `Flat = false`.

**Two translucent layers don't composite to the same shade as one.** The
breakdown's split bar draws its own segment over the fainter one behind it, so
the same colour drawn as a single solid bar comes out duller. Where two modes
must match, build the *same* bar and vary what is split off it — don't swap in
`EffectBackground` for `SplitBackground`.

**The panel's height only ever grows.** Godot enlarges an anchored control to
fit its minimum size but never shrinks it back, and the panel hangs off a
`CanvasLayer`, so no container does it either. Left alone the window keeps the
height of the tallest breakdown it has ever drawn. `_Process` therefore assigns
`_panel.Size` down to `GetCombinedMinimumSize()` every frame — safe, because
Godot clamps that back up, so it can only remove space nothing asked for.

Note the asymmetry with the width above: width is pinned *by construction*
(nothing can influence `CustomMinimumSize`), height is corrected *every frame*.
Don't debug a stuck height by hunting for what set it — `Size` and
`GetCombinedMinimumSize()` disagreeing is the whole bug, so print both.

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
count per act so a missing early fight doesn't shift later acts.

A run other than the loaded one is read back off disk by `ArchivedRun`, keyed by
`RunHistory.Seed`. This used to refuse — only the run in memory resolved — which
showed a meter of zeroes over every finished run while its breakdown sat unread
in the save folder. Two things keep that safe. **A combat key is unique only
within a run**, so the run id rides on `HistoryFight` and the reader picks the
ledger from it; a reader that assumed the loaded run would return today's damage
under an old fight's name and look perfectly healthy in a test where only one
run had data. And the run id *inside* the file is checked against the one asked
for, because two seeds can fold to the same filename. A run with no file at all
is still the empty meter it always was — that case is real (older than the mod,
or pruned), just no longer the common one. Pruning is sized for browsing (60
runs), not for the handful you have in progress: a pruned file is the zeroes bug
coming back.

## Snapshots are cached — every mutator must `Touch()`

The overlay asks for a snapshot every frame; the numbers only move when a hit
lands. So `CombatLedger` caches its rendered rows against a `_revision`, and
`RunLedger.TotalSnapshot` caches the whole-run fold against `(structure,
per-combat revisions)`. Total is the **default** view and used to re-merge every
combat in the run sixty times a second — a cost that grew with every fight won,
so the meter was slowest deep into a run.

**The rule: anything that writes to `_ledgers` or `_names` calls `Touch()`.**
There are six such places (`Reset`, `ApplyHit`, `ApplyBlock`, `ApplyDot`,
`RecordName`, and `AccumulateInto` — which touches its *target*, not itself).
Forgetting one does not crash; it makes a number quietly stop moving, which is
why `SnapshotCacheScenario` walks every write path and re-reads after each.

The structural counter is **not** redundant with the revisions, though it looks
it. Every combat loaded from a file starts at revision zero, so two different
runs that both have one combat present *identical* fingerprints — without the
counter, loading run B after run A returns run A's numbers. The revision list is
also compared element-wise rather than hashed: hashing would collide eventually,
and in a damage meter a collision means silently wrong numbers.

`Snapshot()` hands the same list to every caller. That is safe only because rows
are never written to after construction — keep it that way.

## What a saved run has to carry

Anything the meter draws that isn't a number has to be **in the file**, because
after a restart there is no live `Player` to read it off. Class colour and icon
were read off `Player.Character` and never written down, so a reopened game
restored the numbers and drew every row the neutral grey.

Persist the **`ModelId`**, never the resolved asset. `CharacterVisuals` recovers
`NameColor` and `IconTexture` from the prototype in `ModelDb` at load — a saved
colour would freeze whatever the class looked like the day the file was written,
and a saved texture path breaks the moment the game moves its art. Both
properties belong to the prototype, not to a run's copy, so the lookup answers
exactly as the live model would. Resolution is best-effort in both directions:
an id that no longer names anything falls back to grey rather than throwing
while drawing.

The roster is **run-level, recorded per combat**. Not once per run, because the
meter can be installed mid-run and a co-op player can join one already going, so
there is no single moment the whole party is known; recorded before
`BeginCombat`, because that is what writes the file.

Every run saved before the roster existed has numbers and no party, and those
are exactly the runs the history page is for — so `ArchivedRun.AdoptRoster`
fills the gaps from `RunHistory.Players`, which records each player's character
itself. Only *missing* entries; a file that saved its own roster keeps it. Ship
a persistence change without this and the old data comes back correct in grey,
which reads as the bug not being fixed.

The trap when a row can come from two runs: the local player keeps their net id
across runs while the character changes, so the live visual cache is *wrong* for
an archived run rather than merely unhelpful. `VisualFor` skips the cache
entirely when the history page is on another run, and `_shownRun` drops the
cached rows when the page moves between runs — a `Row` bakes its colour in at
construction, and paging between old runs doesn't bump `RunLedger.Generation`.

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
quits.

**Build it against the installed game, not the default reference.** The
scenarios call game methods directly, so the harness needs
`-p:Sts2Ref=lib/sts2-<installed ver>.dll` — `lib/sts2.dll` is an older capture
and the harness does not compile against it (`PoisonPower.Trigger` is the one
that bites). Ship builds are unaffected: they reach version differences through
Prepare-gates, not the compiler. Check which version is installed by comparing
`md5sum` of the game's `sts2.dll` against the captures in `lib/`. Launch the game **directly** — `cd "<game dir>" && ./SlayTheSpire2.exe`
from WSL — and read stdout for `HARNESS COMPLETE` / `HARNESS FAILED`.

Two traps. `--headless` never reaches the main menu (exits 5 after ~2s), so the
harness never fires. And the launch is flaky: roughly half of attempts exit at
~3s having logged only ~65 lines, ending at the `SteamStatsManager` line — that
is a failed launch, not a failed test, so just retry until the log runs long.
Afterwards remove the marker and redeploy a plain `-c Release` build, or normal
play keeps auto-running the harness. Do that only once the run has finished: a
launch with no marker loads the mod and goes straight to the main menu, and the
log then shows `Initialized` with no `Auto-harness armed` line — which reads
like a broken harness and is only a missing marker.

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

The call stack gives a **name but no owner** — it matches the frame's type against
the model database, and a prototype belongs to nobody — so the credit falls back
to whoever is *wearing* the block. That is right for every source that grants to
its own owner (Plating, Rampart, Frost) and wrong for one that gives block away:
Beacon of Hope hands half of your block to each teammate, and the giver is who
the meter should show. `ForeignBlockGrant` carries the owner for the span of such
a hook and outranks both other routes, being the innermost. Run
`tools/find-attribution-gaps.py` after a game update — it lists every source that
grants block to something other than its owner, which is the set that needs this.

Dexterity is pooled the way Strength is: every source stacks into one
`DexterityPower`, so `PowerOwnershipPatches.GrantedBy` records what granted each
share and the meter can say "Dexterity Potion" instead of "Dexterity".

## The Blocked meter is mitigation, not only block

Osty feeds it too. Summon is the Necrobinder's Defend, so damage their pet eats
in their place is booked on the Blocked meter under the pet's name — and a
Necrobinder who summons instead of blocking would otherwise read as having
mitigated nothing. Nothing above applies to it: there is no pool and no
attribution, because the mechanic is a **redirect**, not a gain.

`OstyCmd.Summon` sets the pet's max HP and hangs `DieForYouPower` on it. That
power overrides `ModifyUnblockedDamageTarget`, so in the damage funnel — *after*
the owner's block has already been spent — the unblocked remainder is retargeted
from the owner onto the pet. Three things follow, all the game's rules:

- **Attacks only.** The redirect is gated on `props.IsPoweredAttack()`
  (`ValueProp.Move` and not `Unpowered`), so poison, burn and every other
  non-attack HP loss goes straight through to the owner.
- **Only what the pet actually had counts.** `LoseHpInternal` reports the HP it
  really lost as `UnblockedDamage` and the rest as `OverkillDamage`, which the
  funnel then deals to the owner for real. Same line the damage side draws, and
  for the same reason: mitigation the pet was never big enough to provide is not
  mitigation.
- **Only *redirected* damage counts.** A pet sits in `CombatState.Allies`, so an
  enemy sweep across the side hits it as a target in its own right — damage that
  was never headed for the owner's HP. Keying off the redirect excludes that by
  construction rather than by a list of exceptions, and excludes a pet the owner
  spends deliberately (Sacrifice) for free.

Two patches, because no single point knows both facts:
`ModifyUnblockedDamageTarget` knows a redirect happened but not what it will be
worth, `LoseHpInternal` knows what was absorbed but not who for. The row's name
comes from the game's own `MonsterModel.Title`, so it reads "Osty" without the
mod shipping the word and reads correctly in every other locale — the same route
card rows already take. A test cannot check that name by comparing against the
same expression, which is why the scenario prints what it resolved to.

**The pet may not be the owner's own work.** `Legion of Bone` summons onto every
living ally at once, and `Bone Brew` is a targeted potion that can be thrown at a
teammate — so the Osty in front of you can be made largely of somebody else's
cards, and crediting the owner would read as the Necrobinder having mitigated
nothing. Same shape as Beacon of Hope, and fixed the same way: credit who paid.

`OstyCmd.Summon(ctx, summoner, amount, source)` is the one choke point — all 17
summon sources funnel through it, and it carries both halves, `summoner` being
who receives and `source` what caused it. Take the amount from
`SummonResult.Amount`, not the argument: `Hook.ModifySummonAmount` runs inside
and can change it. And wrap the returned `Task` rather than using a plain
postfix — it is async, so a postfix runs before the pet exists (the same rule the
effect-stack patches follow).

`PetPool` splits each absorb **pro-rata by contributed max HP**, and deliberately
does *not* track which hit point was spent. That is the whole reason this stayed
small. Osty is long-lived with sinks the meter has no business modelling — heals,
revives, an enemy sweep hitting it as a target, Sacrifice — and a FIFO pool over
that would need a reconciliation pass per sink, each one a way to drift. Shares
over lifetime contributions have nothing to reconcile. Note this is the *opposite*
choice from `BlockPool`, and both are right: block is a turn-scoped pot spent to
zero and refilled, so "the wearer's own first, oldest first" is an ordering a
player can actually see, whereas nobody thinks of Osty's sixth hit point as older
than its seventh.

**A revive is the one sink that is not a sink — it is a reset.** `OstyCmd.Summon`
has two arms: a living pet is topped up with `GainMaxHp`, which *adds*, while a
dead or absent one goes through `SetMaxHp`, which *replaces*. So a revived Osty is
made wholly of the summon that brought it back, and the cards that paid for the
hit points it died with bought nothing still standing. The pool was additive
either way, so a teammate who spent one Legion of Bone early kept drawing a share
of every absorb by a pet that was, by then, entirely somebody else's. Everything
else in the paragraph above still holds — the pool has nothing to reconcile
*within* a life, and this is a new life.

Read the game's own condition (`summoner.IsOstyAlive`, in a **prefix** — by the
postfix the pet has been revived and healed, so it always answers "alive") rather
than reproducing the rule: the two arms are one `if` in `OstyCmd.Summon`, and a
copy here would keep agreeing with today's build long after the game stopped
agreeing with the copy. Gate the reset on a summon that actually happened;
`Hook.ModifySummonAmount` can zero one out, and that path returns before touching
max HP at all.

Every scenario written before this summoned onto a *living* pet, so the replacing
arm went untested while looking thoroughly covered — and a Necrobinder's Osty
dying is the ordinary case, not an edge one. The harness now kills it outright
(`LoseHpInternal`, straight to zero) rather than swinging at it: damage aimed at a
pet spends its **owner's** block on the way past, since the funnel resolves
`PetOwner` before blocking, so a killing swing would book block too and the phase
would be asserting against two effects at once.

A potion resolves its owner directly here (`PotionModel.Owner` is the thrower),
unlike in the block funnel where `BlockSource` has to go through `PotionSource` —
there the potion is out of scope by the time block lands, here the game hands us
the model. Don't copy the `PotionSource` dance into a path that was given the
source.

## When a row says "(none)"

Damage with a real dealer but no card is named from the game's executing-model
stack (`PlayerChoiceContext.LastInvolvedModel`). The trap is that the game
pushes onto that stack on **some routes and not others**, so the same effect can
be named or anonymous depending on how it fired. Orbs are the clearest case:
`OrbCmd` pushes when an orb is evoked and when a card triggers its passive, but
the orb's own end-of-turn trigger pushes nothing, so Glass and Lightning were
named only when evoked.

So when a source reads "(none)", don't ask whether the *effect* is handled — ask
whether **that route** pushes. The fix is always the same shape: push the model
onto `ExecutingEffect` (the supplemental stack `EffectSource` falls back to) in a
prefix, and pop it by wrapping the returned `Task`, never in a plain postfix —
an async method returns its Task long before the damage lands.

But "does that route push?" is the *second* question, not the first. **The stack
is read off whichever `PlayerChoiceContext` the damage call was handed**, so a
source that builds its own — `CreatureCmd.Damage(new BlockingPlayerChoiceContext(),
…)` instead of passing the one it was given — starts from an empty stack and is
anonymous no matter what the game pushed. Black Hole is the case: its
`AfterCardPlayed` dispatcher pushes it faithfully and its damage still read
"(none)", because the push landed on a context the damage never travelled on.
A pushing hook is therefore not evidence a source is fine, and that combination
is worth checking first — it is invisible to the reasoning that catches the
ordinary case, and reads as redundant to anyone tidying the patch list.

`tools/find-attribution-gaps.py` derives the whole list from a decompile, and it
has now been wrong twice in the same direction — by looking too narrowly, never
by over-reporting. It read only the override's own body (Black Hole deals through
a private helper the two hooks share), and it classified hooks only from
top-level `Hook.cs` methods, so the 18 `…Late`/`…Early` hooks dispatched inline
from a sibling's body matched nothing and were skipped rather than flagged. Both
are fixed, and unclassifiable hooks now print as `UNKNOWN` instead of vanishing.
When it reports nothing, check what it *declined* to look at before concluding
there is no gap.

Block is immune to all of this: `BlockSource` reads the real call stack instead,
which does not care what anybody pushed.

The mirror-image bug is a row named after the **wrong** thing rather than
nothing, and it happens where two naming windows are open at once. A potion that
draws a card runs the drawn card's triggers *inside* `OnUse`, so a Speedster hit
arrives with both a potion name and an effect name available. **The inner one
wins** — `EffectSource` before `PotionSource` in `AttributionEngine` — because
the inner effect is what dealt the hit; the outer merely caused it. A relic doing
the same draw was never wrong, so a working relic proves nothing about the potion
path. That precedence is only safe because `EffectSource` is set *or cleared* in
the prefix of the hit it names, so it can never be a leftover: a potion's own
damage runs with the potion on top of the stack, which is not a power/relic/orb,
and the entry clears.

`BlockSource` deliberately goes the other way — a potion outranks the call stack
there — and that is not the same call: block from a thrown potion must be
credited to the *thrower*, and only `PotionSource` knows who that was. No
draw-triggered effect grants block or Strength today, so the two orderings do not
currently collide; a game update that adds one would need this thought through
again.

## One modifier can be several models' work

The counterfactual engine credits a buff by removing it from the modifier list
and re-running the pipeline, so it can only credit what the list *mentions*. The
list is built by `Hook.ModifyDamageInternal` from the models whose own
`ModifyDamage*` returned a non-identity value — which means a model that changes
damage without being a listener is invisible to attribution, and its work is
silently folded into whichever listener consulted it.

Vulnerable is the case. `VulnerablePower.ModifyDamageMultiplicative` starts from
its own `DamageIncrease` (1.5) and then hands the running value to three models
that are not listeners, in this order: the dealer's **Paper Phrog** relic
(`+0.25`), the dealer's **Cruelty** (`+Amount/100`), and the target's
**Debilitate**, which doubles whatever bonus has accumulated (`amount +
(amount - 1)`). All four arrive as one number under `VulnerablePower`, so the
whole stack used to be credited to whoever applied Vulnerable and a teammate who
spent a card on Debilitate was credited nothing.

Note the order: Phrog and Cruelty are additive *on the bonus* (0.5 → 0.75 → 1.0),
and Debilitate doubles the total after them (→ 2.0). So Debilitate is worth more
on a dealer wearing Phrog, and the counterfactual measures that by construction
rather than by a synergy rule written anywhere.

`VulnerableBoosts` puts Debilitate and Cruelty back into the list so they become
ordinary credit candidates. **The exclusion is a Harmony prefix on their
`ModifyVulnerableMultiplier`, not a reimplementation of Vulnerable's formula**,
and that is the whole design. A mirror of the arithmetic would reproduce today's
numbers and then go quietly wrong the first time the game adds a fourth booster
or reorders the three — wrong in the direction that still looks right, since
every row would keep summing to the correct total. Letting the game's own method
run with one participant switched off cannot drift: a booster we don't know
about stays folded into Vulnerable's share, which is the behaviour that already
shipped, not a confident lie.

Paper Phrog is deliberately *not* a candidate. It is a `RelicModel` read off
`dealer.Player`, so it is always the dealer's own and there is nobody else to
credit — same reason a self-cast Cruelty is filtered out by the existing
"shares that are all the dealer's" test. Both still participate in every
counterfactual, which is what makes the teammate's Vulnerable credit *larger*
when the dealer is amplifying it. That is the engine's rule everywhere (a buff
is worth the damage it actually enabled), not a Vulnerable special case.

Weak has the identical shape — `WeakPower` folds in **Paper Krane** and
Debilitate's `ModifyWeakMultiplier` — and needs nothing, twice over: Krane is
read off `target.Player`, and the meter never books damage aimed at a player,
while Debilitate's Weak arm needs the power on the *dealer*, which only happens
to an enemy. `CoveredPower` reads other models elsewhere in its file but its
damage hook is self-contained. Those three are the whole class as of 0.110.1;
`grep -rn "public decimal Modify.*Multiplier"` over a decompile re-derives it.

## An extra play is not a modifier

The counterfactual engine can only credit a model the game's modifier list
mentions, and that list holds the models whose `ModifyDamage*` returned something
other than the identity. Tag Team returns nothing of the sort: it raises a *play
count*, so an ally's attack on the marked enemy happens twice and the second time
arrives as an ordinary hit of the ally's with nothing on it to credit. The engine
is blind to it by construction, not by an omission anyone could find by auditing
the modifier list.

The credit is the whole extra play, less whatever other teammates' buffs took of
it — that is, the share that would otherwise have been the dealer's own. Without
the mark that play does not happen, which is the same rule Vulnerable is credited
under; a teammate's Vulnerable on the same hit is still worth exactly what it was
worth, and the two credits cannot overlap because one is taken from what the
other leaves.

Which of the plays are the granted ones is decided by index — the loop runs the
base plays first, so the granted ones are the last of them. When something else
raised the count too (Echo Form doubling the same card) the split between
grantors is arbitrary and harmlessly so, since an echo is the dealer's own work
and stays with the dealer either way. Only the count matters, never which index.

**Drop the grant on the way in, not on the way out.** A play loop can end without
playing every play it planned — `CombatManager.IsOverOrEnding`, the owner dying —
so consuming the grant on the last play leaves one standing after every loop that
ends early, and the card's *next* play would spend it. The prefix on
`Hook.ModifyCardPlayCount` clears it instead: that hook runs once per play loop,
for every card, before any of that loop's plays exist, and `TagTeamPower`'s own
postfix records into the cleared slot from inside the same call.

Echo Form needs none of this, and that is why the gap survived so long: it is the
same mechanic granted to yourself, so the extra play is already the dealer's own
damage and already lands on the right row. A working Echo Form proves nothing
about Tag Team.

**The Blocked meter follows the same rule, down a different funnel.** An attack
that also grants block, played twice off a mark, gives the second lot of block to
the buyer too — for the same reason the damage goes there, since without the mark
that block is never gained. The insertion point is not the modifier list, though:
block credit starts from `BlockAttributionEngine`'s *base strand*, the part left
once every player-owned modifier has taken its share, so that is the part
`BaseStrands` splits out. A teammate's Dexterity on the same gain keeps its own
strand either way. The two halves therefore share nothing but `BuyersOf`, and the
damage half passing says nothing about the block half — each needs its own
scenario.

Two smaller asymmetries, both deliberate. A block strand carries one name for
both the wearer's own breakdown and the given/received rows, so a bought play's
block reads "Tag Team" in the wearer's list rather than the card's name — where
the damage side keeps the card on the dealt row and says "Tag Team" only on the
given/received line. And the block side does not filter out a buyer who is the
card's own owner, where the damage side does: Tag Team cannot double its
applier's own card, so it cannot arise, and a strand owned by the wearer is block
the pool already treats as their own.

## A card you were given is not your own

Blade Symphony and Largesse are the same blind spot one step further on. Tag
Team buys an ally an extra play of a card they own; these two create the card
itself — two Shivs in every ally's hand, a colourless card into one ally's —
and the receiver then plays an ordinary card of theirs. Nothing modified the
hit, so the modifier list holds nothing to remove and the counterfactual engine
credits the giver nothing. `BoughtPlay` treats both mechanics as one thing,
since the answer has the same shape: whoever paid, and the fractions to pay
them. Tag Team is asked first, so a gifted card doubled by a mark splits
arbitrarily between the two — the same harmless call the Echo Form case makes.

**The giver is not on the hook that reports the card.**
`Hook.AfterCardGeneratedForCombat` carries a `creator`, and for Largesse it is
right. `Shiv.CreateInHand` defaults `creator` to the *receiving* player, and
Blade Symphony passes none, so the hook names the person being handed the Shiv
as the person who made it. Nothing else on the hook recovers the giver, and the
card's `OnPlay` is an async method whose state machine is not a patch target
worth having. `PlayInFlight` tracks who is mid-play from `Hook.BeforeCardPlayed`
and `Hook.AfterCardPlayed` instead, and the giver is whoever else is playing
something. It answers only when exactly one other player is: two at once cannot
be told apart this way, and crediting neither is the honest result.

That tracker's entries can go stale — `OnPlayWrapper` returns without reaching
`Hook.AfterCardPlayed` when the owner dies or the combat ends — and that is
survivable rather than lucky. An entry is overwritten by that player's next
play, dropped at combat end, and only ever read to explain a card generated
*for somebody else*, which nothing but a card play does.

**The line is a card that did not exist.** Plot draws an ally cards and Tutor
moves one of theirs into hand; those cards were already the ally's, the
counterfactual is unknowable — which card? — and neither is credited. A created
card has an exact one: this Shiv, four damage. Without that line the same
argument reaches Energy Surge and every tempo card in the game.

The receiver's own Strength and Fan of Knives inflate a gifted Shiv, and all of
that inflation moves to the giver. It follows from the rule the engine uses
everywhere (a play is worth the damage it actually did), and it is the part most
likely to be questioned on screen: a Silent with stacked Strength makes somebody
else's Blade Symphony row large. That is the rule working, not a bug to fix.

## Checking a new game version

When the game updates, `tools/capture-sts2.sh` grabs the new `sts2.dll`. Three
checks answer "does the mod still work": rebuild against it (catches changed
signatures the mod calls directly), run the binding verifier (catches renamed
Harmony targets), and diff full decompiles of the old and new assemblies —

```
ilspycmd -p -o out-<ver> -r lib lib/sts2-<ver>.dll   # ~40s for a 9 MB assembly
tools/decompile-diff.sh out-<old> out-<new>          # which files differ
tools/decompile-diff.sh out-<old> out-<new> <file>   # what actually changed in one
```

— which is the only one that catches *behavioural* changes, like the new
multiplayer card 0.108.0 added. A patch release often touches only the `.pck`,
leaving the managed assembly identical apart from the commit hash in
`AssemblyInfo.cs`; that diff says so in seconds and no rebuild is needed.

Use the script rather than a plain `diff` on a single file. **ilspy renumbers
every compiler-generated name after the point of a change** — async state
machines and lambda display classes are `_003C…__DisplayClass21_0` and become
`22_0` when anything earlier shifts — and it emits `//IL_` offset comments that
move with them. One new field therefore produces hundreds of diff lines with no
behaviour in them, which is exactly the condition under which a real change in
the next file gets skimmed past. 0.110.1 is the case: `AutoSlayer.cs` read as a
rewrite and was a single added `bool`. The filter is blunt on purpose and drops
whole lines mentioning generated names, so it narrows the question rather than
answering it — when a file matters, read it.

Read `Only in` lines as carefully as `Files … differ`. A deleted model is a
compile error you will find anyway, but a **renamed or reworked** one is not:
0.110.0 deleted `OutbreakPower` and rebuilt Outbreak as a skill that applies
Poison and triggers it, so the mod compiled fine against everything it still
referenced while the card's whole payload stopped being attributed. Diff the
`Models.Cards`/`Powers`/`Relics` folders for `CreatureCmd.Damage`, `GainBlock`
and `PowerCmd.Apply` lines specifically — those three are what the meter reads.

**The three checks do not cover the fourth failure: a method that still binds
but is no longer the one that runs.** 0.110.0 moved CombatManager's per-combat
fields into a `CombatTurnState` and gave `StartCombatInternal` /
`EndCombatInternal` private overloads taking one. `EndCombatInternal` kept its
old public no-argument signature as a *wrapper*, so `nameof` still resolved and
the verifier still said ok — but the ordinary end of a fight runs
`CheckWinCondition -> EndCombatInternal(turnState)` and never touches the
wrapper. The meter would have stopped closing out combats with every check
green. When a diff shows a method gaining an overload, ask which one the
*callers* use, and bind to the one they funnel through
(`LifecycleTarget.Resolve`). A rebuild cannot see this and neither can the
binding verifier; only reading the call sites can.

The verifier resolves the game assembly's own dependencies out of `lib/` by
name, so when the game picks up a new one (0.110.0 added Sentry) the fix is to
drop that dll in beside `sts2.dll` — it is gitignored like the rest. Without it
every patch reports as failed at once, which looks like catastrophe and is
actually a missing file.

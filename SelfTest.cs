// Developer-only self-test harness - compiled in only under -p:Harness=true (see RdpsMeter.csproj). It must never
// ship: NoOpChoiceContext below subclasses PlayerChoiceContext, whose abstract members differ between game versions,
// so a shipped copy would fail to load on any version it wasn't built against and take the whole mod down with it.
#if RDPS_HARNESS
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Potions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter;

/// <summary>
/// A solo reproduction of the cross-player attribution paths so the mod can be validated without a second networked
/// player. The behaviour these scenarios target only appears when the creature that applied an effect has a different
/// NetId than the creature dealing the damage - impossible in real single-player, where you only ever benefit from
/// your own effects.
///
/// It mints two throwaway players (NetIds 2 and 3), then runs a series of scenarios that each apply a teammate effect
/// (Vulnerable, Flanking, Strength) and land a real powered attack, asserting the ledger credits the right player the
/// right amount. That drives the full funnel - Hook.ModifyDamage, the AfterModifyingDamageAmount promotion,
/// AfterDamageGiven, and the stack-ownership hooks - exactly as a real co-op hit would. It runs on F9 (see
/// <see cref="SelfTestNode"/>) or from the headless auto-harness, and returns whether every assertion passed.
/// </summary>
internal static class SelfTest
{
    private const decimal Tolerance = 0.01m;

    // The card key the ledger files a cardSource-less test hit under (see CardModel?.Id ?? "(none)").
    private const string NoCard = "(none)";

    private static bool _installed;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            return;
        }

        tree.Root.CallDeferred(Node.MethodName.AddChild, new SelfTestNode());
        _installed = true;
        GD.Print("[RdpsMeter] Self-test armed - press F9 in combat to run the cross-player attribution scenarios");
    }

    /// <summary>
    /// Runs every scenario against the live combat. Returns true only if all of them passed. Safe to call from F9 or
    /// the headless harness; each scenario resets the ledger and heals the target first so they are independent.
    /// </summary>
    public static async Task<bool> RunAsync()
    {
        CombatState? state = CombatManager.Instance?.DebugOnlyGetState();
        if (state == null || !CombatManager.Instance!.IsInProgress)
        {
            GD.Print("[RdpsMeter] Self-test: not in combat, ignoring");
            return false;
        }

        Creature? dealer = state.PlayerCreatures.FirstOrDefault();
        Creature? enemy = state.HittableEnemies.FirstOrDefault();
        if (dealer?.Player == null || enemy == null)
        {
            GD.Print("[RdpsMeter] Self-test: need a player and a hittable enemy");
            return false;
        }

        // Three detached fake players, never added to combat, exist only to be effect appliers with NetIds distinct
        // from the real dealer (NetId 1). Cross-player credit only happens when applier NetId != dealer NetId. Three of
        // them rather than two so a full four-player party can be assembled: the pro-rata splits are the only place the
        // party size is load-bearing, and a two-way split can be right for reasons a three-way one is not.
        var applier2 = new Creature(Player.CreateForNewRun(dealer.Player.Character, dealer.Player.UnlockState, 2uL), 1, 1);
        var applier3 = new Creature(Player.CreateForNewRun(dealer.Player.Character, dealer.Player.UnlockState, 3uL), 1, 1);
        var applier4 = new Creature(Player.CreateForNewRun(dealer.Player.Character, dealer.Player.UnlockState, 4uL), 1, 1);
        var context = new NoOpChoiceContext();

        // First, while the harness combat is still empty: this one hijacks the run ledger to play two runs against each
        // other, and puts the harness's own run back when it is done.
        bool all = DefaultViewScenario();
        all &= TwoRunsScenario();
        all &= PersistentOverlayScenario();
        all &= LastPlayedScenario();
        all &= RunHistoryScenario();
        all &= await MeterModeScenario();
        all &= await VulnerableScenario(context, dealer, enemy, applier2, applier3);
        all &= await InfectionScenario(context, dealer, enemy);
        all &= await FlankingScenario(context, dealer, enemy, applier2);
        all &= await StrengthScenario(context, dealer, enemy, applier2);
        all &= await BlockScenario(context, dealer, enemy, applier2);
        all &= await OrbPassiveScenario(context, dealer, enemy);
        all &= await SpeedsterPotionScenario(context, dealer, enemy);
        all &= await SleightOfFleshScenario(context, dealer, enemy);
        all &= await UnpushedRelicScenario(context, dealer, enemy);
        all &= await ThunderScenario(context, dealer, enemy);
        all &= await BeaconOfHopeScenario(context, dealer, enemy);
        all &= await BlockSpentScenario(context, dealer, enemy);
        all &= await BlockFromPowerScenario(context, dealer, enemy);
        all &= await BlockFromRelicScenario(context, dealer, enemy);
        all &= await BlockFromCardScenario(context, dealer, enemy, applier2);
        all &= await BlockOwnDexterityScenario(context, dealer, enemy, applier2);
        all &= await BlockDexterityScenario(context, dealer, enemy, applier2);
        all &= await BlockProRataScenario(context, dealer, enemy, applier2, applier3);
        all &= await BlockFourPlayerScenario(context, dealer, enemy, applier2, applier3, applier4);
        all &= await BlockReconcileScenario(context, dealer, enemy);
        all &= await BlockedMeterScenario();
        all &= await EmptyTitleScenario();
        all &= await MinimizeScenario();
        all &= await DrawnGlyphScenario();
        all &= await OverkillScenario(context, dealer, enemy);
        all &= await OverlayWidthScenario(dealer, enemy);
        all &= await CoordinateScenario(context, dealer, enemy, applier2);
        all &= await FlexPotionScenario(context, dealer, enemy, applier2);
        all &= await MixedStrengthScenario(context, dealer, enemy, applier2);
        all &= await PoisonScenario(context, dealer, enemy, applier2, applier3);
        all &= await PoisonAccelerantScenario(context, dealer, enemy, applier2);
        all &= await DemiseScenario(context, dealer, enemy, applier2);
        all &= await MagicBombScenario(context, dealer, enemy, applier2);
        all &= await StrangleScenario(context, dealer, enemy, applier2);
        all &= await HauntScenario(context, dealer, enemy);
        all &= await OutbreakScenario(context, dealer, enemy, applier2);
        all &= await DoomScenario(context, dealer, enemy, applier2, applier3);
        all &= FightLabelScenario();
        all &= PersistenceRoundTrip();

        GD.Print($"[RdpsMeter] Self-test: {(all ? "ALL SCENARIOS PASSED" : "SOME SCENARIOS FAILED")}");
        return all;
    }

    /// <summary>
    /// Two appliers stack Vulnerable 2:1 onto the enemy, then the dealer lands a powered 6. The +3 bonus (6 -> 9)
    /// must split pro-rata by stacks: 2 to NetId 2, 1 to NetId 3.
    /// </summary>
    private static async Task<bool> VulnerableScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2, Creature applier3)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<VulnerablePower>(ctx, enemy, 2m, applier2, null);
        await PowerCmd.Apply<VulnerablePower>(ctx, enemy, 1m, applier3, null);

        VulnerablePower? merged = enemy.GetPower<VulnerablePower>();
        LogShares("Vulnerable", merged);

        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null, null);

        CombatLedger l = CombatLedger.Current;
        return Report("Vulnerable pro-rata",
            Expect("aDPS", l.DealtWith(you, NoCard), 9m),
            Expect("recv <-2", l.ReceivedFrom(you, "Vulnerable", 2uL), 2m),
            Expect("recv <-3", l.ReceivedFrom(you, "Vulnerable", 3uL), 1m),
            Expect("given 2->you", l.GivenTo(2uL, "Vulnerable", you), 2m),
            Expect("given 3->you", l.GivenTo(3uL, "Vulnerable", you), 1m));
    }

    /// <summary>
    /// A card like Infection deals damage to the player who holds it, not to the enemy team. Damage that lands on a
    /// player must never enter the meter, so a hit on the dealer's own creature credits no one.
    /// </summary>
    private static async Task<bool> InfectionScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await CreatureCmd.Damage(ctx, new[] { dealer }, 5m, DamageProps.card, dealer, null, null);

        CombatLedger l = CombatLedger.Current;
        return Report("Infection (player target excluded)",
            Expect("no self aDPS", l.DealtWith(you, NoCard), 0m),
            Expect("no rDPS row", l.Snapshot().Sum(r => r.ADps), 0m));
    }

    /// <summary>
    /// A teammate applies Flanking (x2) to the enemy. Flanking excludes the applier's own hits, so the dealer's
    /// powered 6 becomes 12 and the whole +6 bonus is credited to the flanker.
    /// </summary>
    private static async Task<bool> FlankingScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<FlankingPower>(ctx, enemy, 2m, applier2, null);
        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null, null);

        CombatLedger l = CombatLedger.Current;
        return Report("Flanking",
            Expect("aDPS", l.DealtWith(you, NoCard), 12m),
            Expect("recv <-2", l.ReceivedFrom(you, "Flanking", 2uL), 6m),
            Expect("given 2->you", l.GivenTo(2uL, "Flanking", you), 6m));
    }

    /// <summary>
    /// A teammate gifts the dealer +3 Strength. Strength only buffs its owner's own attacks, but the stacks were
    /// contributed by a teammate, so the +3 additive on the dealer's powered 6 (-> 9) is credited to the gifter.
    ///
    /// This is the shape Blaze takes (0.108.0's ally-targeted "give another player 5 Strength"): it applies a plain
    /// StrengthPower to the ally with itself as applier, so it needs no handling of its own.
    /// </summary>
    private static async Task<bool> StrengthScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<StrengthPower>(ctx, dealer, 3m, applier2, null);
        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null, null);

        CombatLedger l = CombatLedger.Current;
        return Report("Strength (teammate-gifted)",
            Expect("aDPS", l.DealtWith(you, NoCard), 9m),
            Expect("recv <-2", l.ReceivedFrom(you, "Strength", 2uL), 3m),
            Expect("given 2->you", l.GivenTo(2uL, "Strength", you), 3m));
    }

    /// <summary>
    /// Damage a target's block absorbs still counts as damage dealt. First blocks a swing outright, so the enemy loses
    /// no HP at all: the meter must still book the whole hit and still credit the teammate's share of it, where before
    /// the hit was dropped entirely and a fight against a blocking enemy under-reported everyone. Then repeats against
    /// partial block, where the swing is split between block and HP, to check the two halves are summed rather than
    /// one of them standing in for the hit.
    /// </summary>
    private static async Task<bool> BlockScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;
        int hpBefore = enemy.CurrentHp;

        // 50 block against a 9-damage swing (6, plus 3 from a teammate's Strength): nothing reaches HP.
        await CreatureCmd.GainBlock(enemy, 50m, DamageProps.nonCardUnpowered, null);
        await PowerCmd.Apply<StrengthPower>(ctx, dealer, 3m, applier2, null);
        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null, null);

        CombatLedger l = CombatLedger.Current;
        bool absorbed = Report("Block (fully absorbed)",
            Expect("aDPS", l.DealtWith(you, NoCard), 9m),
            Expect("recv <-2", l.ReceivedFrom(you, "Strength", 2uL), 3m),
            Expect("given 2->you", l.GivenTo(2uL, "Strength", you), 3m),
            Expect("enemy lost no HP", enemy.CurrentHp, hpBefore));

        // Leave exactly 4 block against the same 9-damage swing: 4 absorbed, 5 through to HP, and the ledger must add
        // the whole 9 again rather than only the blocked or only the unblocked half.
        await CreatureCmd.LoseBlock(ctx, enemy, enemy.Block - 4m, null);
        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null, null);

        bool split = Report("Block (partly absorbed)",
            Expect("aDPS", l.DealtWith(you, NoCard), 18m),
            Expect("recv <-2", l.ReceivedFrom(you, "Strength", 2uL), 6m),
            Expect("enemy lost the unblocked 5", enemy.CurrentHp, hpBefore - 5));

        return absorbed && split;
    }

    /// <summary>
    /// The Blocked meter counts block that stopped something, not block that was gained, and spends it oldest-first.
    ///
    /// Two 5-block gains from different sources, then a 4-damage hit: only the first source is credited, and only for
    /// the 4 that actually landed. The other 6 is overblock - block nobody swung at - and must never appear, which is
    /// the whole reason the meter waits for a hit before it moves at all.
    /// </summary>
    private static async Task<bool> BlockSpentScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await Shield(dealer, 5m, you, "Block Potion");
        await Shield(dealer, 5m, you, "Second Wind");

        CombatLedger l = CombatLedger.Current;
        bool untouched = Report("Block (nothing spent yet)",
            Expect("nothing booked for standing block", l.RBlockOf(you), 0m));

        await CreatureCmd.Damage(ctx, new[] { dealer }, 4m, DamageProps.card, enemy, null, null);

        bool spent = Report("Block spent oldest-first",
            Expect("the first gain covers it", l.BlockedWith(you, "Block Potion"), 4m),
            Expect("the later gain is untouched", l.BlockedWith(you, "Second Wind"), 0m),
            Expect("only what landed counts", l.RBlockOf(you), 4m),
            Expect("no HP lost", dealer.CurrentHp, dealer.MaxHp));

        return untouched && spent;
    }

    /// <summary>
    /// Block from a power names itself. This is the one naming path nothing else reaches: a card arrives carrying its
    /// own CardModel and a potion is announced by the potion tracker, but a relic or power grants block from a hook the
    /// game keeps no record of, so the only thing that can say what granted it is the call stack at the synchronous
    /// entry to CreatureCmd.GainBlock. Plating is applied to the dealer and its own end-of-turn hook driven directly -
    /// which puts the same frame on the stack a real turn would, since a state machine's MoveNext is the caller either
    /// way. The expected name is read off the live power and matched against what the meter recovered from the model
    /// database, so this also pins the assumption that a model's prototype carries the same title as the run's copy.
    /// </summary>
    private static async Task<bool> BlockFromPowerScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<PlatingPower>(ctx, dealer, 6m, dealer, null);
        PlatingPower? plating = dealer.GetPower<PlatingPower>();
        if (plating == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Block from a power': FAIL (Plating did not apply)");
            return false;
        }

        string expected = plating.Title.GetFormattedText();
        await plating.BeforeSideTurnEndEarly(ctx, CombatSide.Player, new[] { dealer });
        await CreatureCmd.Damage(ctx, new[] { dealer }, 6m, DamageProps.card, enemy, null, null);

        CombatLedger l = CombatLedger.Current;
        return Report("Block from a power",
            Expect("named after the power", l.BlockedWith(you, expected), 6m),
            Expect("nothing left unnamed", l.BlockedWith(you, "(none)"), 0m),
            Expect("no HP lost", dealer.CurrentHp, dealer.MaxHp));
    }

    /// <summary>
    /// An orb's passive names itself when it fires the way it usually does: off the end of the turn, which is the one
    /// route into a passive the game pushes nothing for. Glass is the orb to check, since its passive is damage - it
    /// hits every enemy with the player as dealer and no card, so with nothing on the executing-model stack the whole
    /// hit lands under "(none)".
    ///
    /// A mutable Glass is given the dealer's player as owner and its own end-of-turn hook driven directly, which is
    /// the same call chain CombatManager walks: BeforeTurnEndOrbTrigger, then TriggerPassive on itself. The expected
    /// name is read off the orb, so this pins whatever the game calls it rather than a guess.
    /// </summary>
    private static async Task<bool> OrbPassiveScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        var glass = (GlassOrb)ModelDb.Orb<GlassOrb>().MutableClone();
        glass.Owner = dealer.Player!;
        string expected = glass.Title.GetFormattedText();
        decimal passive = glass.PassiveVal;

        GD.Print($"[RdpsMeter] Self-test: glass orb title = \"{expected}\", passive = {passive}");
        await glass.BeforeTurnEndOrbTrigger(ctx);

        CombatLedger l = CombatLedger.Current;
        return Report("Orb passive at end of turn",
            Expect("the orb's passive is worth something", passive, 4m),
            Expect("named after the orb", l.DealtWith(you, expected), passive),
            Expect("nothing left unnamed", l.DealtWith(you, NoCard), 0m));
    }

    /// <summary>
    /// Damage a drawn card triggers belongs to the effect that dealt it, even when a potion caused the draw. Speedster
    /// hits every enemy on each card drawn, so a potion that draws (Cure All, Clarity, Swift Potion, Glowwater Potion,
    /// Bottled Potential, Snecko Oil) has its own naming window open while Speedster's hook runs inside it - two names
    /// available for one hit, and only the inner one dealt it.
    ///
    /// A relic that draws (Iron Club) never had this problem: no potion window is open, so the effect name was the only
    /// candidate. That is exactly why the potion case is the one worth a scenario - and why the assertion that matters
    /// is the negative one, that the potion took none of it.
    ///
    /// Driven through the real CardPileCmd.Draw rather than by calling Speedster's hook directly, because the push this
    /// depends on is the game's: Hook.AfterCardDrawn puts each listening model on the choice context around its own
    /// call. Imitating that push here would test the harness's idea of it instead of the game's.
    /// </summary>
    private static async Task<bool> SpeedsterPotionScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<SpeedsterPower>(ctx, dealer, 5m, dealer, null);
        SpeedsterPower? speedster = dealer.GetPower<SpeedsterPower>();
        if (speedster == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Speedster under a potion': FAIL (Speedster did not apply)");
            return false;
        }

        string effect = speedster.Title.GetFormattedText();
        string potion = ModelDb.Potion<CureAll>().Title.GetFormattedText();
        decimal hit = speedster.Amount;
        GD.Print($"[RdpsMeter] Self-test: \"{effect}\" for {hit} drawn under \"{potion}\"");

        try
        {
            // The window PotionModel.OnUseWrapper opens in real play, opened by hand around the draw the potion does.
            PotionSource.Begin(you, potion);
            try
            {
                await CardPileCmd.Draw(ctx, 1m, dealer.Player!);
            }
            finally
            {
                PotionSource.End(you);
            }
        }
        finally
        {
            // Removed even if the draw threw: Speedster left standing would deal a stray hit on any later card draw.
            await PowerCmd.Remove<SpeedsterPower>(dealer);
        }

        CombatLedger l = CombatLedger.Current;
        return Report("Speedster triggered by a potion's card draw",
            Expect("Speedster hits for what it says", hit, 5m),
            Expect("named after Speedster", l.DealtWith(you, effect), hit),
            Expect("not after the potion that drew", l.DealtWith(you, potion), 0m),
            Expect("nothing left unnamed", l.DealtWith(you, NoCard), 0m));
    }

    /// <summary>
    /// Sleight of Flesh names itself. It hits an enemy every time you land a debuff on them, out of
    /// AfterPowerAmountChanged - a hook whose dispatcher pushes nothing onto the game's model stack, so the hit arrived
    /// with a player dealer, no card, and nothing to name it, and the whole thing read "(none)".
    ///
    /// The debuff is applied by the dealer themselves, because that is the power's own condition (applier == owner). A
    /// real card would be on the model stack at that moment in play, which is exactly why this cannot be recovered from
    /// LastInvolvedModel: a CardModel is not one of the three kinds EffectSource reads, so the stack's top is useless
    /// here even when it is occupied.
    ///
    /// Removed afterwards without fail - left standing, it would add a stray hit to every later scenario that applies a
    /// debuff, which is most of them.
    /// </summary>
    private static async Task<bool> SleightOfFleshScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<SleightOfFleshPower>(ctx, dealer, 9m, dealer, null);
        SleightOfFleshPower? sleight = dealer.GetPower<SleightOfFleshPower>();
        if (sleight == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Sleight of Flesh': FAIL (it did not apply)");
            return false;
        }

        string expected = sleight.Title.GetFormattedText();
        decimal hit = sleight.Amount;
        GD.Print($"[RdpsMeter] Self-test: \"{expected}\" for {hit} on landing a debuff");

        try
        {
            // A real debuff, applied by the dealer to the enemy: the power checks all three of those.
            await PowerCmd.Apply<VulnerablePower>(ctx, enemy, 2m, dealer, null);
        }
        finally
        {
            await PowerCmd.Remove<SleightOfFleshPower>(dealer);
        }

        CombatLedger l = CombatLedger.Current;
        return Report("Sleight of Flesh (damage on landing a debuff)",
            Expect("it hits for what it says", hit, 9m),
            Expect("named after the power", l.DealtWith(you, expected), hit),
            Expect("nothing left unnamed", l.DealtWith(you, NoCard), 0m));
    }

    /// <summary>
    /// A relic in the same position names itself too, which the powers above do not prove: a relic's Owner is the Player
    /// where a power's is that player's Creature, so it needs a patch of its own and could be broken while every power
    /// works. Parrying Shield is the one of the three that the harness can actually trigger - Screaming Flagon wants an
    /// empty hand and Stone Calendar a particular turn number, neither of which a scenario can arrange cheaply.
    ///
    /// Block is granted first because that is the relic's condition (it fires only if you end the turn still holding
    /// enough), and the clone is never added to the player's relics, so it takes no further hooks.
    /// </summary>
    private static async Task<bool> UnpushedRelicScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        var shield = (ParryingShield)ModelDb.Relic<ParryingShield>().MutableClone();
        shield.Owner = dealer.Player!;
        string expected = shield.Title.GetFormattedText();
        decimal hit = shield.DynamicVars.Damage.BaseValue;
        decimal needed = shield.DynamicVars.Block.BaseValue;

        await CreatureCmd.GainBlock(dealer, needed, DamageProps.nonCardUnpowered, null);
        GD.Print($"[RdpsMeter] Self-test: \"{expected}\" for {hit} behind {needed} block (dealer has {dealer.Block})");
        await shield.AfterSideTurnEnd(ctx, CombatSide.Player, new[] { dealer });

        CombatLedger l = CombatLedger.Current;
        return Report("Parrying Shield (a relic on an unpushed hook)",
            Expect("the relic hits for what it says", hit, 6m),
            Expect("named after the relic", l.DealtWith(you, expected), hit),
            Expect("nothing left unnamed", l.DealtWith(you, NoCard), 0m));
    }

    /// <summary>
    /// Thunder names itself, not the orb that set it off. Worth its own scenario because the orb is the plausible wrong
    /// answer and the code invites it: OrbCmd.Evoke does push the evoked orb onto the model stack, so it reads as though
    /// an evoke-triggered hook runs inside that push. It does not - the pop is the line before the hook is dispatched -
    /// which is why Thunder was anonymous rather than misnamed, and why it belongs in the plain list of unpushed hooks.
    ///
    /// A Lightning orb is what the power checks for, so it gets one, owned by the dealer; the clone is never queued, so
    /// it is only ever the argument to this hook and evokes nothing of its own.
    /// </summary>
    private static async Task<bool> ThunderScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<ThunderPower>(ctx, dealer, 7m, dealer, null);
        ThunderPower? thunder = dealer.GetPower<ThunderPower>();
        if (thunder == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Thunder': FAIL (it did not apply)");
            return false;
        }

        var lightning = (LightningOrb)ModelDb.Orb<LightningOrb>().MutableClone();
        lightning.Owner = dealer.Player!;

        string expected = thunder.Title.GetFormattedText();
        string orbName = lightning.Title.GetFormattedText();
        decimal hit = thunder.Amount;
        GD.Print($"[RdpsMeter] Self-test: \"{expected}\" for {hit} off an evoked \"{orbName}\"");

        try
        {
            await thunder.AfterOrbEvoked(ctx, lightning, new[] { enemy });
        }
        finally
        {
            await PowerCmd.Remove<ThunderPower>(dealer);
        }

        CombatLedger l = CombatLedger.Current;
        return Report("Thunder (damage when a Lightning orb is evoked)",
            Expect("it hits for what it says", hit, 7m),
            Expect("named after the power", l.DealtWith(you, expected), hit),
            Expect("not after the orb that set it off", l.DealtWith(you, orbName), 0m),
            Expect("nothing left unnamed", l.DealtWith(you, NoCard), 0m));
    }

    /// <summary>
    /// Beacon of Hope's block belongs to the player who gave it away, not the one wearing it. It sits on one player and
    /// hands half of every block they gain to each teammate, arriving at the block funnel with no card - so the name came
    /// off the call stack, which yields a prototype and therefore no owner, and the credit fell to the wearer. Every other
    /// card-less source grants to its own owner or is a potion whose thrower is known, which is why that fallback held up
    /// until this card.
    ///
    /// Two halves, because the fix is in two places. The first drives a foreign grant the way
    /// <see cref="Patches.ForeignBlockPatches"/> does and follows it all the way through to a spend, which is where block
    /// is actually booked: the 8 must land on the teammate who gave it and none of it on the wearer. Only a real spend
    /// proves it, since block gained is never booked on its own.
    ///
    /// The second calls the real hook on a real Beacon, which is what verifies the patch rather than the mechanism: it
    /// grants to nobody here (the harness combat has one player, and its fake teammates were never added to it), but the
    /// prefix and the Task-wrapping pop still run, and leaving that stack unbalanced would put a giver's name on the next
    /// player's block. Balance is the assertion a solo harness can make about it honestly.
    /// </summary>
    private static async Task<bool> BeaconOfHopeScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<BeaconOfHopePower>(ctx, dealer, 1m, dealer, null);
        BeaconOfHopePower? beacon = dealer.GetPower<BeaconOfHopePower>();
        if (beacon == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Beacon of Hope': FAIL (it did not apply)");
            return false;
        }

        string expected = beacon.Title.GetFormattedText();

        // A teammate's Beacon putting 8 block on you, as the patch records it: their name, their NetId, your block.
        ForeignBlockGrant.Push(expected, 2uL);
        try
        {
            await CreatureCmd.GainBlock(dealer, 8m, DamageProps.nonCardUnpowered, null);
        }
        finally
        {
            ForeignBlockGrant.Pop();
        }

        decimal worn = dealer.Block;
        await CreatureCmd.Damage(ctx, new[] { dealer }, worn, DamageProps.card, enemy, null, null);

        // Now the real hook, patch and all. Balanced afterwards is the point; it has no teammate to give to.
        bool clearBefore = ForeignBlockGrant.Current == null;
        await beacon.AfterBlockGained(dealer, 8m, DamageProps.nonCardUnpowered, null);
        bool clearAfter = ForeignBlockGrant.Current == null;
        await PowerCmd.Remove<BeaconOfHopePower>(dealer);

        // Which player the block belongs to is RBlockOf - the Blocked meter's own bar. BlockedWith is the *wearer's*
        // breakdown of what stopped the hit, so it naming Beacon is correct in either world and says nothing about
        // ownership; getting those two the wrong way round is what made the first version of this scenario fail.
        CombatLedger l = CombatLedger.Current;
        return Report("Beacon of Hope (block given to a teammate)",
            Expect("the teammate's grant is what got worn", worn, 8m),
            Expect("the giver's meter is what moves", l.RBlockOf(2uL), 8m),
            Expect("and the wearer's does not", l.RBlockOf(you), 0m),
            Expect("recorded as given 2->you", l.BlockGivenTo(2uL, expected, you), 8m),
            Expect("the wearer's breakdown still names it", l.BlockedWith(you, expected), 8m),
            Expect("nothing left unnamed", l.BlockedWith(you, NoCard), 0m),
            Expect("the stack starts clean", clearBefore ? 1m : 0m, 1m),
            Expect("and the real hook leaves it clean", clearAfter ? 1m : 0m, 1m));
    }

    /// <summary>
    /// Block from a relic names itself. It reaches the meter down the same call-stack path a power's block does, but
    /// out the other arm of the model lookup - a RelicModel's title is a different property from a PowerModel's - so a
    /// green power scenario says nothing about a relic. Anchor is cloned mutable and handed the dealer's player as its
    /// owner, then its own before-combat hook is driven directly; the clone is never added to their relics, so it
    /// receives no further hooks and cannot shield a later scenario.
    /// </summary>
    private static async Task<bool> BlockFromRelicScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        var anchor = (Anchor)ModelDb.Relic<Anchor>().MutableClone();
        anchor.Owner = dealer.Player!;
        string expected = anchor.Title.GetFormattedText();

        await anchor.BeforeCombatStart();
        decimal granted = dealer.Block;
        await CreatureCmd.Damage(ctx, new[] { dealer }, granted, DamageProps.card, enemy, null, null);

        CombatLedger l = CombatLedger.Current;
        return Report("Block from a relic",
            Expect("the relic granted its block", granted, 10m),
            Expect("named after the relic", l.BlockedWith(you, expected), granted),
            Expect("nothing left unnamed", l.BlockedWith(you, NoCard), 0m),
            Expect("no HP lost", dealer.CurrentHp, dealer.MaxHp));
    }

    /// <summary>
    /// A card names its own block, and in co-op it says whose block it is. Block arrives at Hook.ModifyBlock carrying
    /// the CardModel that granted it, which is both the row's name and - through the card's owner - the player to
    /// credit, so a teammate's Defend on you is theirs on the meter even though you are the one wearing it.
    ///
    /// The teammate half also pins the priority rule against a card rather than a Dexterity share: your own block is
    /// spent first, and only the 3 it could not cover reaches what they played.
    /// </summary>
    private static async Task<bool> BlockFromCardScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        CardModel mine = Owned(dealer.Player!);
        string myCard = mine.TitleLocString.GetFormattedText();
        await CreatureCmd.GainBlock(dealer, 5m, BlockProps.card, Play(mine, dealer.Player!, dealer));
        await CreatureCmd.Damage(ctx, new[] { dealer }, 5m, DamageProps.card, enemy, null, null);

        CombatLedger l = CombatLedger.Current;
        bool solo = Report("Block from your own card",
            Expect("named after the card", l.BlockedWith(you, myCard), 5m),
            Expect("nothing left unnamed", l.BlockedWith(you, NoCard), 0m),
            Expect("all of it yours", l.RBlockOf(you), 5m));

        // Their card on you: 4 of your own underneath it, and a 7-damage hit that has to go through both.
        await Prep(dealer, enemy);
        CardModel theirs = Owned(applier2.Player!);
        string theirCard = theirs.TitleLocString.GetFormattedText();
        await Shield(dealer, 4m, you, "Block Potion");
        await CreatureCmd.GainBlock(dealer, 6m, BlockProps.card, Play(theirs, applier2.Player!, dealer));
        await CreatureCmd.Damage(ctx, new[] { dealer }, 7m, DamageProps.card, enemy, null, null);

        bool party = Report("Block from a teammate's card",
            Expect("your own 4 goes first", l.BlockedWith(you, "Block Potion"), 4m),
            Expect("then 3 of theirs", l.BlockedWith(you, theirCard), 3m),
            Expect("given 2->you", l.BlockGivenTo(2uL, theirCard, you), 3m),
            Expect("you stopped 4", l.RBlockOf(you), 4m),
            Expect("they stopped 3", l.RBlockOf(2uL), 3m),
            Expect("no HP lost", dealer.CurrentHp, dealer.MaxHp));

        return solo && party;
    }

    /// <summary>
    /// Your own Dexterity is itemized too, not folded into the card that spent it - which is the whole point of the
    /// breakdown in a solo run, where every row is yours and "Defend 5, Speed Potion 3" is the only reading that says
    /// anything. Nothing here crosses players, so it is the one block path a single-player game exercises in full.
    ///
    /// Then the other side of it: Dexterity can be negative, and a modifier that cost block rather than gave it has no
    /// positive contribution to hand out. Crediting it with negative block would read as nonsense, so the whole
    /// (already reduced) gain stays on the card that granted it.
    /// </summary>
    private static async Task<bool> BlockOwnDexterityScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;
        CardModel card = Owned(dealer.Player!);
        string cardName = card.TitleLocString.GetFormattedText();

        PotionSource.Begin(you, "Speed Potion");
        await PowerCmd.Apply<DexterityPower>(ctx, dealer, 3m, dealer, null);
        PotionSource.End(you);

        await CreatureCmd.GainBlock(dealer, 5m, BlockProps.card, Play(card, dealer.Player!, dealer));
        await CreatureCmd.Damage(ctx, new[] { dealer }, 8m, DamageProps.card, enemy, null, null);

        CombatLedger l = CombatLedger.Current;
        bool split = Report("Block from your own Dexterity",
            Expect("the card's own 5", l.BlockedWith(you, cardName), 5m),
            Expect("your Dexterity, under its potion", l.BlockedWith(you, "Speed Potion"), 3m),
            Expect("never as a bare Dexterity row", l.BlockedWith(you, "Dexterity"), 0m),
            Expect("all 8 yours", l.RBlockOf(you), 8m),
            Expect("no HP lost", dealer.CurrentHp, dealer.MaxHp));

        // A teammate's -2 Dexterity against the same 5-block card: 3 lands, and all 3 belongs to the card.
        await Prep(dealer, enemy);
        PotionSource.Begin(2uL, "Sour Potion");
        await PowerCmd.Apply<DexterityPower>(ctx, dealer, -2m, applier2, null);
        PotionSource.End(2uL);

        await CreatureCmd.GainBlock(dealer, 5m, BlockProps.card, Play(card, dealer.Player!, dealer));
        await CreatureCmd.Damage(ctx, new[] { dealer }, 3m, DamageProps.card, enemy, null, null);

        bool negative = Report("Block under negative Dexterity",
            Expect("the reduced block stays on the card", l.BlockedWith(you, cardName), 3m),
            Expect("nothing is credited to the debuff", l.BlockedWith(you, "Sour Potion"), 0m),
            Expect("and none of it is theirs", l.RBlockOf(2uL), 0m));

        return split && negative;
    }

    /// <summary>
    /// Block a teammate paid for is credited to them, and named after what granted it rather than after the pooled
    /// power it stacked into: a teammate's Dexterity Potion adds 3 to the dealer's 5-block card, and that 3 is theirs
    /// under the potion's own name, not under "Dexterity".
    ///
    /// Then the co-op priority rule: the wearer's own block goes first, so a hit small enough to be covered by it never
    /// reaches - and never spends - what a teammate put on top.
    /// </summary>
    private static async Task<bool> BlockDexterityScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        // The teammate is mid-potion as they apply the Dexterity, which is how the stacks come to be named after it.
        PotionSource.Begin(2uL, "Dexterity Potion");
        await PowerCmd.Apply<DexterityPower>(ctx, dealer, 3m, applier2, null);
        PotionSource.End(2uL);

        await Shield(dealer, 5m, you, "Block Potion");
        await CreatureCmd.Damage(ctx, new[] { dealer }, 8m, DamageProps.card, enemy, null, null);

        CombatLedger l = CombatLedger.Current;
        bool split = Report("Block from a teammate's Dexterity",
            Expect("own block", l.BlockedWith(you, "Block Potion"), 5m),
            Expect("the teammate's Dexterity, by its potion", l.BlockedWith(you, "Dexterity Potion"), 3m),
            Expect("given 2->you", l.BlockGivenTo(2uL, "Dexterity Potion", you), 3m),
            Expect("you stopped your own 5", l.RBlockOf(you), 5m),
            Expect("they stopped 3", l.RBlockOf(2uL), 3m),
            Expect("no HP lost", dealer.CurrentHp, dealer.MaxHp));

        // Same 8 block standing, but only 4 damage: your own 5 covers it outright.
        await Prep(dealer, enemy);
        PotionSource.Begin(2uL, "Dexterity Potion");
        await PowerCmd.Apply<DexterityPower>(ctx, dealer, 3m, applier2, null);
        PotionSource.End(2uL);

        await Shield(dealer, 5m, you, "Block Potion");
        await CreatureCmd.Damage(ctx, new[] { dealer }, 4m, DamageProps.card, enemy, null, null);

        bool priority = Report("Block (the wearer's own goes first)",
            Expect("own block covers it", l.BlockedWith(you, "Block Potion"), 4m),
            Expect("the teammate's is untouched", l.BlockedWith(you, "Dexterity Potion"), 0m));

        return split && priority;
    }

    /// <summary>
    /// Two teammates stack Dexterity 2:1 onto the dealer, who brings no block of their own. Nothing of the wearer's is
    /// there to go first, so the whole 3 the hit spends is split between them in proportion to what each put in.
    /// </summary>
    private static async Task<bool> BlockProRataScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2, Creature applier3)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<DexterityPower>(ctx, dealer, 2m, applier2, null);
        await PowerCmd.Apply<DexterityPower>(ctx, dealer, 1m, applier3, null);
        LogShares("Dexterity", dealer.GetPower<DexterityPower>());

        // A zero-block gain: everything standing is the teammates' Dexterity and none of it is the dealer's.
        await CreatureCmd.GainBlock(dealer, 0m, BlockProps.card, null);
        await CreatureCmd.Damage(ctx, new[] { dealer }, 3m, DamageProps.card, enemy, null, null);

        CombatLedger l = CombatLedger.Current;
        return Report("Block pro-rata between teammates",
            Expect("all of it is Dexterity", l.BlockedWith(you, "Dexterity"), 3m),
            Expect("given 2->you", l.BlockGivenTo(2uL, "Dexterity", you), 2m),
            Expect("given 3->you", l.BlockGivenTo(3uL, "Dexterity", you), 1m),
            Expect("you stopped none of it", l.RBlockOf(you), 0m),
            Expect("they stopped 2 and 1", l.RBlockOf(2uL) + l.RBlockOf(3uL), 3m));
    }

    /// <summary>
    /// A full four-player party against one gain, which is where the two rules have to hold at once and where a
    /// two-way split can look right for reasons a three-way one does not. The dealer's own 3-block potion is topped up
    /// by Dexterity from all three teammates, 3:2:1, for 9 block standing.
    ///
    /// A 6-damage hit takes the dealer's own 3 first and leaves 3 for the teammates - half of the 6 they put in - so
    /// each gives up half of their own stake rather than the split running down some order. The second hit then finds
    /// only their block left and spends it out, so across the pair each teammate has stopped exactly what they added.
    /// </summary>
    private static async Task<bool> BlockFourPlayerScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2, Creature applier3, Creature applier4)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<DexterityPower>(ctx, dealer, 3m, applier2, null);
        await PowerCmd.Apply<DexterityPower>(ctx, dealer, 2m, applier3, null);
        await PowerCmd.Apply<DexterityPower>(ctx, dealer, 1m, applier4, null);
        LogShares("Dexterity", dealer.GetPower<DexterityPower>());

        await Shield(dealer, 3m, you, "Block Potion");
        decimal standing = dealer.Block;
        await CreatureCmd.Damage(ctx, new[] { dealer }, 6m, DamageProps.card, enemy, null, null);

        CombatLedger l = CombatLedger.Current;
        bool partial = Report("Block across four players",
            Expect("three teammates topped up 3 to 9", standing, 9m),
            Expect("your own 3 goes first", l.BlockedWith(you, "Block Potion"), 3m),
            Expect("2 gives up half of 3", l.BlockGivenTo(2uL, "Dexterity", you), 1.5m),
            Expect("3 gives up half of 2", l.BlockGivenTo(3uL, "Dexterity", you), 1m),
            Expect("4 gives up half of 1", l.BlockGivenTo(4uL, "Dexterity", you), 0.5m),
            Expect("you stopped your own 3", l.RBlockOf(you), 3m),
            Expect("they stopped 3 between them", l.RBlockOf(2uL) + l.RBlockOf(3uL) + l.RBlockOf(4uL), 3m));

        await CreatureCmd.Damage(ctx, new[] { dealer }, 3m, DamageProps.card, enemy, null, null);

        bool spentOut = Report("Block across four players (spent out)",
            Expect("2 stopped all 3 they added", l.BlockGivenTo(2uL, "Dexterity", you), 3m),
            Expect("3 stopped all 2 they added", l.BlockGivenTo(3uL, "Dexterity", you), 2m),
            Expect("4 stopped all 1 they added", l.BlockGivenTo(4uL, "Dexterity", you), 1m),
            Expect("your own is not spent twice", l.RBlockOf(you), 3m),
            Expect("no HP lost", dealer.CurrentHp, dealer.MaxHp));

        return partial && spentOut;
    }

    /// <summary>
    /// The pool is squared against the creature's real Block before every read, which is what spares the meter a patch
    /// for every path that moves block behind its back. Both directions are real and neither goes through the block
    /// funnel: block the game simply takes away - the turn's own expiry, an enemy stripping it - and block that lands
    /// without ever being granted.
    ///
    /// Losing it must come off the oldest gains, in the order they would have been spent: 10 block from two sources,
    /// 6 of it taken away, and the 4 that is left is all the newer gain's. Gaining it unseen must not vanish from the
    /// total either, so it is filed as the wearer's own under no name rather than dropped.
    /// </summary>
    private static async Task<bool> BlockReconcileScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await Shield(dealer, 5m, you, "Block Potion");
        await Shield(dealer, 5m, you, "Second Wind");
        await CreatureCmd.LoseBlock(ctx, dealer, 6m, null);
        await CreatureCmd.Damage(ctx, new[] { dealer }, 4m, DamageProps.card, enemy, null, null);

        CombatLedger l = CombatLedger.Current;
        bool trimmed = Report("Block taken away unseen",
            Expect("the oldest gain went with it", l.BlockedWith(you, "Block Potion"), 0m),
            Expect("what survived is the newer gain", l.BlockedWith(you, "Second Wind"), 4m),
            Expect("only the surviving 4 counts", l.RBlockOf(you), 4m),
            Expect("no HP lost", dealer.CurrentHp, dealer.MaxHp));

        // Block that never passed through the funnel: nothing named it, and nothing may swallow it either.
        await Prep(dealer, enemy);
        dealer.GainBlockInternal(5m);
        await CreatureCmd.Damage(ctx, new[] { dealer }, 5m, DamageProps.card, enemy, null, null);

        bool padded = Report("Block gained unseen",
            Expect("it still counts", l.RBlockOf(you), 5m),
            Expect("filed under no name", l.BlockedWith(you, NoCard), 5m),
            Expect("no HP lost", dealer.CurrentHp, dealer.MaxHp));

        return trimmed && padded;
    }

    /// <summary>
    /// A solo window with nothing to report still reports. Its headline carries the meter's own total, and carries it
    /// at zero as well - "Damage: 0", not a bare "Damage" - so a fight nobody has swung in yet, or a shop between
    /// fights, reads as an answer rather than as a window that has not finished loading.
    ///
    /// Checked on both meters the arrows reach, and against a player who is there but has done nothing: an empty run
    /// and an idle player mean the same thing, so they must say the same thing.
    /// </summary>
    private static async Task<bool> EmptyTitleScenario()
    {
        RdpsOverlayNode? overlay = RdpsOverlayNode.HarnessInstance;
        if (overlay == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Empty solo title': FAIL (no overlay in the scene tree)");
            return false;
        }

        var idle = new RdpsRow
        {
            NetId = 1uL,
            Name = "Tester",
            ADps = 0m,
            Given = 0m,
            Received = 0m,
            Dealt = Array.Empty<(string, decimal, decimal)>(),
            GivenBy = Array.Empty<(string, ulong, decimal)>(),
            ReceivedBy = Array.Empty<(string, ulong, decimal)>(),
        };

        MeterMode entered = OverlayLayout.LoadMode();
        var titles = new Dictionary<MeterMode, (string Empty, string Idle, string Name)>();
        foreach (MeterMode mode in new[] { MeterMode.Rdps, MeterMode.Blocked })
        {
            for (int guard = 0; overlay.HarnessMode != mode && guard < 4; guard++)
            {
                overlay.HarnessStepMode(1, solo: true);
            }

            titles[mode] = (overlay.HarnessHeaderTitle(null), overlay.HarnessHeaderTitle(idle), overlay.HarnessModeName(solo: true));
        }

        OverlayLayout.SaveMode(entered);
        for (int guard = 0; overlay.HarnessMode != entered && guard < 4; guard++)
        {
            overlay.HarnessStepMode(1);
        }

        OverlayLayout.SaveMode(entered);
        await Settle();

        (string emptyDamage, string idleDamage, string damageName) = titles[MeterMode.Rdps];
        (string emptyBlock, string idleBlock, string blockName) = titles[MeterMode.Blocked];

        // Printed rather than asserted: the words themselves come from whichever language the game is set to, so the
        // checks below are about the shape of the headline, and the log is where you read what it actually says.
        GD.Print($"[RdpsMeter] Self-test: empty solo headlines = \"{emptyDamage}\", \"{emptyBlock}\"");

        return Report("Empty solo title",
            Expect("damage says zero, not just its name", emptyDamage != damageName ? 1m : 0m, 1m),
            Expect("and ends in a zero", emptyDamage.EndsWith('0') ? 1m : 0m, 1m),
            Expect("an idle player reads the same", emptyDamage == idleDamage ? 1m : 0m, 1m),
            Expect("blocked says zero, not just its name", emptyBlock != blockName ? 1m : 0m, 1m),
            Expect("and ends in a zero", emptyBlock.EndsWith('0') ? 1m : 0m, 1m),
            Expect("an idle player reads the same", emptyBlock == idleBlock ? 1m : 0m, 1m),
            Expect("the two meters are told apart", emptyDamage != emptyBlock ? 1m : 0m, 1m));
    }

    /// <summary>
    /// Collapsing the window to its square and opening it again. Collapsed, everything goes - the body, the meter
    /// arrows, the fight picker and the title - leaving a square big enough to find, which is checked as a square
    /// rather than merely as something smaller: it has to be as wide as it is tall, and half again the header it
    /// replaced. The mark flips to the inverse of what the button just did and is drawn on the button's own centre,
    /// which is the part a font got wrong. The choice is written to the config so it survives a restart, and opening
    /// restores the window's full width and height exactly.
    ///
    /// Every check is made against the real header: the scenario presses the button rather than describing what it
    /// would do, and reads back the controls' own visibility and geometry.
    /// </summary>
    private static async Task<bool> MinimizeScenario()
    {
        RdpsOverlayNode? overlay = RdpsOverlayNode.HarnessInstance;
        if (overlay == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Minimize': FAIL (no overlay in the scene tree)");
            return false;
        }

        // Start open, whatever this machine's config was last left on.
        if (overlay.HarnessMinimized)
        {
            overlay.HarnessToggleMinimized();
        }

        // Somewhere with room on every side, so the checks below read the geometry rather than the screen edge the
        // real window might happen to be parked against. Put back, config and all, before returning.
        Vector2 origin = overlay.HarnessPanelPosition;
        Vector2? storedPosition = OverlayLayout.LoadPosition();
        overlay.HarnessPlace(new Vector2(240f, 200f));

        await Settle();
        (bool body, bool arrows, bool picker, bool title) open = overlay.HarnessVisibleParts;
        bool openIsPlus = overlay.HarnessGlyphIsPlus;
        decimal openOffCentre = (decimal)overlay.HarnessGlyphOffCentre.Length();
        decimal openWidth = (decimal)overlay.HarnessPanelWidth;
        decimal openHeight = (decimal)overlay.HarnessPanelHeight;
        decimal headerHeight = (decimal)overlay.HarnessHeaderHeight;
        Vector2 markOpen = overlay.HarnessMarkCentre;
        Vector2 positionOpen = overlay.HarnessPanelPosition;
        Vector2 markSizeOpen = overlay.HarnessMarkSize;

        overlay.HarnessToggleMinimized();
        await Settle();
        (bool body, bool arrows, bool picker, bool title) shut = overlay.HarnessVisibleParts;
        bool shutIsPlus = overlay.HarnessGlyphIsPlus;
        decimal shutOffCentre = (decimal)overlay.HarnessGlyphOffCentre.Length();
        decimal shutWidth = (decimal)overlay.HarnessPanelWidth;
        decimal shutHeight = (decimal)overlay.HarnessPanelHeight;
        bool remembered = OverlayLayout.LoadMinimized();
        Vector2 markShut = overlay.HarnessMarkCentre;
        Vector2 positionShut = overlay.HarnessPanelPosition;
        Vector2 markSizeShut = overlay.HarnessMarkSize;

        overlay.HarnessToggleMinimized();
        await Settle();
        (bool body, bool arrows, bool picker, bool title) reopened = overlay.HarnessVisibleParts;
        decimal reopenedHeight = (decimal)overlay.HarnessPanelHeight;
        decimal reopenedWidth = (decimal)overlay.HarnessPanelWidth;
        bool forgotten = !OverlayLayout.LoadMinimized();
        Vector2 markReopened = overlay.HarnessMarkCentre;
        Vector2 positionReopened = overlay.HarnessPanelPosition;

        // Again with the window pushed against the right edge of the screen, which is its own case: the collapsed
        // square is clamped into the viewport, and a clamp measured against the size the window still has rather than
        // the size it is becoming pulls the square left - the further right, the harder. Nothing about the arithmetic
        // above notices, because it runs where there is room on every side.
        Vector2 viewport = overlay.HarnessViewport;
        overlay.HarnessPlace(new Vector2(viewport.X - 324f, 200f));
        await Settle();
        Vector2 markOpenEdge = overlay.HarnessMarkCentre;

        overlay.HarnessToggleMinimized();
        await Settle();
        Vector2 markShutEdge = overlay.HarnessMarkCentre;

        overlay.HarnessToggleMinimized();
        await Settle();
        Vector2 markReopenedEdge = overlay.HarnessMarkCentre;

        overlay.HarnessPlace(origin);
        if (storedPosition is Vector2 stored)
        {
            OverlayLayout.SavePosition(stored);
        }

        await Settle();

        GD.Print($"[RdpsMeter] Self-test: collapsed {openWidth}x{openHeight} -> {shutWidth}x{shutHeight} "
            + $"(header {headerHeight}px), mark off-centre by {shutOffCentre}px");
        GD.Print($"[RdpsMeter] Self-test: mark {markOpen} -> {markShut}, window {positionOpen} -> {positionShut}");
        GD.Print($"[RdpsMeter] Self-test: at the right edge, mark {markOpenEdge} -> {markShutEdge} (viewport {viewport})");

        return Report("Minimize",
            Expect("opens with a body", open.body ? 1m : 0m, 1m),
            Expect("opens with its arrows", open.arrows ? 1m : 0m, 1m),
            Expect("opens with its fight picker", open.picker ? 1m : 0m, 1m),
            Expect("opens with its title", open.title ? 1m : 0m, 1m),
            Expect("open, the button offers to collapse", openIsPlus ? 1m : 0m, 0m),
            Expect("open, the mark sits on the button's centre", openOffCentre <= 1m ? 1m : 0m, 1m),
            Expect("collapsed, the body is gone", shut.body ? 1m : 0m, 0m),
            Expect("collapsed, the arrows are gone", shut.arrows ? 1m : 0m, 0m),
            Expect("collapsed, the fight picker is gone", shut.picker ? 1m : 0m, 0m),
            Expect("collapsed, the title is gone too", shut.title ? 1m : 0m, 0m),
            Expect("collapsed, the button offers to open", shutIsPlus ? 1m : 0m, 1m),
            Expect("collapsed, the mark still sits on the button's centre", shutOffCentre <= 1m ? 1m : 0m, 1m),
            // Smaller by area, not shorter: the square is a fixed size, so against a nearly-empty open window it is
            // legitimately the taller of the two. Width is where collapsing always wins, and by a lot.
            Expect("the window got smaller", shutWidth * shutHeight < openWidth * openHeight ? 1m : 0m, 1m),
            Expect("much narrower", shutWidth < openWidth ? 1m : 0m, 1m),
            Expect("down to a square", shutWidth, shutHeight),
            Expect("as tall as the header it replaced", shutHeight, headerHeight),
            Expect("the plus is drawn the same size as the minus",
                (decimal)(markSizeShut - markSizeOpen).Length(), 0m),
            Expect("the plus opens exactly where the minus was",
                (decimal)(markShut - markOpen).Length() <= 1m ? 1m : 0m, 1m),
            Expect("which took moving the window, not leaving it be",
                (decimal)(positionShut - positionOpen).Length() > 1m ? 1m : 0m, 1m),
            Expect("collapsing is remembered", remembered ? 1m : 0m, 1m),
            Expect("reopening brings the body back", reopened.body ? 1m : 0m, 1m),
            Expect("and the arrows", reopened.arrows ? 1m : 0m, 1m),
            Expect("and the fight picker", reopened.picker ? 1m : 0m, 1m),
            Expect("and the title", reopened.title ? 1m : 0m, 1m),
            Expect("and the height", reopenedHeight, openHeight),
            Expect("and the width", reopenedWidth, openWidth),
            Expect("the minus comes back to where it was",
                (decimal)(markReopened - markOpen).Length() <= 1m ? 1m : 0m, 1m),
            Expect("and the window to where it started",
                (decimal)(positionReopened - positionOpen).Length() <= 1m ? 1m : 0m, 1m),
            Expect("reopening is remembered too", forgotten ? 1m : 0m, 1m),
            Expect("against the right edge of the screen, the plus still opens on the minus",
                (decimal)(markShutEdge - markOpenEdge).Length() <= 1m ? 1m : 0m, 1m),
            Expect("and the minus still comes back to it",
                (decimal)(markReopenedEdge - markOpenEdge).Length() <= 1m ? 1m : 0m, 1m));
    }

    /// <summary>
    /// Every mark in the header is drawn, not typed. The paging arrows and the picker's caret were the characters
    /// U+25C0, U+25B6 and U+25BE, which draw as a hex-code box on a machine whose font lacks them - reported on Linux,
    /// invisible on Windows, and nothing about the source says which machine you are on. Polygons have no such
    /// dependency, so the guard worth keeping is that none of these controls has gone back to carrying text.
    ///
    /// The directions are checked because a drawn mark cannot be eyeballed in a log the way "left arrow says U+25C0"
    /// could, and the centring because a triangle is placed by the box it is given rather than by a font's line box -
    /// the same thing that made the minimize mark sit high when it was a character.
    ///
    /// Also prints whether this machine's font actually has those codepoints: the check that would have caught the bug,
    /// kept as a diagnostic rather than an assertion since the answer no longer changes what is drawn.
    /// </summary>
    private static async Task<bool> DrawnGlyphScenario()
    {
        RdpsOverlayNode? overlay = RdpsOverlayNode.HarnessInstance;
        if (overlay == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Marks are drawn, not typed': FAIL (no overlay in the scene tree)");
            return false;
        }

        // Open, or the arrows and picker it measures are hidden and sized to nothing.
        if (overlay.HarnessMinimized)
        {
            overlay.HarnessToggleMinimized();
        }

        await Settle();
        GD.Print($"[RdpsMeter] Self-test: font coverage of the old glyphs = {overlay.HarnessFontCoverage()}");

        string text = overlay.HarnessGlyphChromeText;
        (GlyphDirection prev, GlyphDirection next, GlyphDirection caret) = overlay.HarnessGlyphDirections;
        (Vector2 prevOff, Vector2 nextOff) = overlay.HarnessArrowOffCentre;
        (Rect2 caretBox, Rect2 picker) = overlay.HarnessCaretPlacement;

        return Report("Marks are drawn, not typed",
            Expect("no text left on the marks to need a font", text.Length, 0m),
            Expect("the left arrow points left", prev == GlyphDirection.Left ? 1m : 0m, 1m),
            Expect("the right arrow points right", next == GlyphDirection.Right ? 1m : 0m, 1m),
            Expect("the caret points down", caret == GlyphDirection.Down ? 1m : 0m, 1m),
            Expect("the left arrowhead is centred on its button",
                (decimal)prevOff.Length() <= 1m ? 1m : 0m, 1m),
            Expect("the right arrowhead is centred on its button",
                (decimal)nextOff.Length() <= 1m ? 1m : 0m, 1m),
            Expect("the caret is drawn inside the picker",
                picker.Encloses(caretBox) ? 1m : 0m, 1m));
    }

    /// <summary>
    /// The Blocked tab: its bar is the block a player provided that stopped something, its breakdown is the block
    /// tally rather than the damage one, and it is one of the meters the arrows reach - in a solo run as well as a
    /// party one, where it is the second of two rather than the third of three.
    /// </summary>
    private static async Task<bool> BlockedMeterScenario()
    {
        RdpsOverlayNode? overlay = RdpsOverlayNode.HarnessInstance;
        if (overlay == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Blocked tab': FAIL (no overlay in the scene tree)");
            return false;
        }

        MeterMode entered = OverlayLayout.LoadMode();
        var row = new RdpsRow
        {
            NetId = 1uL,
            Name = "Tester",
            ADps = 100m,
            Given = 30m,
            Received = 10m,
            Dealt = new List<(string, decimal, decimal)> { ("Strike", 100m, 25m) },
            GivenBy = new List<(string, ulong, decimal)> { ("VulnerablePower", 2uL, 30m) },
            ReceivedBy = new List<(string, ulong, decimal)> { ("FlankingPower", 3uL, 10m) },
            ABlock = 40m,
            BlockGiven = 12m,
            BlockReceived = 5m,
            Blocked = new List<(string, decimal, decimal)> { ("Defend", 40m, 5m) },
            BlockGivenBy = new List<(string, ulong, decimal)> { ("Dexterity Potion", 2uL, 12m) },
            BlockReceivedBy = new List<(string, ulong, decimal)> { ("Footwork", 3uL, 5m) },
        };

        // Walk round to Blocked from wherever the config left the meter; three steps reaches it from any of the three.
        for (int guard = 0; overlay.HarnessMode != MeterMode.Blocked && guard < 4; guard++)
        {
            overlay.HarnessStepMode(1);
        }

        await Settle();
        decimal value = overlay.HarnessValue(row);
        (IReadOnlyList<string> Sections, bool SplitBars) blocked = overlay.HarnessBreakdown(row);
        string title = overlay.HarnessModeName(solo: false);
        string soloTitle = overlay.HarnessModeName(solo: true);
        MeterMode remembered = OverlayLayout.LoadMode();

        // Solo offers two meters, so one step lands on Blocked and one more comes back; a party run has three.
        overlay.HarnessStepMode(1, solo: true);
        MeterMode soloNext = overlay.HarnessMode;
        overlay.HarnessStepMode(1, solo: true);
        MeterMode soloBack = overlay.HarnessMode;
        overlay.HarnessStepMode(1);
        MeterMode partyNext = overlay.HarnessMode;

        OverlayLayout.SaveMode(entered);
        for (int guard = 0; overlay.HarnessMode != entered && guard < 4; guard++)
        {
            overlay.HarnessStepMode(1);
        }

        OverlayLayout.SaveMode(entered);
        await Settle();

        return Report("Blocked tab",
            Expect("the bar is the block you provided", value, 47m),
            Expect("it itemizes block", blocked.Sections.Contains(Loc.T("section.block")) ? 1m : 0m, 1m),
            Expect("not damage", blocked.Sections.Contains(Loc.T("section.damage")) ? 0m : 1m, 1m),
            Expect("it lists block given", blocked.Sections.Contains(Loc.T("section.block.given")) ? 1m : 0m, 1m),
            Expect("it lists block received", blocked.Sections.Contains(Loc.T("section.block.received")) ? 1m : 0m, 1m),
            Expect("its bars carry the teammate segment", blocked.SplitBars ? 1m : 0m, 1m),
            Expect("titled", title == Loc.T("mode.block") ? 1m : 0m, 1m),
            Expect("titled the same alone", soloTitle == Loc.T("mode.block") ? 1m : 0m, 1m),
            Expect("the meter is remembered", remembered == MeterMode.Blocked ? 1m : 0m, 1m),
            Expect("solo pages off it", soloNext == MeterMode.Rdps ? 1m : 0m, 1m),
            Expect("and back to it", soloBack == MeterMode.Blocked ? 1m : 0m, 1m),
            Expect("a party wraps to rDPS", partyNext == MeterMode.Rdps ? 1m : 0m, 1m));
    }

    /// <summary>
    /// Gains the dealer block under a named source. Nothing about a harness call stack says where block came from, so
    /// the name is supplied the way a potion supplies it in a real run - which is also the path a thrown Block Potion
    /// takes, and the only one that can name a gain no card explains.
    /// </summary>
    private static async Task Shield(Creature dealer, decimal amount, ulong netId, string source)
    {
        PotionSource.Begin(netId, source);
        await CreatureCmd.GainBlock(dealer, amount, BlockProps.card, null);
        PotionSource.End(netId);
    }

    /// <summary>
    /// A card of this player's, stamped as theirs. A detached fake player's deck cards start unowned, and ownership is
    /// what the block attribution reads to decide whose the block is, so it is set before the card is ever played.
    /// </summary>
    private static CardModel Owned(Player player)
    {
        CardModel card = player.Deck.Cards.First();
        if (card.Owner != player)
        {
            card.Owner = player;
        }

        return card;
    }

    /// <summary>A card being played, which is all a block gain needs to name itself and say whose it is.</summary>
    private static CardPlay Play(CardModel card, Player player, Creature target)
    {
        return new CardPlay
        {
            Card = card,
            Player = player,
            Target = target,
            ResultPile = PileType.Discard,
            Resources = default,
            IsAutoPlay = false,
            PlayIndex = 0,
            PlayCount = 1,
        };
    }

    /// <summary>
    /// Overkill - the part of a killing blow the target was not alive to take - is not damage done, so it must not be
    /// counted, while damage block absorbed is and must be.
    ///
    /// Unlike the other scenarios this hands the settled result to the ledger directly instead of landing a real hit,
    /// because a real one would have to kill: the harness fight has a single enemy, killing it ends the combat, and
    /// CreatureCmd.Damage only runs the hooks the ledger listens on while a combat is in progress - so every scenario
    /// after the kill would silently record nothing. The funnel that produces a DamageResult is already covered by
    /// every other scenario; what needs pinning here is only how ApplyHit adds the three parts up.
    /// </summary>
    private static async Task<bool> OverkillScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        // 20 swung at a target with 4 HP: 4 lands, 16 is wasted.
        CombatLedger.Current.ApplyHit(
            Swing(enemy, you, 20m),
            new DamageResult(enemy, DamageProps.card) { UnblockedDamage = 4, OverkillDamage = 16 });

        bool wasted = Report("Overkill (excess past the kill is not counted)",
            Expect("aDPS", CombatLedger.Current.DealtWith(you, NoCard), 4m));

        // The same killing blow into 5 block: the 5 counts, the 16 still does not.
        CombatLedger.Current.Reset();
        CombatLedger.Current.ApplyHit(
            Swing(enemy, you, 25m),
            new DamageResult(enemy, DamageProps.card) { UnblockedDamage = 4, OverkillDamage = 16, BlockedDamage = 5 });

        bool withBlock = Report("Overkill through block (block counts, overkill does not)",
            Expect("aDPS", CombatLedger.Current.DealtWith(you, NoCard), 9m));

        return wasted && withBlock;
    }

    /// <summary>An unbuffed swing by <paramref name="you"/> for <paramref name="total"/>, with no teammate share.</summary>
    private static HitAttribution Swing(Creature enemy, ulong you, decimal total, string card = NoCard)
    {
        return new HitAttribution
        {
            Target = enemy,
            Total = total,
            DealerNetId = you,
            DealerCard = card,
            DealerPreBlock = total,
            Externals = Array.Empty<ExternalContribution>(),
        };
    }

    /// <summary>
    /// The window holds one width whatever it is showing. The expected width is written out here rather than read back
    /// from the overlay's own constant, so that changing the constant fails this instead of moving both together.
    ///
    /// Note what this can and cannot see: the harness plays solo, so it exercises the breakdown layout only. The
    /// party-table layout the other players get is not reachable from here.
    /// </summary>
    private static async Task<bool> OverlayWidthScenario(Creature dealer, Creature enemy)
    {
        const decimal expected = 320m;

        RdpsOverlayNode? overlay = RdpsOverlayNode.HarnessInstance;
        if (overlay == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Overlay width is fixed': FAIL (no overlay in the scene tree)");
            return false;
        }

        await Prep(dealer, enemy);
        await Settle();
        decimal empty = (decimal)overlay.HarnessPanelWidth;

        CombatLedger.Current.ApplyHit(
            Swing(enemy, dealer.Player!.NetId, 999999m, "A card name far longer than the row could ever show"),
            new DamageResult(enemy, DamageProps.card) { UnblockedDamage = 999999 });
        await Settle();
        decimal stretched = (decimal)overlay.HarnessPanelWidth;

        return Report("Overlay width is fixed",
            Expect("empty width", empty, expected),
            Expect("width with oversized content", stretched, expected));
    }

    /// <summary>Waits for the overlay to redraw and its containers to re-sort, which Godot defers by a frame.</summary>
    private static async Task Settle()
    {
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            for (int i = 0; i < 3; i++)
            {
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
        }
    }

    /// <summary>
    /// A teammate plays Coordinate on the dealer - the ally-targeted card that grants Strength. Coordinate's own
    /// power modifies no damage: like every TemporaryStrengthPower it re-applies a real StrengthPower internally,
    /// passing the applier through, and that inner power is what the damage funnel sees. This pins the two-step path,
    /// which the direct StrengthScenario above does not cover: the credit must still land on the player who played
    /// the card, not on the dealer who is merely holding the buff.
    /// </summary>
    private static async Task<bool> CoordinateScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        // The card is passed as the source exactly as Coordinate's own OnPlay does, since that is what lets the
        // credit be named after the card rather than the Strength pool it stacks into.
        CardModel coordinate = ModelDb.Card<Coordinate>();
        await PowerCmd.Apply<CoordinatePower>(ctx, dealer, 3m, applier2, coordinate);
        LogShares("Strength (granted by Coordinate)", dealer.GetPower<StrengthPower>());
        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null, null);

        string name = coordinate.TitleLocString.GetFormattedText();
        CombatLedger l = CombatLedger.Current;
        return Report("Coordinate (teammate's ally-targeted Strength card)",
            Expect("aDPS", l.DealtWith(you, NoCard), 9m),
            Expect("recv <-2", l.ReceivedFrom(you, name, 2uL), 3m),
            Expect("given 2->you", l.GivenTo(2uL, name, you), 3m));
    }

    /// <summary>
    /// A teammate throws a Flex Potion onto the dealer. A thrown buff potion reaches the same two-step path as
    /// Coordinate (FlexPotionPower is a TemporaryStrengthPower), but by a different route - the potion applies it
    /// with the drinker as applier - so the damage the buff adds must be credited to whoever threw it.
    /// </summary>
    private static async Task<bool> FlexPotionScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        // A potion carries no card source, so its name reaches the ledger through the potion-being-resolved window
        // that PotionModel.OnUseWrapper opens in real play; the harness opens the same window by hand.
        string name = ModelDb.Potion<FlexPotion>().Title.GetFormattedText();
        PotionSource.Begin(2uL, name);
        try
        {
            await PowerCmd.Apply<FlexPotionPower>(ctx, dealer, 5m, applier2, null);
        }
        finally
        {
            PotionSource.End(2uL);
        }

        LogShares("Strength (granted by Flex Potion)", dealer.GetPower<StrengthPower>());
        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null, null);

        CombatLedger l = CombatLedger.Current;
        return Report("Flex Potion (thrown by a teammate)",
            Expect("aDPS", l.DealtWith(you, NoCard), 11m),
            Expect("recv <-2", l.ReceivedFrom(you, name, 2uL), 5m),
            Expect("given 2->you", l.GivenTo(2uL, name, you), 5m));
    }

    /// <summary>
    /// The dealer's own Strength and a teammate's Coordinate stack on one power instance. Only the teammate's share
    /// may move on the meter - the dealer's own stacks are their own damage - so this pins the split that a shared
    /// instance makes possible: 2 of the 5 additive belong to the teammate, 3 stay with the dealer.
    /// </summary>
    private static async Task<bool> MixedStrengthScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        CardModel coordinate = ModelDb.Card<Coordinate>();
        await PowerCmd.Apply<StrengthPower>(ctx, dealer, 3m, dealer, null);
        await PowerCmd.Apply<CoordinatePower>(ctx, dealer, 2m, applier2, coordinate);
        LogShares("Strength (own 3 + teammate 2)", dealer.GetPower<StrengthPower>());
        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null, null);

        // The two shares of one Strength instance are credited separately: the teammate's under the card that granted
        // it, the dealer's own not at all - it never leaves them.
        string name = coordinate.TitleLocString.GetFormattedText();
        CombatLedger l = CombatLedger.Current;
        return Report("Strength split between the dealer and a teammate",
            Expect("aDPS", l.DealtWith(you, NoCard), 11m),
            Expect("recv <-2", l.ReceivedFrom(you, name, 2uL), 2m),
            Expect("given 2->you", l.GivenTo(2uL, name, you), 2m),
            Expect("no self-credit", l.ReceivedFrom(you, "Strength", you), 0m));
    }

    /// <summary>
    /// Two appliers stack Poison 3:2 onto the enemy, then the enemy's poison ticks. Poison has no dealer, so the
    /// whole effective tick (5) is the appliers' own damage, split pro-rata by stacks: 3 to NetId 2, 2 to NetId 3.
    /// </summary>
    private static async Task<bool> PoisonScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2, Creature applier3)
    {
        await Prep(dealer, enemy);

        await PowerCmd.Apply<PoisonPower>(ctx, enemy, 3m, applier2, null);
        await PowerCmd.Apply<PoisonPower>(ctx, enemy, 2m, applier3, null);

        PoisonPower? poison = enemy.GetPower<PoisonPower>();
        LogShares("Poison", poison);
        if (poison != null)
        {
            await poison.AfterSideTurnStart(enemy.Side, new[] { enemy }, enemy.CombatState!);
        }

        CombatLedger l = CombatLedger.Current;
        return Report("Poison pro-rata",
            Expect("2 aDPS Poison", l.DealtWith(2uL, "Poison"), 3m),
            Expect("3 aDPS Poison", l.DealtWith(3uL, "Poison"), 2m));
    }

    /// <summary>
    /// A teammate poisons the enemy for 4 while the dealer holds Accelerant 1, so poison ticks twice: the natural
    /// tick (4) belongs to the poison applier, the Accelerant-forced extra tick (3, after the decrement) belongs to
    /// the Accelerant holder.
    /// </summary>
    private static async Task<bool> PoisonAccelerantScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<PoisonPower>(ctx, enemy, 4m, applier2, null);
        await PowerCmd.Apply<AccelerantPower>(ctx, dealer, 1m, dealer, null);

        PoisonPower? poison = enemy.GetPower<PoisonPower>();
        if (poison != null)
        {
            await poison.AfterSideTurnStart(enemy.Side, new[] { enemy }, enemy.CombatState!);
        }

        CombatLedger l = CombatLedger.Current;
        return Report("Poison + Accelerant",
            Expect("2 aDPS (base tick)", l.DealtWith(2uL, "Poison"), 4m),
            Expect("you aDPS (accel tick)", l.DealtWith(you, "Poison"), 3m));
    }

    /// <summary>
    /// A teammate throws Powdered Demise at the enemy for 9. Demise deals dealer-less unblockable damage at side-turn
    /// end, which the game cannot attribute; the removed HP must be booked as the applier's own aDPS.
    /// </summary>
    private static async Task<bool> DemiseScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);

        await PowerCmd.Apply<DemisePower>(ctx, enemy, 9m, applier2, null);

        DemisePower? demise = enemy.GetPower<DemisePower>();
        LogShares("Demise", demise);
        if (demise != null)
        {
            await demise.AfterSideTurnEnd(ctx, enemy.Side, new[] { enemy });
        }

        CombatLedger l = CombatLedger.Current;
        return Report("Demise",
            Expect("2 aDPS Demise", l.DealtWith(2uL, "Demise"), 9m));
    }

    /// <summary>
    /// A teammate puts Magic Bomb 8 on the enemy. Magic Bomb damages the enemy it sits on at that enemy's side-turn
    /// end, dealt with the enemy as dealer - so the counterfactual engine drops it; the removed HP must be booked as
    /// the applier's own aDPS, named by the power.
    /// </summary>
    private static async Task<bool> MagicBombScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);

        await PowerCmd.Apply<MagicBombPower>(ctx, enemy, 8m, applier2, null);

        MagicBombPower? bomb = enemy.GetPower<MagicBombPower>();
        LogShares("MagicBomb", bomb);
        if (bomb != null)
        {
            await bomb.AfterSideTurnEnd(ctx, enemy.Side, new[] { enemy });
        }

        CombatLedger l = CombatLedger.Current;
        string name = bomb?.Title.GetFormattedText() ?? "Magic Bomb";
        return Report("Magic Bomb",
            Expect("2 aDPS Magic Bomb", l.DealtWith(2uL, name), 8m));
    }

    /// <summary>
    /// A teammate strangles the enemy for 7, then plays one of their own cards. Strangle deals dealer-less
    /// unblockable damage to the enemy after that card resolves, which must be booked as the applier's own aDPS.
    /// Driven by invoking the power's card-played hooks with a card owned by the applier, exactly as a real play does.
    /// </summary>
    private static async Task<bool> StrangleScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);

        await PowerCmd.Apply<StranglePower>(ctx, enemy, 7m, applier2, null);

        StranglePower? strangle = enemy.GetPower<StranglePower>();
        LogShares("Strangle", strangle);
        if (strangle != null)
        {
            // A detached fake player's deck cards start unowned; Strangle only fires on a card owned by its applier,
            // so stamp ownership first.
            CardModel card = applier2.Player!.Deck.Cards.First();
            if (card.Owner != applier2.Player)
            {
                card.Owner = applier2.Player;
            }

            var cardPlay = new CardPlay
            {
                Card = card,
                Player = applier2.Player!,
                Target = enemy,
                ResultPile = PileType.Discard,
                Resources = default,
                IsAutoPlay = false,
                PlayIndex = 0,
                PlayCount = 1,
            };
            await strangle.BeforeCardPlayed(cardPlay);
            await strangle.AfterCardPlayed(ctx, cardPlay);
        }

        CombatLedger l = CombatLedger.Current;
        return Report("Strangle",
            Expect("2 aDPS Strangle", l.DealtWith(2uL, "Strangle"), 7m));
    }

    /// <summary>
    /// The dealer holds Haunt 6 and plays a Soul, which deals dealer-less unblockable damage to a random enemy. It is
    /// the dealer's own damage, so it must be booked as their aDPS regardless of which enemy the roll picks.
    /// </summary>
    private static async Task<bool> HauntScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<HauntPower>(ctx, dealer, 6m, dealer, null);

        HauntPower? haunt = dealer.GetPower<HauntPower>();
        LogShares("Haunt", haunt);
        if (haunt != null)
        {
            CardModel soul = enemy.CombatState!.CreateCard(ModelDb.Card<Soul>(), dealer.Player!);
            var cardPlay = new CardPlay
            {
                Card = soul,
                Player = dealer.Player!,
                Target = enemy,
                ResultPile = PileType.Discard,
                Resources = default,
                IsAutoPlay = false,
                PlayIndex = 0,
                PlayCount = 1,
            };
            await haunt.AfterCardPlayed(ctx, cardPlay);
        }

        CombatLedger l = CombatLedger.Current;
        return Report("Haunt",
            Expect("you aDPS Haunt", l.DealtWith(you, "Haunt"), 6m));
    }

    /// <summary>
    /// Two appliers stack Doom 20:10 onto the enemy, whose HP is set to 15. Doom is not damage - it instakills - so
    /// the removed HP (15) is credited as the appliers' own damage, split by stacks: 10 to NetId 2, 5 to NetId 3.
    /// Run last, because it kills the target.
    /// </summary>
    private static async Task<bool> DoomScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2, Creature applier3)
    {
        await Prep(dealer, enemy);

        await PowerCmd.Apply<DoomPower>(ctx, enemy, 20m, applier2, null);
        await PowerCmd.Apply<DoomPower>(ctx, enemy, 10m, applier3, null);
        await CreatureCmd.SetCurrentHp(enemy, 15m);

        DoomPower? doom = enemy.GetPower<DoomPower>();
        LogShares("Doom", doom);
        await DoomPower.DoomKill(new[] { enemy });

        CombatLedger l = CombatLedger.Current;
        return Report("Doom",
            Expect("2 aDPS Doom", l.DealtWith(2uL, "Doom"), 10m),
            Expect("3 aDPS Doom", l.DealtWith(3uL, "Doom"), 5m));
    }

    /// <summary>
    /// The dealer plays Outbreak, then poisons an enemy three times. On the third poison Outbreak deals its amount to
    /// every enemy with the player as dealer but no card source, from AfterPowerAmountChanged - a non-pushing hook - so
    /// without a source label it would read "(none)". It must be booked as the player's own aDPS, named "Outbreak".
    /// The first poison is applied by a teammate (so Outbreak ignores it) so the three owner-applied increments that
    /// drive the burst are the direct calls, keeping the count exact.
    /// </summary>
    private static async Task<bool> OutbreakScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<OutbreakPower>(ctx, dealer, 11m, dealer, null);
        await PowerCmd.Apply<PoisonPower>(ctx, enemy, 1m, applier2, null);

        OutbreakPower? outbreak = dealer.GetPower<OutbreakPower>();
        PoisonPower? poison = enemy.GetPower<PoisonPower>();
        if (outbreak != null && poison != null)
        {
            for (int i = 0; i < 3; i++)
            {
                await outbreak.AfterPowerAmountChanged(ctx, poison, 1m, dealer, null);
            }
        }

        // Outbreak bursts every hittable enemy, so its total is 11 per enemy; the point of the check is that the damage
        // is booked as the player's own and named "Outbreak" (a whole number of 11s), never left as "(none)".
        CombatLedger l = CombatLedger.Current;
        string name = outbreak?.Title.GetFormattedText() ?? "Outbreak";
        decimal dealt = l.DealtWith(you, name);
        bool ok = dealt > 0m && dealt % 11m == 0m && l.DealtWith(you, NoCard) == 0m;
        GD.Print($"[RdpsMeter] Scenario 'Outbreak': {(ok ? "PASS" : "FAIL")}");
        if (!ok)
        {
            GD.Print($"[RdpsMeter]     Outbreak dealt={dealt} (want a positive multiple of 11), unlabeled={l.DealtWith(you, NoCard)}");
        }

        return ok;
    }

    /// <summary>
    /// The fight-picker labels: a single enemy keeps its full name (pluralized when there are several), while a mix is
    /// shortened toughest-first to about the length of one name so the dropdown stays readable.
    /// </summary>
    /// <summary>
    /// The meter must open on the run total, not the live combat. Reads the caption off the live overlay once the
    /// harness is in a fight, so it covers the view the picker is built with and the one a new run resets it to - the
    /// two places a default could regress independently.
    /// </summary>
    private static bool DefaultViewScenario()
    {
        RdpsOverlayNode? overlay = RdpsOverlayNode.HarnessInstance;
        if (overlay == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Opens on the run total': FAIL (no overlay in the scene tree)");
            return false;
        }

        string expected = Loc.T("view.total");
        string actual = overlay.HarnessPickerCaption;
        bool ok = actual == expected;
        GD.Print($"[RdpsMeter] Scenario 'Opens on the run total': {(ok ? "PASS" : "FAIL")}");
        if (!ok)
        {
            GD.Print($"[RdpsMeter]     picker caption: got '{actual}', expected '{expected}'");
        }

        return ok;
    }

    private static bool FightLabelScenario()
    {
        var cases = new (string[] Enemies, string Expected)[]
        {
            (new[] { "Nibbit" }, "Nibbit"),
            (new[] { "Cubex Construct" }, "Cubex Construct"),
            (new[] { "Acid Slime", "Acid Slime", "Acid Slime" }, "Acid Slimes"),
            (new[] { "Shrinker Beetle", "Fungi Beast" }, "Beetle & Beast"),
            (new[] { "Cubex Construct", "Red Louse", "Fungi Beast" }, "Construct +2"),
            (Array.Empty<string>(), "Combat"),
        };

        bool ok = true;
        foreach ((string[] enemies, string expected) in cases)
        {
            string actual = FightLabel.From(enemies);
            if (actual != expected)
            {
                ok = false;
                GD.Print($"[RdpsMeter]     FightLabel [{string.Join(", ", enemies)}]: got '{actual}', expected '{expected}'");
            }
        }

        GD.Print($"[RdpsMeter] Scenario 'Fight labels': {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    /// <summary>
    /// The run's saved breakdown must survive a JSON round-trip byte-for-byte: serializing the live ledger, parsing it
    /// back, and re-serializing must reproduce the same combats and totals. Runs last, over whatever the scenarios left
    /// in the active combat, so it exercises real card/effect/name entries rather than a hand-built stub.
    /// </summary>
    private static bool PersistenceRoundTrip()
    {
        RunLedgerDto dto = RunLedger.ToDto();
        string json = RunLedgerStore.Serialize(dto);
        RunLedgerDto? back = RunLedgerStore.Deserialize(json);

        return Report("Persistence round-trip",
            Expect("combats preserved", back?.Combats.Count ?? -1, dto.Combats.Count),
            Expect("total dealt preserved", back != null ? TotalDealt(back) : -1m, TotalDealt(dto)),
            Expect("reserialize is stable", back != null && RunLedgerStore.Serialize(back) == json ? 1m : 0m, 1m));
    }

    /// <summary>
    /// Two runs paused in parallel - the game allows a solo run and a co-op run at once - must keep separate saved
    /// breakdowns. Plays a fight in run A, then one in run B (which is what used to overwrite A), then resumes each and
    /// checks it comes back with its own damage and its own fight name. Leaves the harness's run in place afterwards.
    /// </summary>
    private static bool TwoRunsScenario()
    {
        const string runA = "selftest-run-a";
        const string runB = "selftest-run-b";
        var share = new Dictionary<ulong, decimal> { { 1uL, 1m } };

        // The harness's own combat is handed back at the end wearing the name it started with.
        string harnessLabel = RunLedger.Active.Label;

        RunLedger.StartNewRun(runA);
        RunLedger.BeginCombat("9:0:0:-", "Alpha");
        RunLedger.Active.ApplyDot("Poison", share, 10);
        RunLedger.EndCombat();

        RunLedger.StartNewRun(runB);
        RunLedger.BeginCombat("9:1:1:-", "Beta");
        RunLedger.Active.ApplyDot("Poison", share, 20);
        RunLedger.EndCombat();

        RunLedger.ResumeRun(runA);
        decimal aDamage = RunLedger.TotalSnapshot().Sum(r => r.ADps);
        IReadOnlyList<CombatInfo> aFights = RunLedger.Fights();

        RunLedger.ResumeRun(runB);
        decimal bDamage = RunLedger.TotalSnapshot().Sum(r => r.ADps);
        IReadOnlyList<CombatInfo> bFights = RunLedger.Fights();

        RunLedgerStore.Delete(runA);
        RunLedgerStore.Delete(runB);

        // Hand the ledger back to the combat the harness is actually in.
        RunLedger.StartNewRun(RunContext.RunId);
        RunLedger.BeginCombat(RunContext.CombatKey, harnessLabel);

        return Report("Two runs stored separately",
            Expect("run A damage", aDamage, 10m),
            Expect("run A fights", aFights.Count, 1m),
            Expect("run A fight name", aFights.Count == 1 && aFights[0].Label == "Alpha" ? 1m : 0m, 1m),
            Expect("run B damage", bDamage, 20m),
            Expect("run B fights", bFights.Count, 1m),
            Expect("run B fight name", bFights.Count == 1 && bFights[0].Label == "Beta" ? 1m : 0m, 1m));
    }

    /// <summary>
    /// Nothing but an empty run may take the window away. Walks the states a player passes through around a fight -
    /// before it, in it, after it, standing in a room with a fresh empty combat picked, and back again after quitting to
    /// the menu and continuing - and checks the meter stays up for all of them once the run has recorded anybody.
    /// Leaves the harness's own run in place afterwards.
    /// </summary>
    private static bool PersistentOverlayScenario()
    {
        const string runId = "selftest-persistent-overlay";
        var share = new Dictionary<ulong, decimal> { { 1uL, 1m } };
        string harnessLabel = RunLedger.Active.Label;

        // A run with nothing recorded yet: there is genuinely nothing to draw out of combat, and the fight itself puts
        // the window up. This is the only state that may hide it, and it is what makes the checks below non-vacuous.
        RunLedger.StartNewRun(runId);
        bool emptyOutOfCombat = RdpsOverlay.ShouldShow(inCombat: false);
        bool emptyInCombat = RdpsOverlay.ShouldShow(inCombat: true);

        RunLedger.BeginCombat("9:5:5:-", "Persistent");
        RunLedger.Active.ApplyDot("Poison", share, 12);
        RunLedger.EndCombat();
        bool afterFight = RdpsOverlay.ShouldShow(inCombat: false);

        // Walking into the next room begins nothing, but entering it does: the live view is empty again while the run
        // total is not. The window must follow the run, not the picked view.
        RunLedger.BeginCombat("9:6:6:-", "Next");
        bool liveViewEmpty = RunLedger.CurrentSnapshot().Count == 0;
        bool nextRoom = RdpsOverlay.ShouldShow(inCombat: false);

        // Quit to the menu and continue: the resumed run comes back with its damage, so the window comes back with it.
        RunLedger.ResumeRun(runId);
        bool afterReload = RdpsOverlay.ShouldShow(inCombat: false);
        decimal reloaded = RunLedger.TotalSnapshot().Sum(r => r.ADps);

        RunLedgerStore.Delete(runId);
        RunLedger.StartNewRun(RunContext.RunId);
        RunLedger.BeginCombat(RunContext.CombatKey, harnessLabel);

        return Report("Overlay outlives the fight",
            Expect("hidden when the run is empty", emptyOutOfCombat ? 1m : 0m, 0m),
            Expect("shown in combat regardless", emptyInCombat ? 1m : 0m, 1m),
            Expect("shown after the fight ends", afterFight ? 1m : 0m, 1m),
            Expect("live view really is empty", liveViewEmpty ? 1m : 0m, 1m),
            Expect("shown in the next room", nextRoom ? 1m : 0m, 1m),
            Expect("shown after quit and continue", afterReload ? 1m : 0m, 1m),
            Expect("damage survived the reload", reloaded, 12m));
    }

    /// <summary>
    /// Launching the game must bring back the run that was being played, so the meter has something to show from the
    /// main menu on. Saves two runs in order, wipes the in-memory ledger the way a fresh launch starts, and checks the
    /// restore picks the one saved last - not the other one, and not nothing.
    /// </summary>
    private static bool LastPlayedScenario()
    {
        const string older = "selftest-older-run";
        const string newer = "selftest-newer-run";
        var share = new Dictionary<ulong, decimal> { { 1uL, 1m } };
        string harnessLabel = RunLedger.Active.Label;

        RunLedger.StartNewRun(older);
        RunLedger.BeginCombat("9:7:7:-", "Older");
        RunLedger.Active.ApplyDot("Poison", share, 7);
        RunLedger.EndCombat();

        RunLedger.StartNewRun(newer);
        RunLedger.BeginCombat("9:8:8:-", "Newer");
        RunLedger.Active.ApplyDot("Poison", share, 33);
        RunLedger.EndCombat();

        // A fresh launch: nothing in memory, everything on disk.
        RunLedger.LoadDto(null);
        bool blankBeforeLoad = !RdpsOverlay.ShouldShow(inCombat: false);

        RunLedger.LoadLastPlayed();
        decimal restored = RunLedger.TotalSnapshot().Sum(r => r.ADps);
        IReadOnlyList<CombatInfo> fights = RunLedger.Fights();
        bool shown = RdpsOverlay.ShouldShow(inCombat: false);

        RunLedgerStore.Delete(older);
        RunLedgerStore.Delete(newer);
        RunLedger.StartNewRun(RunContext.RunId);
        RunLedger.BeginCombat(RunContext.CombatKey, harnessLabel);

        return Report("Last played run restored at launch",
            Expect("blank before the restore", blankBeforeLoad ? 1m : 0m, 1m),
            Expect("damage restored", restored, 33m),
            Expect("its own fight", fights.Count, 1m),
            Expect("its own fight name", fights.Count == 1 && fights[0].Label == "Newer" ? 1m : 0m, 1m),
            Expect("window comes up with it", shown ? 1m : 0m, 1m));
    }

    /// <summary>
    /// The run history page drives the meter. Builds a run of three fights across two acts and a page whose map points
    /// match it, then checks that a map point resolves to the combat the ledger filed for it - skipping the shop, and
    /// starting its count over in the second act - and that the overlay actually switches to it. A page showing some
    /// other run resolves to no combat at all: the meter goes empty under the fight's own name rather than showing the
    /// loaded run's damage, and stays on screen to say so.
    /// </summary>
    private static bool RunHistoryScenario()
    {
        RdpsOverlayNode? overlay = RdpsOverlayNode.HarnessInstance;
        if (overlay == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Run history drives the meter': FAIL (no overlay in the scene tree)");
            return false;
        }

        const string runId = "selftest-run-history";
        var share = new Dictionary<ulong, decimal> { { 1uL, 1m } };
        string harnessLabel = RunLedger.Active.Label;

        RunLedger.StartNewRun(runId);
        Fought("0:1:2:0", "Alpha", 11);
        Fought("0:3:4:0", "Beta", 22);
        Fought("1:0:1:0", "Gamma", 33);

        // The page's map points, in the order they were walked: a fight, a shop that is not one, an elite, then the
        // next act starting over.
        var act0 = new List<MapPointHistoryEntry>
        {
            Point(RoomType.Monster), Point(RoomType.Shop), Point(RoomType.Elite),
        };
        var act1 = new List<MapPointHistoryEntry> { Point(RoomType.Monster) };
        var history = new RunHistory
        {
            Seed = runId,
            MapPointHistory = new List<List<MapPointHistoryEntry>> { act0, act1 },
        };

        HistoryFight? first = RunHistoryLink.Locate(history, act0[0]);
        HistoryFight? shop = RunHistoryLink.Locate(history, act0[1]);
        HistoryFight? elite = RunHistoryLink.Locate(history, act0[2]);
        HistoryFight? nextAct = RunHistoryLink.Locate(history, act1[0]);

        // The overlay follows whatever the page picked out.
        RunHistoryView.Show(elite ?? default);
        decimal shownDamage = overlay.HarnessSelectedView().Sum(r => r.ADps);
        string shownCaption = overlay.HarnessPickerCaption;

        // A different run: same shape of page, nothing of it in memory.
        var other = new RunHistory
        {
            Seed = "selftest-some-other-run",
            MapPointHistory = new List<List<MapPointHistoryEntry>> { act0, act1 },
        };
        HistoryFight? foreign = RunHistoryLink.Locate(other, act0[0]);
        RunHistoryView.Show(foreign ?? default);
        int foreignRows = overlay.HarnessSelectedView().Count;
        bool foreignStillShown = RdpsOverlay.ShouldShow(inCombat: false);

        // ... and still shown with nothing loaded at all, which is the only case an empty window is worth drawing.
        RunLedger.LoadDto(null);
        bool shownOnEmptyLedger = RdpsOverlay.ShouldShow(inCombat: false);

        // Closing the page hands the meter back to its own view.
        RunHistoryView.Release();
        overlay.HarnessSelectedView();
        string releasedCaption = overlay.HarnessPickerCaption;

        RunLedgerStore.Delete(runId);
        RunLedger.StartNewRun(RunContext.RunId);
        RunLedger.BeginCombat(RunContext.CombatKey, harnessLabel);

        return Report("Run history drives the meter",
            Expect("first fight of the act", first?.Key == "0:1:2:0" ? 1m : 0m, 1m),
            Expect("the shop is not a fight", shop == null ? 1m : 0m, 1m),
            Expect("the elite is the act's second fight", elite?.Key == "0:3:4:0" ? 1m : 0m, 1m),
            Expect("the next act counts from zero", nextAct?.Key == "1:0:1:0" ? 1m : 0m, 1m),
            Expect("overlay shows that fight", shownDamage, 22m),
            Expect("captioned with its name", shownCaption == "Beta" ? 1m : 0m, 1m),
            Expect("another run resolves to no combat", foreign is { Key: null } ? 1m : 0m, 1m),
            Expect("and shows nothing", foreignRows, 0m),
            Expect("but stays on screen", foreignStillShown ? 1m : 0m, 1m),
            Expect("even with nothing loaded", shownOnEmptyLedger ? 1m : 0m, 1m),
            Expect("released back to the total", releasedCaption == Loc.T("view.total") ? 1m : 0m, 1m));

        void Fought(string key, string label, int damage)
        {
            RunLedger.BeginCombat(key, label);
            RunLedger.Active.ApplyDot("Poison", share, damage);
            RunLedger.EndCombat();
        }
    }

    /// <summary>
    /// The arrows page between meters, and each meter draws its own thing. Against one player who dealt 100, was given
    /// 30 by teammates and gave away 10: rDPS reads 120 and itemizes both, while aDPS reads the 100 they actually dealt
    /// and drops the two teammate sections along with the faded segment on the damage bars - all three describe an
    /// adjustment that meter does not make. Also checks the arrows land back where they started, and that the meter is
    /// written down so the next session opens on it.
    /// </summary>
    private static async Task<bool> MeterModeScenario()
    {
        RdpsOverlayNode? overlay = RdpsOverlayNode.HarnessInstance;
        if (overlay == null)
        {
            GD.Print("[RdpsMeter] Scenario 'Arrows page between meters': FAIL (no overlay in the scene tree)");
            return false;
        }

        MeterMode entered = OverlayLayout.LoadMode();
        var row = new RdpsRow
        {
            NetId = 1uL,
            Name = "Tester",
            ADps = 100m,
            Given = 30m,
            Received = 10m,
            Dealt = new List<(string, decimal, decimal)> { ("Strike", 100m, 25m) },
            GivenBy = new List<(string, ulong, decimal)> { ("VulnerablePower", 2uL, 30m) },
            ReceivedBy = new List<(string, ulong, decimal)> { ("FlankingPower", 3uL, 10m) },
        };

        // Start from rDPS however the config left the meter, so the paging below is measured from a known place.
        for (int guard = 0; overlay.HarnessMode != MeterMode.Rdps && guard < 4; guard++)
        {
            overlay.HarnessStepMode(1);
        }

        await Settle();
        decimal rdpsValue = overlay.HarnessValue(row);
        (IReadOnlyList<string> Sections, bool SplitBars) rdps = overlay.HarnessBreakdown(row);
        string rdpsTitle = overlay.HarnessModeName(solo: false);

        overlay.HarnessStepMode(1);
        await Settle();
        decimal adpsValue = overlay.HarnessValue(row);
        (IReadOnlyList<string> Sections, bool SplitBars) adps = overlay.HarnessBreakdown(row);
        string adpsTitle = overlay.HarnessModeName(solo: false);
        MeterMode remembered = OverlayLayout.LoadMode();

        // Two meters, so either arrow reaches the other one and the pair comes back around.
        overlay.HarnessStepMode(-1);
        MeterMode back = overlay.HarnessMode;

        // Alone, the two damage meters are the same number and are offered as the one name they share, so the arrows
        // page from it straight to Blocked rather than through an aDPS that would read identically.
        string soloTitle = overlay.HarnessModeName(solo: true);
        overlay.HarnessStepMode(1, solo: true);
        MeterMode soloNext = overlay.HarnessMode;

        OverlayLayout.SaveMode(entered);
        for (int guard = 0; overlay.HarnessMode != entered && guard < 4; guard++)
        {
            overlay.HarnessStepMode(1);
        }

        OverlayLayout.SaveMode(entered);
        await Settle();

        return Report("Arrows page between meters",
            Expect("rDPS credits teammates", rdpsValue, 120m),
            Expect("rDPS lists given", rdps.Sections.Contains(Loc.T("section.given")) ? 1m : 0m, 1m),
            Expect("rDPS lists received", rdps.Sections.Contains(Loc.T("section.received")) ? 1m : 0m, 1m),
            Expect("rDPS bars carry the buff segment", rdps.SplitBars ? 1m : 0m, 1m),
            Expect("rDPS titled", rdpsTitle.StartsWith(Loc.T("mode.rdps")) ? 1m : 0m, 1m),
            Expect("aDPS is the damage dealt", adpsValue, 100m),
            Expect("aDPS drops given", adps.Sections.Contains(Loc.T("section.given")) ? 0m : 1m, 1m),
            Expect("aDPS drops received", adps.Sections.Contains(Loc.T("section.received")) ? 0m : 1m, 1m),
            Expect("aDPS keeps the damage itemized", adps.Sections.Contains(Loc.T("section.damage")) ? 1m : 0m, 1m),
            Expect("aDPS bars are solid", adps.SplitBars ? 0m : 1m, 1m),
            Expect("aDPS titled", adpsTitle.StartsWith(Loc.T("mode.adps")) ? 1m : 0m, 1m),
            Expect("the meter is remembered", remembered == MeterMode.ADps ? 1m : 0m, 1m),
            Expect("the other arrow comes back", back == MeterMode.Rdps ? 1m : 0m, 1m),
            Expect("solo says DPS", soloTitle == Loc.T("mode.dps") ? 1m : 0m, 1m),
            Expect("solo skips aDPS", soloNext == MeterMode.Blocked ? 1m : 0m, 1m),
            Expect("the picker draws its chip", overlay.HarnessPickerDrawsChip ? 1m : 0m, 1m));
    }

    private static MapPointHistoryEntry Point(params RoomType[] rooms)
    {
        var point = new MapPointHistoryEntry();
        foreach (RoomType room in rooms)
        {
            point.Rooms.Add(new MapPointRoomHistoryEntry { RoomType = room });
        }

        return point;
    }

    private static decimal TotalDealt(RunLedgerDto dto)
    {
        return dto.Combats.SelectMany(c => c.Players).SelectMany(p => p.Dealt).Sum(d => d.Amount);
    }

    /// <summary>
    /// Resets the ledger and returns the enemy to a clean, full-health state: strips Artifact (which would eat the
    /// first debuff) and any effect a prior scenario left behind, then heals to full so the hit lands unblocked and
    /// pre-block shares scale onto settled damage 1:1.
    /// </summary>
    private static async Task Prep(Creature dealer, Creature enemy)
    {
        CombatLedger.Current.Reset();
        Patches.AttributionPatches.ClearPending();

        for (int guard = 0; enemy.GetPower<ArtifactPower>() != null && guard < 10; guard++)
        {
            await PowerCmd.Remove<ArtifactPower>(enemy);
        }

        if (enemy.GetPower<VulnerablePower>() != null)
        {
            await PowerCmd.Remove<VulnerablePower>(enemy);
        }

        if (enemy.GetPower<FlankingPower>() != null)
        {
            await PowerCmd.Remove<FlankingPower>(enemy);
        }

        // The temporary-strength powers are cleared too, not just the StrengthPower they grant: one left on the dealer
        // would make the next scenario's application a merge onto it rather than a fresh apply, so that scenario would
        // no longer be testing the path it names.
        if (dealer.GetPower<CoordinatePower>() != null)
        {
            await PowerCmd.Remove<CoordinatePower>(dealer);
        }

        if (dealer.GetPower<FlexPotionPower>() != null)
        {
            await PowerCmd.Remove<FlexPotionPower>(dealer);
        }

        if (dealer.GetPower<StrengthPower>() != null)
        {
            await PowerCmd.Remove<StrengthPower>(dealer);
        }

        if (enemy.GetPower<PoisonPower>() != null)
        {
            await PowerCmd.Remove<PoisonPower>(enemy);
        }

        if (dealer.GetPower<AccelerantPower>() != null)
        {
            await PowerCmd.Remove<AccelerantPower>(dealer);
        }

        if (dealer.GetPower<HauntPower>() != null)
        {
            await PowerCmd.Remove<HauntPower>(dealer);
        }

        if (enemy.GetPower<DoomPower>() != null)
        {
            await PowerCmd.Remove<DoomPower>(enemy);
        }

        if (enemy.GetPower<DemisePower>() != null)
        {
            await PowerCmd.Remove<DemisePower>(enemy);
        }

        if (enemy.GetPower<StranglePower>() != null)
        {
            await PowerCmd.Remove<StranglePower>(enemy);
        }

        if (dealer.GetPower<OutbreakPower>() != null)
        {
            await PowerCmd.Remove<OutbreakPower>(dealer);
        }

        if (dealer.GetPower<DexterityPower>() != null)
        {
            await PowerCmd.Remove<DexterityPower>(dealer);
        }

        // Plating grants block off its own hooks, so one left standing would quietly shield a later scenario.
        if (dealer.GetPower<PlatingPower>() != null)
        {
            await PowerCmd.Remove<PlatingPower>(dealer);
        }

        // Block left standing would silently soak the next scenario's swing and make it assert against the wrong
        // number, so it is cleared alongside the HP reset. Only the block scenarios ever leave any - and theirs must go
        // before the pool is reset below, or the pool would find block it cannot account for and file it as unknown.
        if (enemy.Block > 0)
        {
            await CreatureCmd.LoseBlock(new NoOpChoiceContext(), enemy, enemy.Block, null);
        }

        if (dealer.Block > 0)
        {
            await CreatureCmd.LoseBlock(new NoOpChoiceContext(), dealer, dealer.Block, null);
        }

        // Cleared last: the removals above run block and power hooks, which can book more of both.
        CombatLedger.Current.Reset();
        Patches.AttributionPatches.ClearPending();

        // The block scenarios swing at the dealer, so their health is restored alongside the enemy's - a scenario that
        // fails must not go on to kill the player and end the combat every later scenario needs.
        await CreatureCmd.SetCurrentHp(dealer, dealer.MaxHp);
        await CreatureCmd.SetCurrentHp(enemy, enemy.MaxHp);
    }

    private static void LogShares(string effect, PowerModel? power)
    {
        if (power == null)
        {
            return;
        }

        IReadOnlyDictionary<ulong, decimal>? shares = PowerOwnership.Instance.Shares(power);
        string rendered = shares == null ? "none" : string.Join(", ", shares.Select(kv => $"{kv.Key}:{kv.Value:0.00}"));
        GD.Print($"[RdpsMeter] Self-test: {effect} ownership = {rendered} (amount={power.Amount})");
    }

    private static (string Label, decimal Actual, decimal Expected, bool Ok) Expect(string label, decimal actual, decimal expected)
    {
        return (label, actual, expected, Math.Abs(actual - expected) <= Tolerance);
    }

    private static bool Report(string scenario, params (string Label, decimal Actual, decimal Expected, bool Ok)[] checks)
    {
        bool ok = checks.All(c => c.Ok);
        GD.Print($"[RdpsMeter] Scenario '{scenario}': {(ok ? "PASS" : "FAIL")}");
        foreach ((string label, decimal actual, decimal expected, bool passed) in checks)
        {
            if (!passed)
            {
                GD.Print($"[RdpsMeter]     {label}: got {actual}, expected {expected}");
            }
        }

        return ok;
    }
}

internal sealed partial class SelfTestNode : Node
{
    private bool _keyWasDown;

    public override void _Process(double delta)
    {
        bool keyIsDown = Input.IsPhysicalKeyPressed(Key.F9);
        if (keyIsDown && !_keyWasDown)
        {
            _ = TaskHelper.RunSafely(SelfTest.RunAsync());
        }

        _keyWasDown = keyIsDown;
    }
}

/// <summary>
/// Minimal choice context for driving commands that raise no player choices. Applying an effect and dealing damage
/// never prompt a choice, so both signals are no-ops.
/// </summary>
internal sealed class NoOpChoiceContext : PlayerChoiceContext
{
    // No player owns these synthetic harness actions, and none of them read the owner back.
    public override ulong? OwnerId => null;

    public override Task SignalPlayerChoiceBegun(Player chooser, PlayerChoiceOptions options)
    {
        return Task.CompletedTask;
    }

    public override Task SignalPlayerChoiceEnded()
    {
        return Task.CompletedTask;
    }
}
#endif

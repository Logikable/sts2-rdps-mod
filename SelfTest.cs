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

        // Two detached fake players, never added to combat, exist only to be effect appliers with NetIds distinct
        // from the real dealer (NetId 1). Cross-player credit only happens when applier NetId != dealer NetId.
        var applier2 = new Creature(Player.CreateForNewRun(dealer.Player.Character, dealer.Player.UnlockState, 2uL), 1, 1);
        var applier3 = new Creature(Player.CreateForNewRun(dealer.Player.Character, dealer.Player.UnlockState, 3uL), 1, 1);
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
        string rdpsTitle = overlay.HarnessTitle;

        overlay.HarnessStepMode(1);
        await Settle();
        decimal adpsValue = overlay.HarnessValue(row);
        (IReadOnlyList<string> Sections, bool SplitBars) adps = overlay.HarnessBreakdown(row);
        string adpsTitle = overlay.HarnessTitle;
        MeterMode remembered = OverlayLayout.LoadMode();

        // Two meters, so either arrow reaches the other one and the pair comes back around.
        overlay.HarnessStepMode(-1);
        MeterMode back = overlay.HarnessMode;

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
            Expect("the other arrow comes back", back == MeterMode.Rdps ? 1m : 0m, 1m));
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

        // Block left standing would silently soak the next scenario's swing and make it assert against the wrong
        // number, so it is cleared alongside the HP reset. Only the block scenario ever leaves any.
        if (enemy.Block > 0)
        {
            await CreatureCmd.LoseBlock(new NoOpChoiceContext(), enemy, enemy.Block, null);
        }

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

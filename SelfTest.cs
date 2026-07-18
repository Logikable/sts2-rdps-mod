using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
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

        bool all = true;
        all &= await VulnerableScenario(context, dealer, enemy, applier2, applier3);
        all &= await FlankingScenario(context, dealer, enemy, applier2);
        all &= await StrengthScenario(context, dealer, enemy, applier2);
        all &= await PoisonScenario(context, dealer, enemy, applier2, applier3);
        all &= await PoisonAccelerantScenario(context, dealer, enemy, applier2);

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

        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null);

        CombatLedger l = CombatLedger.Instance;
        return Report("Vulnerable pro-rata",
            Expect("aDPS", l.DealtWith(you, NoCard), 9m),
            Expect("recv <-2", l.ReceivedFrom(you, "Vulnerable", 2uL), 2m),
            Expect("recv <-3", l.ReceivedFrom(you, "Vulnerable", 3uL), 1m),
            Expect("given 2->you", l.GivenTo(2uL, "Vulnerable", you), 2m),
            Expect("given 3->you", l.GivenTo(3uL, "Vulnerable", you), 1m));
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
        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null);

        CombatLedger l = CombatLedger.Instance;
        return Report("Flanking",
            Expect("aDPS", l.DealtWith(you, NoCard), 12m),
            Expect("recv <-2", l.ReceivedFrom(you, "Flanking", 2uL), 6m),
            Expect("given 2->you", l.GivenTo(2uL, "Flanking", you), 6m));
    }

    /// <summary>
    /// A teammate gifts the dealer +3 Strength. Strength only buffs its owner's own attacks, but the stacks were
    /// contributed by a teammate, so the +3 additive on the dealer's powered 6 (-> 9) is credited to the gifter.
    /// </summary>
    private static async Task<bool> StrengthScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        await PowerCmd.Apply<StrengthPower>(ctx, dealer, 3m, applier2, null);
        await CreatureCmd.Damage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null);

        CombatLedger l = CombatLedger.Instance;
        return Report("Strength (teammate-gifted)",
            Expect("aDPS", l.DealtWith(you, NoCard), 9m),
            Expect("recv <-2", l.ReceivedFrom(you, "Strength", 2uL), 3m),
            Expect("given 2->you", l.GivenTo(2uL, "Strength", you), 3m));
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

        CombatLedger l = CombatLedger.Instance;
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

        CombatLedger l = CombatLedger.Instance;
        return Report("Poison + Accelerant",
            Expect("2 aDPS (base tick)", l.DealtWith(2uL, "Poison"), 4m),
            Expect("you aDPS (accel tick)", l.DealtWith(you, "Poison"), 3m));
    }

    /// <summary>
    /// Resets the ledger and returns the enemy to a clean, full-health state: strips Artifact (which would eat the
    /// first debuff) and any effect a prior scenario left behind, then heals to full so the hit lands unblocked and
    /// pre-block shares scale onto settled damage 1:1.
    /// </summary>
    private static async Task Prep(Creature dealer, Creature enemy)
    {
        CombatLedger.Instance.Reset();
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
    public override Task SignalPlayerChoiceBegun(PlayerChoiceOptions options)
    {
        return Task.CompletedTask;
    }

    public override Task SignalPlayerChoiceEnded()
    {
        return Task.CompletedTask;
    }
}

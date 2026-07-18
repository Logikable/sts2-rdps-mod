using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter;

/// <summary>
/// A solo reproduction of the cross-player attribution path so the mod can be validated without a second networked
/// player. The multiplayer behaviour it targets only appears when the creature that applied a debuff has a different
/// NetId than the creature dealing the damage - impossible in real single-player, where you only ever benefit from
/// your own debuffs.
///
/// It mints two throwaway players (NetIds 2 and 3), stacks Vulnerable on the first enemy from both of them in unequal
/// amounts, then deals a normal powered attack from the real player. That drives the full funnel - Hook.ModifyDamage,
/// the AfterModifyingDamageAmount promotion, AfterDamageGiven, and the stack-ownership hooks - exactly as a real co-op
/// hit would. It runs on F9 (see <see cref="SelfTestNode"/>) or from the headless auto-harness.
/// </summary>
internal static class SelfTest
{
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
        GD.Print("[RdpsMeter] Self-test armed - press F9 in combat to inject a cross-player Vulnerable hit");
    }

    public static async Task RunAsync()
    {
        CombatState? state = CombatManager.Instance?.DebugOnlyGetState();
        if (state == null || !CombatManager.Instance!.IsInProgress)
        {
            GD.Print("[RdpsMeter] Self-test: not in combat, ignoring");
            return;
        }

        Creature? dealer = state.PlayerCreatures.FirstOrDefault();
        Creature? enemy = state.HittableEnemies.FirstOrDefault();
        if (dealer?.Player == null || enemy == null)
        {
            GD.Print("[RdpsMeter] Self-test: need a player and a hittable enemy");
            return;
        }

        // Two detached fake players, never added to combat, exist only to be the debuff's appliers with NetIds
        // distinct from the real dealer. They contribute unequal stacks (2 and 1) so the pro-rata split is visible:
        // both credited via one merged Vulnerable, whose bonus should divide 2/3 to NetId 2 and 1/3 to NetId 3.
        var applier2 = new Creature(Player.CreateForNewRun(dealer.Player.Character, dealer.Player.UnlockState, 2uL), 1, 1);
        var applier3 = new Creature(Player.CreateForNewRun(dealer.Player.Character, dealer.Player.UnlockState, 3uL), 1, 1);

        var context = new NoOpChoiceContext();

        // Some enemies carry Artifact, which negates the first debuffs applied to them and would swallow a test
        // Vulnerable. Strip it so both applications land.
        for (int guard = 0; enemy.GetPower<ArtifactPower>() != null && guard < 10; guard++)
        {
            await PowerCmd.Remove<ArtifactPower>(enemy);
        }

        GD.Print($"[RdpsMeter] Self-test: Vulnerable to {enemy.LogName} - 2 stacks by NetId 2, 1 stack by NetId 3, "
            + $"then {dealer.Player.NetId} attacks for 6 - expect the +3 bonus split 2:1 between NetId 2 and 3");

        await PowerCmd.Apply<VulnerablePower>(context, enemy, 2m, applier2, null);
        await PowerCmd.Apply<VulnerablePower>(context, enemy, 1m, applier3, null);

        // Read the enemy's actual merged instance - the return of the first Apply can be an orphan if the merge went
        // through a different path.
        VulnerablePower? merged = enemy.GetPower<VulnerablePower>();
        if (merged != null)
        {
            IReadOnlyDictionary<ulong, decimal>? shares = PowerOwnership.Instance.Shares(merged);
            string rendered = shares == null
                ? "none"
                : string.Join(", ", shares.Select(kv => $"{kv.Key}:{kv.Value:0.00}"));
            GD.Print($"[RdpsMeter] Self-test: recorded ownership = {rendered} (amount={merged.Amount})");
        }

        await CreatureCmd.Damage(context, new[] { enemy }, 6m, DamageProps.card, dealer, null);

        GD.Print("[RdpsMeter] Self-test: done");
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
/// Minimal choice context for driving commands that raise no player choices. Applying a debuff and dealing damage
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

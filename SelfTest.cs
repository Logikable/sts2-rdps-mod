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
/// A solo, keybind-driven reproduction of the cross-player attribution path so the mod can be validated without a
/// second networked player. The multiplayer bug it targets only appears when the creature that applied a debuff has
/// a different NetId than the creature dealing the damage - impossible in real single-player, where you only ever
/// benefit from your own debuffs.
///
/// On F9, inside a live combat, it mints a throwaway second player (NetId 2), applies Vulnerable to the first enemy
/// with that fake player as the applier, then deals a normal powered attack from the real player. That drives the
/// full funnel - Hook.ModifyDamage, the AfterModifyingDamageAmount promotion, AfterDamageGiven - exactly as a real
/// co-op hit would, so the DBG detect/consume logs and the end-of-combat summary reflect real behavior.
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

        tree.Root.AddChild(new SelfTestNode());
        _installed = true;
        GD.Print("[RdpsMeter] Self-test armed - press F9 in combat to inject a cross-player Vulnerable hit");
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
            _ = TaskHelper.RunSafely(RunAsync());
        }

        _keyWasDown = keyIsDown;
    }

    private static async Task RunAsync()
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

        GD.Print($"[RdpsMeter] Self-test: Vulnerable to {enemy.LogName} - 2 stacks by NetId 2, 1 stack by NetId 3, "
            + $"then {dealer.Player.NetId} attacks for 6 - expect the +3 bonus split 2:1 between NetId 2 and 3");

        VulnerablePower? vulnerable = await PowerCmd.Apply<VulnerablePower>(context, enemy, 2m, applier2, null);
        await PowerCmd.Apply<VulnerablePower>(context, enemy, 1m, applier3, null);

        if (vulnerable != null)
        {
            IReadOnlyDictionary<ulong, decimal>? shares = PowerOwnership.Instance.Shares(vulnerable);
            string rendered = shares == null
                ? "none"
                : string.Join(", ", shares.Select(kv => $"{kv.Key}:{kv.Value:0.00}"));
            GD.Print($"[RdpsMeter] Self-test: recorded ownership = {rendered}");
        }

        await CreatureCmd.Damage(context, new[] { enemy }, 6m, DamageProps.card, dealer, null);

        GD.Print("[RdpsMeter] Self-test: done");
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

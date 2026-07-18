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

        // A detached second player: never added to combat, it exists only to be the debuff's applier with a NetId
        // distinct from the real dealer. NetId 2 will never collide with a real SteamID64.
        Player fakePlayer = Player.CreateForNewRun(dealer.Player.Character, dealer.Player.UnlockState, 2uL);
        var fakeCreature = new Creature(fakePlayer, 1, 1);

        var context = new NoOpChoiceContext();

        GD.Print($"[RdpsMeter] Self-test: applying Vulnerable (applier NetId {fakePlayer.NetId}) to {enemy.LogName}, "
            + $"then {dealer.Player.NetId} attacks for 6 - expect ~3 credited to {fakePlayer.NetId}");

        await PowerCmd.Apply<VulnerablePower>(context, enemy, 2m, fakeCreature, null);
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

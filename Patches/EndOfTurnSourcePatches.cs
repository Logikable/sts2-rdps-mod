using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter.Patches;

/// <summary>
/// Names damage from the end-of-turn AoE buffs the game does not push onto its executing-model stack, so their hits
/// read "Hailstorm" / "The Bomb" instead of "(none)". These powers sit on the player and deal to every enemy with the
/// player as dealer but no card source, from BeforeSideTurnEnd - a hook whose dispatcher does not push - so
/// <see cref="EffectSource"/> cannot recover them from LastInvolvedModel.
///
/// A prefix pushes the power onto <see cref="ExecutingEffect"/> (which EffectSource consults as a fallback) for the
/// span of the hook, and the postfix wraps the returned Task so the matching pop runs only after the async hook - and
/// its damage - has fully settled. Wrapping the Task is what makes the pop reliable: a plain postfix on an async
/// method runs when the Task is first returned, long before the damage lands.
/// </summary>
[HarmonyPatch]
internal static class EndOfTurnSourcePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HailstormPower), nameof(HailstormPower.BeforeSideTurnEnd));
        yield return AccessTools.Method(typeof(TheBombPower), nameof(TheBombPower.BeforeSideTurnEnd));
    }

    [HarmonyPrefix]
    private static void Prefix(PowerModel __instance, out ulong? __state)
    {
        __state = null;
        if (__instance.Owner?.Player is { } player)
        {
            __state = player.NetId;
            ExecutingEffect.Push(player.NetId, __instance.Title.GetFormattedText());
        }
    }

    [HarmonyPostfix]
    private static void Postfix(ulong? __state, ref Task __result)
    {
        if (__state is ulong netId && __result != null)
        {
            __result = PopAfter(__result, netId);
        }
    }

    private static async Task PopAfter(Task inner, ulong playerNetId)
    {
        try
        {
            await inner;
        }
        finally
        {
            ExecutingEffect.Pop(playerNetId);
        }
    }
}

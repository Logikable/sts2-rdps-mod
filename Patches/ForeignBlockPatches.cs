using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter.Patches;

/// <summary>
/// Credits block a power gives *away* to the player who gave it, not the one wearing it.
///
/// Beacon of Hope sits on one player and hands half of every block they gain to each teammate. That reaches the block
/// funnel as a plain GainBlock with no card, so <see cref="BlockSource"/> recovers the name "Beacon of Hope" off the call
/// stack but has no owner to go with it - the stack yields the model's prototype, which belongs to nobody - and
/// <see cref="BlockAttributionEngine"/> then falls back to crediting whoever is wearing the block. That fallback is right
/// for every other source (they all grant to their own owner, or are potions, whose thrower is already known) and wrong
/// here, where the whole point of the card is that the block is a gift.
///
/// So the owner is recorded from the live power for the span of its hook, which is the only place it is reachable:
/// <see cref="ForeignBlockGrant"/> holds it while the grant runs and BlockSource prefers it. The pop wraps the returned
/// Task rather than sitting in a plain postfix, because the hook awaits a GainBlock per teammate and an async method
/// returns its Task long before any of that has happened.
///
/// Only Beacon of Hope needs this on the current build - `tools/find-attribution-gaps.py` lists every source that grants
/// block to something other than its owner, and the rest are either aimed at enemies (Rampart, at Turret Operators) or
/// potions (Fortifier, Ship in a Bottle, Block Potion), which name their thrower through PotionSource instead.
/// </summary>
[HarmonyPatch]
internal static class ForeignBlockPatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(BeaconOfHopePower), nameof(BeaconOfHopePower.AfterBlockGained));
    }

    [HarmonyPrefix]
    private static void Prefix(PowerModel __instance, out bool __state)
    {
        __state = false;
        if (__instance.Owner?.Player is { } giver)
        {
            __state = true;
            ForeignBlockGrant.Push(__instance.Title.GetFormattedText(), giver.NetId);
        }
    }

    [HarmonyPostfix]
    private static void Postfix(bool __state, ref Task __result)
    {
        if (__state && __result != null)
        {
            __result = PopAfter(__result);
        }
    }

    private static async Task PopAfter(Task inner)
    {
        try
        {
            await inner;
        }
        finally
        {
            ForeignBlockGrant.Pop();
        }
    }
}

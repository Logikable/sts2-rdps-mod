using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace RdpsMeter.Patches;

/// <summary>
/// Credits the Strength a relic hands to enemies to the player who owns the relic, so its cost reaches the Blocked meter.
///
/// Philosopher's Stone and Brimstone both apply enemy Strength with a null applier and no card, so the power change
/// arrives saying nothing about whose it was. The owner is reachable only from the live relic, for the span of its own
/// hook - the same shape as Beacon of Hope's block (<see cref="ForeignBlockPatches"/>). The pop wraps the returned
/// Task rather than sitting in a plain postfix: both hooks await a Strength application per enemy, and an async method
/// returns its Task long before the second one lands.
///
/// The list is every relic that gives enemies Strength as of 0.111.0; `grep -rn "Apply&lt;StrengthPower&gt;" over a
/// decompile, looking for applications aimed at opponents with a null applier, re-derives it.
/// </summary>
[HarmonyPatch]
internal static class RelicStrengthPatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(PhilosophersStone), nameof(PhilosophersStone.AfterRoomEntered));
        yield return AccessTools.Method(typeof(PhilosophersStone), nameof(PhilosophersStone.AfterCreatureAddedToCombat));
        yield return AccessTools.Method(typeof(Brimstone), nameof(Brimstone.AfterSideTurnStart));
    }

    [HarmonyPrefix]
    private static void Prefix(RelicModel __instance, out bool __state)
    {
        __state = false;
        if (__instance.Owner is { } owner)
        {
            __state = true;
            RelicStrengthGrant.Push(__instance.Title.GetFormattedText(), owner.NetId);
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
            RelicStrengthGrant.Pop();
        }
    }
}

/// <summary>The relic giving enemies Strength right now, and whose it is. Innermost wins.</summary>
internal static class RelicStrengthGrant
{
    private static readonly Stack<(string Name, ulong OwnerNetId)> Granting = new();
    private static readonly object Lock = new();

    public static void Push(string name, ulong ownerNetId)
    {
        lock (Lock)
        {
            Granting.Push((name, ownerNetId));
        }
    }

    public static void Pop()
    {
        lock (Lock)
        {
            if (Granting.Count > 0)
            {
                Granting.Pop();
            }
        }
    }

    public static (string Name, ulong OwnerNetId)? Current
    {
        get
        {
            lock (Lock)
            {
                return Granting.TryPeek(out (string Name, ulong OwnerNetId) top) ? top : null;
            }
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Granting.Clear();
        }
    }
}

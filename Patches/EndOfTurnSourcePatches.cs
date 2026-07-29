using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter.Patches;

/// <summary>
/// Names damage from player buffs that deal it from a hook the game does not push onto its executing-model stack, so
/// their hits read as the power ("Hailstorm", "The Bomb", "Outbreak") instead of "(none)". These powers sit on the
/// player and deal to every enemy with the player as dealer but no card source, from a hook whose dispatcher does not
/// push (BeforeSideTurnEnd for the end-of-turn bombs, AfterPowerAmountChanged for Outbreak's every-third-poison burst),
/// so <see cref="EffectSource"/> cannot recover them from LastInvolvedModel.
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
        yield return AccessTools.Method(typeof(OutbreakPower), nameof(OutbreakPower.AfterPowerAmountChanged));
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

    internal static async Task PopAfter(Task inner, ulong playerNetId)
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

/// <summary>
/// Names the damage an orb's passive deals, for the same reason as the powers above: nothing pushed it.
///
/// An orb is pushed onto the game's executing-model stack when it is evoked (OrbCmd.Evoke) and when something else
/// triggers its passive - Tesla Coil, Darkness, Emotion Chip, Loop - all of which go through OrbCmd.Passive. The one
/// route that does not is the orb's own end-of-turn trigger: CombatManager walks the queue and calls
/// BeforeTurnEndOrbTrigger on each orb, which calls TriggerPassive on itself, and no push happens anywhere along the
/// way. That is the route a passive fires on nearly every turn, so Glass and Lightning - the two orbs whose passive
/// deals damage - read as "(none)" for the ordinary case and as themselves only when evoked or triggered by a card.
///
/// The turn triggers themselves are what is patched, rather than the passive underneath them. They are the whole of
/// the unpushed route and nothing else reaches them, and - unlike the TriggerPassive that 0.109 funnels passives
/// through - they exist on every version the mod supports. Each orb overrides them, so the overrides are found by
/// reflection rather than listed: a version that adds an orb is covered without being edited.
/// </summary>
[HarmonyPatch]
internal static class OrbPassiveSourcePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        string[] triggers =
        {
            nameof(OrbModel.BeforeTurnEndOrbTrigger),
            nameof(OrbModel.AfterTurnStartOrbTrigger),
        };

        // Harmony's own enumerator, not Assembly.GetTypes: the game assembly has types that fail to load on their own
        // (Godot node types among them), and GetTypes throws the moment one does rather than yielding the rest.
        foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(OrbModel).Assembly))
        {
            if (type.IsAbstract || !type.IsSubclassOf(typeof(OrbModel)))
            {
                continue;
            }

            foreach (string trigger in triggers)
            {
                if (AccessTools.DeclaredMethod(type, trigger) is { } method)
                {
                    yield return method;
                }
            }
        }
    }

    [HarmonyPrefix]
    private static void Prefix(OrbModel __instance, out ulong? __state)
    {
        __state = null;
        if (__instance.Owner is { } player)
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
            __result = EndOfTurnSourcePatches.PopAfter(__result, netId);
        }
    }
}

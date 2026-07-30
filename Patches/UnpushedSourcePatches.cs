using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace RdpsMeter.Patches;

/// <summary>
/// Names damage from a player's powers that arrives with nothing on the executing-model stack to name it, so those
/// hits read as the power ("Hailstorm", "Sleight of Flesh", "Juggernaut") instead of "(none)". The power sits on the
/// player and deals with the player as dealer but no card source, so <see cref="EffectSource"/> has only
/// LastInvolvedModel to work from - and there are two different ways that comes back empty.
///
/// The common one is a hook whose dispatcher in Hook.cs never calls PushModel; roughly four fifths of them don't.
///
/// The other is subtler and cost a bug report: the hook pushes, but the power hands
/// <c>CreatureCmd.Damage</c> a **freshly constructed** PlayerChoiceContext instead of the one it was passed. A new
/// context's model stack is empty, so LastInvolvedModel is null no matter what the game pushed onto the real one.
/// Black Hole is the case that surfaced it - AfterCardPlayed pushes it faithfully, and its damage still read
/// "(none)", because the push landed on a context the damage never travelled on.
///
/// Both routes want the same repair, which is why they share this class: the name has to come from somewhere other
/// than the context.
///
/// A prefix pushes the power onto <see cref="ExecutingEffect"/> (which EffectSource consults as a fallback) for the
/// span of the hook, and the postfix wraps the returned Task so the matching pop runs only after the async hook - and
/// its damage - has fully settled. Wrapping the Task is what makes the pop reliable: a plain postfix on an async
/// method runs when the Task is first returned, long before the damage lands.
///
/// The list is derived, not remembered: `tools/find-attribution-gaps.py` asks a decompile which models deal damage
/// that would arrive unnamed. Re-run it when the game updates - the hand-maintained version of this list has been
/// caught short three times, by Outbreak, then Sleight of Flesh, then Black Hole.
///
/// Four of that script's answers are deliberately not here, for two reasons:
///
/// - Demise, Poison and Magic Bomb never reach EffectSource. Their damage is dealer-less or dealt by the creature
///   carrying the power, and <see cref="SourceAttribution"/> books it against whoever applied the effect instead.
/// - Constrict sits on the player and damages that same player, and the ledger books no hit whose target is a player,
///   so there is no row to name. Nothing a player can play applies it, so that is the only way it occurs.
///
/// Disintegration needs no entry for the same reason as Constrict: it damages its own wearer out of
/// AfterSideTurnEndLate, with that wearer as dealer, so either it is a player hitting a player - which the ledger does
/// not book - or a monster hitting itself, which is nobody's row. There is nothing to misname either way.
///
/// Three more the script reports as "UNKNOWN hook" and that are nonetheless fine. Fragrant Mushroom and Precarious
/// Shears deal from AfterObtained, which is not a combat hook at all - the damage is a dealer-less HP loss outside a
/// fight. And an orb's Passive is dispatched by OrbCmd rather than Hook.cs, so no Hook.cs dispatcher exists to find;
/// that route is covered by <see cref="OrbPassiveSourcePatches"/> below.
///
/// Thunder deserves a note because it looks like an exception and is not. It fires from AfterOrbEvoked, and OrbCmd.Evoke
/// does push the evoked orb - so the natural reading is that its damage is misnamed after the orb rather than left
/// anonymous, and that no push here could outrank that. But the push is popped on the line *before* the hook is
/// dispatched (PushModel, await Evoke, PopModel, then Hook.AfterOrbEvoked), so nothing of the orb is standing by the
/// time Thunder runs. It is an ordinary member of this list, and reads "(none)" without an entry like everything else.
/// </summary>
[HarmonyPatch]
internal static class UnpushedSourcePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(HailstormPower), nameof(HailstormPower.BeforeSideTurnEnd));
        yield return AccessTools.Method(typeof(TheBombPower), nameof(TheBombPower.BeforeSideTurnEnd));
        yield return AccessTools.Method(typeof(OutbreakPower), nameof(OutbreakPower.AfterPowerAmountChanged));
        yield return AccessTools.Method(typeof(SleightOfFleshPower), nameof(SleightOfFleshPower.AfterPowerAmountChanged));
        yield return AccessTools.Method(typeof(JuggernautPower), nameof(JuggernautPower.AfterBlockGained));
        yield return AccessTools.Method(typeof(NecroMasteryPower), nameof(NecroMasteryPower.AfterCurrentHpChanged));
        yield return AccessTools.Method(
            typeof(SmokestackPower), nameof(SmokestackPower.AfterCardGeneratedForCombat));
        yield return AccessTools.Method(typeof(ThunderPower), nameof(ThunderPower.AfterOrbEvoked));

        // Black Hole, the fresh-context case above. Both of its routes deal the same damage through the same private
        // helper, and both hand that helper's Damage call a new BlockingPlayerChoiceContext, so both are anonymous -
        // even AfterCardPlayed, whose dispatcher does push. Patch the two hooks rather than the helper they share: the
        // helper is private, and the hooks are where the owning player is still in hand.
        yield return AccessTools.Method(typeof(BlackHolePower), nameof(BlackHolePower.AfterCardPlayed));
        yield return AccessTools.Method(typeof(BlackHolePower), nameof(BlackHolePower.AfterStarsGained));
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
/// The same, for the relics that deal damage out of an unpushed hook: Parrying Shield when you end a turn still
/// holding block, Screaming Flagon on an empty hand, Stone Calendar on its turn. All three read "(none)" without this.
///
/// A separate class from the powers above only because of the type: a relic's Owner is the Player itself, where a
/// power's is that player's Creature, so the prefix cannot be shared even though everything it does is the same.
/// </summary>
[HarmonyPatch]
internal static class RelicSourcePatches
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ParryingShield), nameof(ParryingShield.AfterSideTurnEnd));
        yield return AccessTools.Method(typeof(ScreamingFlagon), nameof(ScreamingFlagon.BeforeSideTurnEnd));
        yield return AccessTools.Method(typeof(StoneCalendar), nameof(StoneCalendar.BeforeSideTurnEnd));
    }

    [HarmonyPrefix]
    private static void Prefix(RelicModel __instance, out ulong? __state)
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
            __result = UnpushedSourcePatches.PopAfter(__result, netId);
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
            __result = UnpushedSourcePatches.PopAfter(__result, netId);
        }
    }
}

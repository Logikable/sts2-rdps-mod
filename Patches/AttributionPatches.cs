using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter.Patches;

/// <summary>
/// Wires the attribution engine into the game's damage flow across three hooks, matching the shape of
/// CreatureCmd.Damage: it computes every target's damage in one loop (Hook.ModifyDamage +
/// Hook.AfterModifyingDamageAmount) and then settles every target in a second loop (Hook.AfterDamageGiven).
///
/// - Hook.ModifyDamage fires for real hits, card previews, and enemy-intent display alike. We compute attribution
///   here (where the modifier list and powers are freshest and reproduce the returned damage exactly) but only stash
///   it, keyed by the modifier-list reference.
/// - Hook.AfterModifyingDamageAmount is called solely from the damage funnel, so it is a reliable "this is a real
///   hit" gate: it promotes the stashed attribution into a per-target queue. Preview/intent calcs never reach it and
///   are discarded when their modifier list is garbage-collected.
/// - Hook.AfterDamageGiven delivers the settled DamageResult per target; we dequeue the matching attribution and
///   fold it into the ledger.
/// </summary>
[HarmonyPatch(typeof(Hook))]
internal static class AttributionPatches
{
    // Keyed by the modifier-list reference returned from Hook.ModifyDamage; the same reference is handed to
    // Hook.AfterModifyingDamageAmount. A weak table means un-promoted (preview/intent) entries cost nothing - they
    // vanish with their list.
    private static readonly ConditionalWeakTable<object, HitAttribution> Calcs = new();

    private static readonly Dictionary<Creature, Queue<HitAttribution>> Pending = new();
    private static readonly object PendingLock = new();

    // Resolved once from Hook.ModifyDamage's own parameters: the index of its cardPlay argument, or -1 on a build
    // that has none. -2 means "not yet resolved".
    private static int _cardPlayArgIndex = -2;

    [HarmonyPatch(nameof(Hook.ModifyDamage))]
    [HarmonyPostfix]
    private static void ModifyDamagePostfix(
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        CardModel? cardSource,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode,
        IEnumerable<AbstractModel> modifiers,
        decimal __result,
        object[] __args,
        MethodBase __originalMethod)
    {
        if (previewMode != CardPreviewMode.None || modifyDamageHookType != ModifyDamageHookType.All)
        {
            return;
        }

        // cardPlay is read from the raw argument array rather than declared as a parameter: a build whose
        // Hook.ModifyDamage has no cardPlay would refuse to bind a patch that declares one, disabling attribution
        // wholesale. When absent, recompute runs without it - that build's damage pipeline takes no cardPlay either.
        CardPlay? cardPlay = CardPlayArg(__originalMethod, __args);

        if (CombatManager.Instance is not { IsInProgress: true })
        {
            return;
        }

        // Damage that lands on a player - Infection and similar self/ally-damaging cards, a Doubt/retaliation hit
        // onto a teammate - is not offensive output, so it never belongs in the meter. Drop it before it is stashed.
        if (target?.Player != null)
        {
            return;
        }

        IReadOnlyList<AbstractModel> modifierList = modifiers as IReadOnlyList<AbstractModel> ?? modifiers.ToList();
        HitAttribution attribution = AttributionEngine.Attribute(
            damage, props, target, dealer, cardSource, cardPlay, modifyDamageHookType, modifierList, __result);

        if (attribution.DealerNetId is ulong dealerNetId && dealer?.Player != null)
        {
            CombatLedger.Name(dealerNetId, PlayerIdentity.Name(dealer.Player));
        }

        foreach (ExternalContribution contribution in attribution.Externals)
        {
            CombatLedger.Name(contribution.ApplierNetId, PlayerIdentity.Name(contribution.ApplierNetId));
        }

        Calcs.AddOrUpdate(modifiers, attribution);
    }

    private static CardPlay? CardPlayArg(MethodBase original, object[] args)
    {
        if (_cardPlayArgIndex == -2)
        {
            ParameterInfo[] parameters = original.GetParameters();
            _cardPlayArgIndex = Array.FindIndex(parameters, p => p.Name == "cardPlay" && p.ParameterType == typeof(CardPlay));
        }

        return _cardPlayArgIndex >= 0 ? args[_cardPlayArgIndex] as CardPlay : null;
    }

    [HarmonyPatch(nameof(Hook.AfterModifyingDamageAmount))]
    [HarmonyPrefix]
    private static void AfterModifyingDamageAmountPrefix(IEnumerable<AbstractModel> modifiers)
    {
        if (!Calcs.TryGetValue(modifiers, out HitAttribution? attribution) || attribution.Target == null)
        {
            return;
        }

        lock (PendingLock)
        {
            if (!Pending.TryGetValue(attribution.Target, out Queue<HitAttribution>? queue))
            {
                queue = new Queue<HitAttribution>();
                Pending[attribution.Target] = queue;
            }

            queue.Enqueue(attribution);
        }
    }

    [HarmonyPatch(nameof(Hook.AfterDamageGiven))]
    [HarmonyPrefix]
    private static void AfterDamageGivenPrefix(Creature target, DamageResult results)
    {
        // Drain the queued (dealer-less) calc for this tick first so the queue never leaks, then decide how to book
        // it. A poison tick's calc has no dealer and would be discarded by ApplyHit anyway; the poison path owns it.
        HitAttribution? attribution = null;
        lock (PendingLock)
        {
            if (Pending.TryGetValue(target, out Queue<HitAttribution>? queue) && queue.Count > 0)
            {
                attribution = queue.Dequeue();
                if (queue.Count == 0)
                {
                    Pending.Remove(target);
                }
            }
        }

        // A hit on a player (Infection and similar self/ally damage) is not damage dealt to the enemy team. Still
        // consume any queued DoT/source entry below so it can't leak onto a later enemy hit, but credit no one for it.
        bool targetIsPlayer = target.Player != null;

        if (PoisonAttribution.TryConsume(target, out IReadOnlyDictionary<ulong, decimal> shares))
        {
            if (!targetIsPlayer)
            {
                foreach (ulong netId in shares.Keys)
                {
                    CombatLedger.Name(netId, PlayerIdentity.Name(netId));
                }

                CombatLedger.Record("Poison", shares, results.UnblockedDamage);
            }

            return;
        }

        if (SourceAttribution.TryConsume(target, out string sourceEffect, out IReadOnlyDictionary<ulong, decimal> sourceShares))
        {
            if (!targetIsPlayer)
            {
                foreach (ulong netId in sourceShares.Keys)
                {
                    CombatLedger.Name(netId, PlayerIdentity.Name(netId));
                }

                CombatLedger.Record(sourceEffect, sourceShares, results.UnblockedDamage);
            }

            return;
        }

        if (attribution != null && !targetIsPlayer)
        {
            CombatLedger.Record(attribution, results);
        }
    }

    public static void ClearPending()
    {
        lock (PendingLock)
        {
            Pending.Clear();
        }

        PoisonAttribution.Clear();
        SourceAttribution.Clear();
        PotionSource.Clear();
        EffectSource.Clear();
        ExecutingEffect.Clear();
        ConcoctAttribution.Clear();
    }
}

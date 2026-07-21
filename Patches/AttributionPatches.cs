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

    [HarmonyPatch(nameof(Hook.ModifyDamage))]
    [HarmonyPostfix]
    private static void ModifyDamagePostfix(
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode,
        IEnumerable<AbstractModel> modifiers,
        decimal __result)
    {
        if (previewMode != CardPreviewMode.None || modifyDamageHookType != ModifyDamageHookType.All)
        {
            return;
        }

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

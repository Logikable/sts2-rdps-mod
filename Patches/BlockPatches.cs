using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter.Patches;

/// <summary>
/// Wires the Blocked meter into the game's block flow, in the same shape as the damage side: attribute where the numbers
/// are freshest, promote only what turns out to be real, and book only when it settles.
///
/// - CreatureCmd.GainBlock's prefix is the last moment the granting relic or power is still on the call stack (the rest
///   of that method is async), so a gain with no card behind it is named there.
/// - Hook.ModifyBlock runs for card previews as well as real gains. Attribution is computed there - it is where the
///   modifier list and the powers behind it are live - but only stashed, keyed by the modifier list the game hands back.
/// - Hook.AfterModifyingBlockAmount is called from the block funnel alone, so it is the "this is a real gain" gate, and
///   it carries the same list. Previews never reach it and are collected along with their list.
/// - Creature.GainBlockInternal is where block actually lands; the promoted grant joins the wearer's pool there.
/// - Creature.DamageBlockInternal is where block is spent, and that - not the gaining - is what the meter counts.
///
/// The three parts that need a before-and-after view of the creature's block live in their own patch classes, so each
/// prefix hands its __state to exactly one postfix.
/// </summary>
[HarmonyPatch]
internal static class BlockPatches
{
    // Keyed by the modifier list Hook.ModifyBlock returns, which is the same reference Hook.AfterModifyingBlockAmount is
    // given. Weak, so a preview's attribution costs nothing: it goes when its list does.
    private static readonly ConditionalWeakTable<object, BlockGrant> Calcs = new();

    private static readonly Dictionary<Creature, Queue<BlockGrant>> Pending = new();
    private static readonly object PendingLock = new();

    /// <summary>
    /// Names the relic, power or potion behind a block gain no card explains. The check for a live combat mirrors
    /// GainBlock's own opening guards, so a gain the game is about to drop never leaves a name behind for the next one.
    /// </summary>
    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.GainBlock),
        new[] { typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(CardPlay), typeof(bool) })]
    [HarmonyPrefix]
    private static void GainBlockPrefix(Creature creature, CardPlay? cardPlay)
    {
        if (cardPlay?.Card == null
            && creature.Player != null
            && !creature.IsDead
            && CombatManager.Instance is { IsInProgress: true, IsOverOrEnding: false })
        {
            BlockSource.Capture(creature);
        }
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyBlock))]
    [HarmonyPostfix]
    private static void ModifyBlockPostfix(
        Creature target,
        decimal block,
        ValueProp props,
        CardModel? cardSource,
        CardPlay? cardPlay,
        IEnumerable<AbstractModel> modifiers,
        decimal __result)
    {
        // Only players are metered: an enemy's block is the thing being punched through, not a thing anybody earned.
        if (target.Player == null || CombatManager.Instance is not { IsInProgress: true })
        {
            return;
        }

        IReadOnlyList<AbstractModel> list = modifiers as IReadOnlyList<AbstractModel> ?? modifiers.ToList();
        BlockGrant grant = BlockAttributionEngine.Attribute(block, props, target, cardSource, cardPlay, list, __result);
        Calcs.AddOrUpdate(modifiers, grant);
    }

    /// <summary>
    /// Promotes a stashed attribution once the game commits to the gain. The zero check is the game's own: GainBlock
    /// applies nothing at or below zero, so promoting one would leave an entry nothing ever comes to collect.
    /// </summary>
    [HarmonyPatch(typeof(Hook), nameof(Hook.AfterModifyingBlockAmount))]
    [HarmonyPrefix]
    private static void AfterModifyingBlockAmountPrefix(decimal modifiedBlock, IEnumerable<AbstractModel> modifiers)
    {
        if (modifiedBlock <= 0m || !Calcs.TryGetValue(modifiers, out BlockGrant? grant))
        {
            return;
        }

        lock (PendingLock)
        {
            if (!Pending.TryGetValue(grant.Receiver, out Queue<BlockGrant>? queue))
            {
                queue = new Queue<BlockGrant>();
                Pending[grant.Receiver] = queue;
            }

            queue.Enqueue(grant);
        }
    }

    /// <summary>The grant now landing on this creature, or null when nothing was promoted for it.</summary>
    internal static BlockGrant? TakePending(Creature creature)
    {
        lock (PendingLock)
        {
            if (!Pending.TryGetValue(creature, out Queue<BlockGrant>? queue) || queue.Count == 0)
            {
                return null;
            }

            BlockGrant grant = queue.Dequeue();
            if (queue.Count == 0)
            {
                Pending.Remove(creature);
            }

            return grant;
        }
    }

    public static void Clear()
    {
        lock (PendingLock)
        {
            Pending.Clear();
        }

        BlockPool.Clear();
        BlockSource.Clear();

        // Balanced by its own postfix in the normal case; cleared here too so a hook that threw between the push and the
        // pop cannot leave a giver's name attached to the next combat's block.
        ForeignBlockGrant.Clear();
    }
}

/// <summary>Block landing on a creature: the promoted grant joins their pool, tagged with what it was worth.</summary>
[HarmonyPatch(typeof(Creature), nameof(Creature.GainBlockInternal))]
internal static class BlockGainedPatch
{
    [HarmonyPrefix]
    private static void Prefix(Creature __instance, out int __state)
    {
        __state = __instance.Block;
    }

    [HarmonyPostfix]
    private static void Postfix(Creature __instance, int __state)
    {
        if (BlockPatches.TakePending(__instance) is not { } grant)
        {
            return;
        }

        foreach (BlockStrand strand in grant.Strands)
        {
            CombatLedger.Name(strand.OwnerNetId, PlayerIdentity.Name(strand.OwnerNetId));
        }

        BlockPool.Gained(grant, __state);
    }
}

/// <summary>
/// Block being spent, which is the only moment the Blocked meter moves. The game takes block down by the truncated
/// absorption (Block is a whole number), so that is what the pool is charged - the meter and the creature spend the
/// same block. The block standing before the hit is read in the prefix because the postfix is too late to ask.
/// </summary>
[HarmonyPatch(typeof(Creature), nameof(Creature.DamageBlockInternal))]
internal static class BlockSpentPatch
{
    [HarmonyPrefix]
    private static void Prefix(Creature __instance, out int __state)
    {
        __state = __instance.Block;
    }

    [HarmonyPostfix]
    private static void Postfix(Creature __instance, int __state, decimal __result)
    {
        if (__instance.Player == null || __result <= 0m || CombatManager.Instance is not { IsInProgress: true })
        {
            return;
        }

        IReadOnlyList<BlockStrand> spent = BlockPool.Spent(__instance, Math.Truncate(__result), __state);
        if (spent.Count > 0)
        {
            CombatLedger.RecordBlock(__instance.Player.NetId, spent);
        }
    }
}

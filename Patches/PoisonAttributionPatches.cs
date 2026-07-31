using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter.Patches;

/// <summary>
/// Opens the poison context at the one place every tick sequence now starts.
///
/// Through 0.109.1 poison only ever ticked at turn start, so bracketing AfterSideTurnStart was the same thing as
/// bracketing the ticks. 0.110.0 lifted the loop into a public Trigger() and left AfterSideTurnStart as a guard that
/// calls it - and, more to the point, reworked Outbreak from a power into a skill that applies Poison to every enemy
/// and then calls power.Trigger() on each of them **immediately**. Those ticks never pass through AfterSideTurnStart.
/// Left alone, the whole payload of a rare 3-cost card would find no open context, fall through to ordinary hit
/// attribution, and - being a dealer-less unblockable hit - land as nobody's damage.
///
/// Patching Trigger covers both routes at once on 0.110.0, and needs no participants guard of its own: the turn-start
/// route applies that guard before it calls in, and a direct call is unconditional by construction.
/// </summary>
[HarmonyPatch]
internal static class PoisonTriggerPatch
{
    internal static MethodInfo? TriggerMethod()
    {
        return AccessTools.Method(typeof(PoisonPower), "Trigger");
    }

    // Older versions have no Trigger; PoisonAttributionPatches below brackets the turn-start hook for them instead.
    private static bool Prepare()
    {
        return TriggerMethod() != null;
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        if (TriggerMethod() is { } method)
        {
            yield return method;
        }
    }

    [HarmonyPrefix]
    private static void Prefix(PoisonPower __instance)
    {
        PoisonAttribution.Begin(__instance);
    }
}

/// <summary>
/// Attributes Poison, which the counterfactual engine cannot see: a poison tick is dealt through CreatureCmd.Damage
/// with a null dealer, so it never carries a modifier list and Hook.AfterDamageGiven alone cannot tell it apart from
/// any other dealer-less self-damage (Strangle, Demise). This prefix runs just before PoisonPower drives its ticks
/// for a turn and records a per-target context - the poison's per-player stack ownership, the Accelerant owners, and
/// how many ticks to expect - which AttributionPatches then reads when each tick settles.
///
/// Poison ticks 1 + (total Accelerant on the poisoned creature's opponents) times, decrementing each tick, capped at
/// the poison amount. The first (natural) tick belongs to whoever applied the poison; the Accelerant-driven extra
/// ticks belong to the players whose Accelerant forced them.
///
/// The bracket is prefix-only: a Harmony postfix on an async method fires when the Task is first returned, long
/// before the awaited ticks land, so it cannot mark the end. Instead each settled tick decrements the expected count
/// and the context clears itself once the last one is consumed. A context left behind by a creature that died
/// mid-sequence is harmless (that creature takes no more damage) and is cleared at combat end.
/// </summary>
[HarmonyPatch(typeof(PoisonPower), nameof(PoisonPower.AfterSideTurnStart))]
internal static class PoisonAttributionPatches
{
    // Only for versions with no Trigger() to bracket. Where both exist this would double-Begin the same sequence -
    // harmless in effect, since Begin overwrites the context wholesale, but it would mean the turn-start route and the
    // direct route were bracketed at different depths, and only one of them resets the tick index correctly.
    private static bool Prepare()
    {
        return PoisonTriggerPatch.TriggerMethod() == null;
    }

    [HarmonyPrefix]
    private static void Prefix(PoisonPower __instance, IReadOnlyList<Creature> participants)
    {
        // Mirror the method's own guard: it only ticks when its owner is among the creatures whose turn is starting.
        if (participants.Contains(__instance.Owner))
        {
            PoisonAttribution.Begin(__instance);
        }
    }
}

/// <summary>
/// Per-target poison tick context, populated at turn start and drained tick by tick as damage settles.
/// </summary>
internal static class PoisonAttribution
{
    private sealed class Context
    {
        public required IReadOnlyDictionary<ulong, decimal> PoisonShares { get; init; }
        public required IReadOnlyDictionary<ulong, decimal> AccelShares { get; init; }
        public int TickIndex { get; set; }
        public int Remaining { get; set; }
    }

    private static readonly Dictionary<Creature, Context> Active = new();
    private static readonly object Lock = new();

    public static void Begin(PoisonPower power)
    {
        Creature owner = power.Owner;

        IReadOnlyDictionary<ulong, decimal> poisonShares =
            AttributionEngine.OwnershipShares(power) ?? new Dictionary<ulong, decimal>();

        // Accelerant lives on the poisoned creature's opponents (the players); the total drives both the extra-tick
        // count and how the extra ticks are shared. Weight each owner by their Accelerant amount.
        var accel = new Dictionary<ulong, decimal>();
        decimal accelTotal = 0m;
        IReadOnlyList<Creature> opponents = owner.CombatState?.GetOpponentsOf(owner) ?? new List<Creature>();
        foreach (Creature opponent in opponents)
        {
            if (!opponent.IsAlive || opponent.Player is not { } player)
            {
                continue;
            }

            decimal amount = opponent.GetPowerAmount<AccelerantPower>();
            if (amount > 0m)
            {
                ulong netId = player.NetId;
                accel[netId] = accel.GetValueOrDefault(netId) + amount;
                accelTotal += amount;
            }
        }

        if (accelTotal > 0m)
        {
            foreach (ulong netId in accel.Keys.ToList())
            {
                accel[netId] /= accelTotal;
            }
        }

        int expected = (int)Math.Min(power.Amount, 1m + accelTotal);

        lock (Lock)
        {
            Active[owner] = new Context
            {
                PoisonShares = poisonShares,
                AccelShares = accel,
                TickIndex = 0,
                Remaining = expected,
            };
        }
    }

    /// <summary>
    /// Claims the next poison tick for <paramref name="target"/> and returns whose damage it is: the poison appliers
    /// for the first (natural) tick, the Accelerant owners for the forced extras. False when no poison tick is
    /// pending for this creature, so the caller falls back to normal hit attribution.
    /// </summary>
    public static bool TryConsume(Creature target, out IReadOnlyDictionary<ulong, decimal> shares)
    {
        lock (Lock)
        {
            if (Active.TryGetValue(target, out Context? ctx) && ctx.Remaining > 0)
            {
                shares = ctx.TickIndex == 0 || ctx.AccelShares.Count == 0 ? ctx.PoisonShares : ctx.AccelShares;
                ctx.TickIndex++;
                ctx.Remaining--;
                if (ctx.Remaining == 0)
                {
                    Active.Remove(target);
                }

                return shares.Count > 0;
            }
        }

        shares = new Dictionary<ulong, decimal>();
        return false;
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Active.Clear();
        }
    }
}

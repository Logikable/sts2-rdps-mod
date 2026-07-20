using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter.Patches;

/// <summary>
/// Redirects Concoct's poison to the player who played Concoct.
///
/// Concoct puts ConcoctPower on an ally; when that ally lands a powered attack, ConcoctPower applies Poison with the
/// ally as the applier. So the poison - and every tick it deals - would be credited to the ally who swung rather than
/// to the player whose Concoct enabled it. This arms a redirect keyed by the poisoned enemy just before ConcoctPower
/// applies its poison, carrying the ConcoctPower owner's shares; <see cref="PowerOwnership"/> reads it when the poison
/// stacks land and books them to that player instead. The arming is consumed by the very next poison change on that
/// enemy (the one ConcoctPower is about to apply) and cleared at combat end, so it never mis-credits a later poison.
/// </summary>
[HarmonyPatch(typeof(ConcoctPower), nameof(ConcoctPower.AfterDamageGiven))]
internal static class ConcoctAttributionPatches
{
    [HarmonyPrefix]
    private static void Prefix(ConcoctPower __instance, Creature? dealer, DamageResult result, ValueProp props, Creature target)
    {
        // Mirror ConcoctPower's own gate so a redirect is armed only when it will actually apply poison.
        if (dealer != __instance.Owner || !props.IsPoweredAttack() || result.UnblockedDamage <= 0)
        {
            return;
        }

        IReadOnlyDictionary<ulong, decimal>? shares = AttributionEngine.OwnershipShares(__instance);
        if (shares != null)
        {
            ConcoctAttribution.Arm(target, shares);
        }
    }
}

/// <summary>
/// Per-enemy redirect for a poison ConcoctPower is about to apply: the shares of the player(s) who played Concoct,
/// which <see cref="PowerOwnership"/> credits in place of the ally the game records as the applier.
/// </summary>
internal static class ConcoctAttribution
{
    private static readonly Dictionary<Creature, IReadOnlyDictionary<ulong, decimal>> Pending = new();
    private static readonly object Lock = new();

    public static void Arm(Creature target, IReadOnlyDictionary<ulong, decimal> shares)
    {
        lock (Lock)
        {
            Pending[target] = shares;
        }
    }

    /// <summary>
    /// The Concoct owner's shares to credit for poison landing on <paramref name="target"/>, consuming the arming, or
    /// null when this poison change was not driven by Concoct.
    /// </summary>
    public static IReadOnlyDictionary<ulong, decimal>? Consume(Creature target)
    {
        lock (Lock)
        {
            return Pending.Remove(target, out IReadOnlyDictionary<ulong, decimal>? shares) ? shares : null;
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Pending.Clear();
        }
    }
}

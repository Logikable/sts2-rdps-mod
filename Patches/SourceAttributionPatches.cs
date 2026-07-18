using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter.Patches;

/// <summary>
/// Registers dealer-less player DoTs with <see cref="SourceAttribution"/> just before they deal damage, so their HP
/// loss is credited to the players responsible rather than dropped. Each of these powers deals damage through
/// CreatureCmd.Damage with a null dealer, which the game's ModifyDamage pipeline cannot attribute; a prefix at the
/// power's trigger - fired only when that trigger will actually deal damage - names the target and owner shares, and
/// AfterDamageGiven consumes the matching entry.
///
/// The prefixes run at the async method's synchronous entry, before its awaited body deals the damage, so the entry
/// is waiting by the time the settled hit arrives. Each is gated on the same condition the game uses to decide
/// whether to deal damage, so a registration is never left stranded to mis-credit a later hit.
///
/// The bare class-level [HarmonyPatch] marks the type so the loader picks it up; each method carries its own target.
/// </summary>
[HarmonyPatch]
internal static class SourceAttributionPatches
{
    /// <summary>
    /// Demise ticks at the end of a side turn its owner took part in, dealing unblockable damage to that owner (an
    /// enemy the potion was thrown at). The removed HP is the potion user's own damage.
    /// </summary>
    [HarmonyPatch(typeof(DemisePower), nameof(DemisePower.AfterSideTurnEnd))]
    [HarmonyPrefix]
    private static void DemiseAfterSideTurnEndPrefix(DemisePower __instance, IEnumerable<Creature> participants)
    {
        if (!participants.Contains(__instance.Owner))
        {
            return;
        }

        IReadOnlyDictionary<ulong, decimal>? shares = AttributionEngine.OwnershipShares(__instance);
        if (shares != null)
        {
            SourceAttribution.Register(__instance.Owner, "Demise", shares);
        }
    }
}

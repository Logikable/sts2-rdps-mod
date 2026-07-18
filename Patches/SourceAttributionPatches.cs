using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
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

    // Cards a Strangle instance will damage on: the game arms a card in BeforeCardPlayed and fires in AfterCardPlayed,
    // so mirroring that set here means we register exactly when it will deal damage - and never on the card that
    // applied the Strangle itself, which was never armed.
    private static readonly HashSet<(StranglePower Power, CardModel Card)> ArmedStrangles = new();
    private static readonly object StrangleLock = new();

    /// <summary>
    /// Strangle arms a card played by its applier, then deals unblockable dealer-less damage to the strangled enemy
    /// after that card resolves. Arm the same cards the game does so the after-hook knows a hit is coming.
    /// </summary>
    [HarmonyPatch(typeof(StranglePower), nameof(StranglePower.BeforeCardPlayed))]
    [HarmonyPrefix]
    private static void StrangleBeforeCardPlayedPrefix(StranglePower __instance, CardPlay cardPlay)
    {
        if (__instance.Applier?.Player == null || cardPlay.Card.Owner != __instance.Applier.Player)
        {
            return;
        }

        lock (StrangleLock)
        {
            ArmedStrangles.Add((__instance, cardPlay.Card));
        }
    }

    [HarmonyPatch(typeof(StranglePower), nameof(StranglePower.AfterCardPlayed))]
    [HarmonyPrefix]
    private static void StrangleAfterCardPlayedPrefix(StranglePower __instance, CardPlay cardPlay)
    {
        bool armed;
        lock (StrangleLock)
        {
            armed = ArmedStrangles.Remove((__instance, cardPlay.Card));
        }

        if (!armed)
        {
            return;
        }

        IReadOnlyDictionary<ulong, decimal>? shares = AttributionEngine.OwnershipShares(__instance);
        if (shares != null)
        {
            SourceAttribution.Register(__instance.Owner, "Strangle", shares);
        }
    }
}

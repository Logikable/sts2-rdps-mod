using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter.Patches;

/// <summary>
/// Names the potion behind a dealer-less-card hit so the breakdown shows "Fire Potion" instead of "(none)".
///
/// A potion's damage runs inside PotionModel.OnUseWrapper with the player as dealer but no card source. The wrapper's
/// synchronous entry records the potion title against its owner, before OnUse deals any damage; the matching clear is
/// CombatManager.EndCardOrPotionEffect, whose prefix runs after OnUse has settled, so the label covers exactly the
/// potion's own hits and no later one.
///
/// The bare class-level [HarmonyPatch] marks the type so the loader picks it up; each method carries its own target.
/// </summary>
[HarmonyPatch]
internal static class PotionSourcePatches
{
    [HarmonyPatch(typeof(PotionModel), nameof(PotionModel.OnUseWrapper))]
    [HarmonyPrefix]
    private static void OnUseWrapperPrefix(PotionModel __instance)
    {
        PotionSource.Begin(__instance.Owner.NetId, __instance.Title.GetFormattedText());
    }

    [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EndCardOrPotionEffect))]
    [HarmonyPrefix]
    private static void EndCardOrPotionEffectPrefix(Player player)
    {
        PotionSource.End(player.NetId);
    }
}

using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter.Patches;

/// <summary>
/// Lets a counterfactual replay switch Debilitate's contribution to Vulnerable off without touching anything else the
/// game computes. Returning the multiplier unchanged is Debilitate's own no-op answer (it is what the real method
/// returns for an unpowered attack), so the suppressed run is the run where this card was never played.
///
/// The patch is only ever live inside <see cref="RdpsMeter.AttributionEngine.Recompute"/>; when the game is dealing
/// real damage nothing is suppressed and the original runs. See <see cref="RdpsMeter.VulnerableBoosts"/> for why the
/// engine goes through the game's method at all instead of reimplementing the formula.
/// </summary>
[HarmonyPatch(typeof(DebilitatePower), nameof(DebilitatePower.ModifyVulnerableMultiplier))]
internal static class DebilitateBoostPatch
{
    [HarmonyPrefix]
    private static bool Prefix(DebilitatePower __instance, decimal amount, ref decimal __result)
    {
        if (!VulnerableBoosts.IsSuppressed(__instance))
        {
            return true;
        }

        __result = amount;
        return false;
    }
}

/// <summary>
/// The same, for Cruelty. Cruelty is cast on yourself (<c>TargetType.Self</c>, applied by its own owner), so its
/// share is normally filtered out as the dealer's own and this suppression never fires. It exists for the one case
/// where the dealer is not the owner: a pet swings using its owner's Cruelty, and the engine has to be able to ask
/// what the swing would have been without it.
/// </summary>
[HarmonyPatch(typeof(CrueltyPower), nameof(CrueltyPower.ModifyVulnerableMultiplier))]
internal static class CrueltyBoostPatch
{
    [HarmonyPrefix]
    private static bool Prefix(CrueltyPower __instance, decimal amount, ref decimal __result)
    {
        if (!VulnerableBoosts.IsSuppressed(__instance))
        {
            return true;
        }

        __result = amount;
        return false;
    }
}

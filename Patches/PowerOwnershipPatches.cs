using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter.Patches;

/// <summary>
/// Records the applier behind every stack change so multi-player debuffs can be attributed pro-rata.
///
/// Hook.AfterPowerAmountChanged is the one hook both stack paths funnel through - PowerCmd.Apply for a fresh power
/// and PowerCmd.ModifyAmount for a merge onto an existing one - each passing the power, the stacks added, and the
/// applier. Patching only this hook captures every contribution once, with no double counting.
/// </summary>
[HarmonyPatch(typeof(Hook))]
internal static class PowerOwnershipPatches
{
    [HarmonyPatch(nameof(Hook.AfterPowerAmountChanged))]
    [HarmonyPrefix]
    private static void AfterPowerAmountChangedPrefix(PowerModel power, decimal amount, Creature? applier)
    {
        // Poison applied by a Concoct buff is booked to the ally who swung; redirect those stacks to the player who
        // played Concoct instead, split across its owners.
        if (power is PoisonPower && ConcoctAttribution.Consume(power.Owner) is { } concoctShares)
        {
            foreach ((ulong netId, decimal fraction) in concoctShares)
            {
                PowerOwnership.Instance.Record(power, netId, amount * fraction);
            }

            return;
        }

        if (applier?.Player?.NetId is ulong applierNetId)
        {
            PowerOwnership.Instance.Record(power, applierNetId, amount);
        }
    }
}

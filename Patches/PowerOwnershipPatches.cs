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
    private static void AfterPowerAmountChangedPrefix(
        PowerModel power, decimal amount, Creature? applier, CardModel? cardSource)
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
            PowerOwnership.Instance.Record(power, applierNetId, amount, GrantedBy(power, applierNetId, cardSource));
        }
    }

    /// <summary>
    /// What granted these stacks, for a power whose own name would not say. Only Strength needs this: it is the one
    /// damage power every source stacks into a single shared instance, so without it a teammate's Coordinate, Blaze
    /// and thrown Flex Potion all merge into one "Strength" row. Every other power names its own effect - a
    /// Vulnerable share should read "Vulnerable", not "Bash" - so they record no source and are unchanged.
    ///
    /// The card is the direct source (Blaze applies Strength with itself as cardSource; Coordinate's own power passes
    /// its card through when it re-applies Strength internally). A potion carries no cardSource, so it comes from the
    /// potion being resolved, and a relic from the effect stack - the same two the damage rows are named from.
    /// </summary>
    private static string? GrantedBy(PowerModel power, ulong applierNetId, CardModel? cardSource)
    {
        if (power is not StrengthPower)
        {
            return null;
        }

        try
        {
            return cardSource?.TitleLocString.GetFormattedText()
                ?? PotionSource.Current(applierNetId)
                ?? EffectSource.Current(applierNetId);
        }
        catch (Exception)
        {
            // A name is never worth breaking a power application over; fall back to the power's own.
            return null;
        }
    }
}

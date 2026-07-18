using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter.Patches;

/// <summary>
/// Attributes Doom, which is not damage at all: DoomPower.DoomKill instakills any creature whose HP has fallen to or
/// below its Doom amount, via CreatureCmd.Kill - there is no damage event, so neither the counterfactual engine nor
/// the poison path sees it. The value of a Doom kill is the HP it removed, i.e. the victim's HP the instant before
/// the kill, so this prefix reads CurrentHp before the kill loop zeroes it and books it as "Doom" aDPS split by the
/// per-player Doom-stack ownership. EndOfDays and any other caller of DoomKill are covered for free.
///
/// A monster Dooming a player resolves to a non-player applier, so ownership comes back null and the death is skipped
/// - correct, since that is not co-op rDPS.
/// </summary>
[HarmonyPatch(typeof(DoomPower), nameof(DoomPower.DoomKill))]
internal static class DoomAttributionPatches
{
    [HarmonyPrefix]
    private static void Prefix(IReadOnlyList<Creature> creatures)
    {
        foreach (Creature creature in creatures)
        {
            DoomPower? doom = creature.GetPower<DoomPower>();
            if (doom == null)
            {
                continue;
            }

            IReadOnlyDictionary<ulong, decimal>? shares = AttributionEngine.OwnershipShares(doom);
            if (shares == null || shares.Count == 0)
            {
                continue;
            }

            foreach (ulong netId in shares.Keys)
            {
                CombatLedger.Instance.RecordName(netId, PlayerIdentity.Name(netId));
            }

            CombatLedger.Instance.ApplyDot("Doom", shares, creature.CurrentHp);
        }
    }
}

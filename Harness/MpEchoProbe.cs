// Developer-only two-peer harness - compiled in only under -p:Harness=true (see RdpsMeter.csproj). Never ships.
#if RDPS_HARNESS
using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter.Harness;

/// <summary>
/// Says in the log when a card's play count was raised, and when a raised play actually resolves.
///
/// A scenario named after Echo Form is worth nothing unless the echo demonstrably happened, and neither the game nor
/// the meter says so on its own. Two runs already looked like Echo Form coverage and were not: in the first the peer
/// under test never drew one, and in the second it spent every turn's energy on another copy and never attacked - and
/// Echo Form's payload is entirely in the cards played *after* it. So the evidence has to be printed, not inferred
/// from the fact that the card was played.
///
/// Both hooks are read-only and run on both peers, like the rest of the harness scaffolding.
/// </summary>
[HarmonyPatch]
internal static class MpEchoProbe
{
    private static bool Prepare()
    {
        return MpConfig.Active;
    }

    [HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayCount))]
    [HarmonyPostfix]
    private static void PlayCountPostfix(CardModel card, int playCount, int __result)
    {
        if (__result != playCount)
        {
            MpHarness.Log($"play count for {card.TitleLocString.GetFormattedText()} raised {playCount} -> {__result}");
        }
    }

    // Hook.BeforeCardPlayed is raised once per index of the series, which is the only place an echoed play is
    // distinguishable from an ordinary one.
    [HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCardPlayed))]
    [HarmonyPrefix]
    private static void BeforeCardPlayedPrefix(CardPlay cardPlay)
    {
        if (cardPlay.PlayCount > 1)
        {
            MpHarness.Log(
                $"echoed play of {cardPlay.Card.TitleLocString.GetFormattedText()}: index {cardPlay.PlayIndex} of {cardPlay.PlayCount}");
        }
    }
}
#endif

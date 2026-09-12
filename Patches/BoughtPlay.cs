using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace RdpsMeter.Patches;

/// <summary>
/// Who bought a card play, and the name to file their credit under: fractions summing to 1, so the same answer serves
/// the damage the play dealt and the block it granted.
/// </summary>
internal readonly record struct Buyers(string Effect, IReadOnlyList<(ulong NetId, decimal Fraction)> Shares);

/// <summary>
/// A play a teammate paid for, from whichever of the two mechanics paid for it.
///
/// The counterfactual engine can only credit a model the game's modifier list mentions, and a play that would not have
/// happened at all is nowhere in that list - there is no modifier to remove. Two things buy one: a Tag Team mark, which
/// raises an ally's play count (<see cref="TagTeamCredit"/>), and a card handed to an ally that they did not own
/// (<see cref="GiftedCardCredit"/>). Both answer in the same <see cref="Buyers"/> shape, so one redirect serves both
/// and the block side needs one call rather than two.
///
/// Tag Team is asked first. A gifted card doubled by a mark was bought twice over - without the gift there is no play,
/// without the mark there is no second one - and the overlap has no principled split, so the extra play goes to the
/// mark and the base plays to the giver. That is the same arbitrary-but-harmless call the Echo Form case already makes,
/// and it cannot double-count: each play is credited once.
/// </summary>
internal static class BoughtPlay
{
    public static Buyers? BuyersOf(CardPlay? cardPlay)
    {
        return TagTeamCredit.BuyersOf(cardPlay) ?? GiftedCardCredit.BuyersOf(cardPlay);
    }

    /// <summary>
    /// The attribution again, with the dealer's own share moved to whoever bought this play. Returns it untouched
    /// unless a teammate bought the play.
    /// </summary>
    public static HitAttribution Redirect(HitAttribution attribution, CardPlay? cardPlay)
    {
        if (attribution.DealerPreBlock <= 0m
            || attribution.DealerNetId is not ulong dealer
            || BuyersOf(cardPlay) is not { } bought)
        {
            return attribution;
        }

        // Neither mechanic can buy a play for the player who paid - Tag Team returns early on its own applier's card,
        // and a gift to yourself is never recorded - so the dealer is never among the buyers. Skipping them anyway
        // keeps that true of any future caller rather than by luck.
        var externals = attribution.Externals.ToList();
        decimal moved = 0m;
        foreach ((ulong netId, decimal fraction) in bought.Shares)
        {
            if (netId == dealer)
            {
                continue;
            }

            decimal portion = attribution.DealerPreBlock * fraction;
            externals.Add(new ExternalContribution(netId, bought.Effect, portion));
            moved += portion;
        }

        if (moved <= 0m)
        {
            return attribution;
        }

        return new HitAttribution
        {
            Target = attribution.Target,
            Total = attribution.Total,
            DealerNetId = attribution.DealerNetId,
            DealerCard = attribution.DealerCard,
            DealerPreBlock = attribution.DealerPreBlock - moved,
            Externals = externals,
        };
    }
}

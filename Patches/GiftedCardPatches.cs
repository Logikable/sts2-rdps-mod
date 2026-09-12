using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter.Patches;

/// <summary>
/// Credits a player for the damage and block of a card they made for a teammate.
///
/// Blade Symphony puts two Shivs in every ally's hand; Largesse hands one ally a colourless card. The card is created
/// owned by the receiver, so when they play it the hit arrives as ordinary damage of theirs and the counterfactual
/// engine sees nothing to credit - the same blind spot Tag Team has, and for the same reason. Neither card is a
/// modifier: one raises a play count, the other creates a play out of nothing.
///
/// The rule is the engine's own: an effect is worth the damage it enabled. Without the gift that card does not exist,
/// so the whole of what it deals is the giver's, less whatever other teammates' buffs took of it. That is
/// <see cref="BoughtPlay.Redirect"/>, shared with Tag Team.
///
/// The line this stops at is a card the receiver already owned. Plot draws an ally cards and Tutor moves one of theirs
/// into hand; those are the ally's own cards, the counterfactual is unknowable - which card? - and neither is credited.
/// A created card has an exact one: this Shiv, four damage.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardGeneratedForCombat))]
internal static class GiftedCardPatches
{
    [HarmonyPrefix]
    private static void Prefix(CardModel card, Player? creator)
    {
        if (card.Owner is not { } receiver)
        {
            return;
        }

        if (GiftedCardCredit.Giver(receiver, creator) is not { } gift)
        {
            return;
        }

        GiftedCardCredit.Record(card, gift.NetId, gift.Effect);
    }
}

/// <summary>
/// Which card each player is in the middle of playing, so a card generated during a play can be traced back to the
/// card that generated it.
///
/// The game's own answer is <c>Hook.AfterCardGeneratedForCombat</c>'s <c>creator</c>, and for Largesse it is right.
/// Blade Symphony never passes one: <c>Shiv.CreateInHand</c> defaults <c>creator</c> to the *receiving* player, so the
/// hook names the person being handed the Shiv as the person who made it. There is nothing on the hook to recover the
/// giver from, and the card's own OnPlay is an async method whose state machine is not a patch target worth having, so
/// the play is tracked from the two hooks that bracket it instead.
///
/// An entry can outlive its play: OnPlayWrapper returns without reaching Hook.AfterCardPlayed when the owner dies or
/// the combat ends. A stale one is harmless by construction - it is overwritten by that player's next play, dropped at
/// combat end, and only ever read to name a card generated *for someone else*, which is a thing only a card play does.
/// </summary>
internal static class PlayInFlight
{
    private static readonly Dictionary<ulong, CardPlay> ByPlayer = new();
    private static readonly object Lock = new();

    public static void Begin(CardPlay cardPlay)
    {
        lock (Lock)
        {
            ByPlayer[cardPlay.Player.NetId] = cardPlay;
        }
    }

    public static void End(CardPlay cardPlay)
    {
        lock (Lock)
        {
            // Only the play that is actually standing, so a nested play finishing does not drop its parent's entry.
            if (ByPlayer.TryGetValue(cardPlay.Player.NetId, out CardPlay? current) && current == cardPlay)
            {
                ByPlayer.Remove(cardPlay.Player.NetId);
            }
        }
    }

    /// <summary>The card <paramref name="netId"/> is playing, or null when they are not playing one.</summary>
    public static CardModel? CardOf(ulong netId)
    {
        lock (Lock)
        {
            return ByPlayer.GetValueOrDefault(netId)?.Card;
        }
    }

    /// <summary>
    /// The card being played by the single player other than <paramref name="exclude"/> who is playing one, or null
    /// when nobody is or more than one is. Two players mid-play at once cannot be told apart by this route, so the
    /// honest answer is to credit neither.
    /// </summary>
    public static (ulong NetId, CardModel Card)? SoleOther(ulong exclude)
    {
        lock (Lock)
        {
            (ulong, CardModel)? only = null;
            foreach ((ulong netId, CardPlay play) in ByPlayer)
            {
                if (netId == exclude)
                {
                    continue;
                }

                if (only != null)
                {
                    return null;
                }

                only = (netId, play.Card);
            }

            return only;
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            ByPlayer.Clear();
        }
    }
}

/// <summary>
/// Tracks each play for <see cref="PlayInFlight"/>. Two classes rather than one, because a Harmony patch class targets
/// one method.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.BeforeCardPlayed))]
internal static class PlayInFlightBeginPatches
{
    [HarmonyPrefix]
    private static void Prefix(CardPlay cardPlay)
    {
        PlayInFlight.Begin(cardPlay);
    }
}

[HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardPlayed))]
internal static class PlayInFlightEndPatches
{
    [HarmonyPrefix]
    private static void Prefix(CardPlay cardPlay)
    {
        PlayInFlight.End(cardPlay);
    }
}

/// <summary>
/// Which card a player was handed, and by whom. Keyed weakly by the card itself, so the credit rides the card for as
/// long as it exists - a Shiv held over several turns is still the giver's when it is finally thrown - and goes when
/// the card does.
/// </summary>
internal static class GiftedCardCredit
{
    private sealed record Gift(ulong GiverNetId, string Effect);

    private static readonly ConditionalWeakTable<CardModel, Gift> Gifts = new();
    private static readonly object Lock = new();

    /// <summary>
    /// Who gave <paramref name="receiver"/> a card being generated right now and what to file it under, or null when
    /// this is not a gift. A card a player made for themselves is not one, which is most of them: Blade Dance, Up My
    /// Sleeve, every relic and power that fills your own hand.
    /// </summary>
    public static (ulong NetId, string Effect)? Giver(Player receiver, Player? creator)
    {
        ulong receiverNetId = receiver.NetId;

        // The game named a creator, and it is somebody else: take them, and name the row after whatever they are
        // playing. Without a card to name it after there is no row worth writing, so the credit is dropped rather than
        // filed under "(none)" - and that needs a generator which is not a card play, which no cross-player one is.
        if (creator != null && creator.NetId != receiverNetId)
        {
            return PlayInFlight.CardOf(creator.NetId) is { } creatorCard
                ? (creator.NetId, Name(creatorCard))
                : null;
        }

        // No usable creator - the Blade Symphony shape, where the hook names the receiver. The giver is then whoever
        // else is mid-play, and only when exactly one player is.
        return PlayInFlight.SoleOther(receiverNetId) is { } other ? (other.NetId, Name(other.Card)) : null;
    }

    public static void Record(CardModel card, ulong giverNetId, string effect)
    {
        lock (Lock)
        {
            Gifts.AddOrUpdate(card, new Gift(giverNetId, effect));
        }
    }

    /// <summary>
    /// Who bought this play, or null when the card was not a gift. Unlike a Tag Team grant this does not depend on the
    /// play index: every play of a card that would not exist was bought, not just the last of them.
    /// </summary>
    public static Buyers? BuyersOf(CardPlay? cardPlay)
    {
        if (cardPlay is not { } play)
        {
            return null;
        }

        Gift? gift;
        lock (Lock)
        {
            if (!Gifts.TryGetValue(play.Card, out gift))
            {
                return null;
            }
        }

        return new Buyers(gift.Effect, new[] { (gift.GiverNetId, 1m) });
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Gifts.Clear();
        }
    }

    /// <summary>
    /// The game's own name for the giving card, so the row reads "Blade Symphony" in the player's own language without
    /// the mod shipping the words - the route every card row already takes. The type name is the fallback rather than
    /// "(none)", since it is a real name and the only way to get here is a loc table that has lost the entry.
    /// </summary>
    private static string Name(CardModel card)
    {
        string title = card.TitleLocString.GetFormattedText();
        return string.IsNullOrWhiteSpace(title) ? card.GetType().Name : title;
    }
}

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter.Patches;

/// <summary>
/// Credits a Tag Team player for the extra card play they bought a teammate.
///
/// Tag Team marks an enemy; the next attack an *ally* plays at it plays twice. That second play is not a modified
/// hit but a whole extra one, so it reaches the meter as ordinary damage dealt by the ally and the counterfactual
/// engine never sees Tag Team at all - TagTeamPower changes a play count, not a damage number, so it is never in the
/// modifier list Hook.ModifyDamageInternal builds. Left alone, a card that exists only to hand a teammate a free
/// attack reads as worth nothing beyond its own 11.
///
/// The rule the engine uses everywhere else settles what it is worth: a buff is worth the damage it enabled. Without
/// Tag Team the extra play does not happen, so the whole of it is Tag Team's - not the whole hit, but whatever of it
/// remains the dealer's own after every other teammate's buff has taken its share. A teammate's Vulnerable on the
/// same hit is still worth what it was worth; the two credits do not overlap, and together with the dealer's
/// (now empty) share they still sum to the hit.
/// </summary>
[HarmonyPatch(typeof(TagTeamPower), nameof(TagTeamPower.ModifyCardPlayCount))]
internal static class TagTeamGrantPatches
{
    [HarmonyPostfix]
    private static void Postfix(TagTeamPower __instance, CardModel card, int playCount, int __result)
    {
        // The power returns the count unchanged for every play it does not double - a skill, the applier's own card,
        // a card aimed somewhere else. Only a real increase is a grant.
        if (__result <= playCount)
        {
            return;
        }

        IReadOnlyDictionary<ulong, decimal>? shares = AttributionEngine.OwnershipShares(__instance);
        if (shares == null || shares.Count == 0)
        {
            return;
        }

        TagTeamCredit.Record(card, __result - playCount, shares);
    }
}

/// <summary>
/// Drops any grant standing against a card at the start of the play loop that will decide its play count.
///
/// A grant belongs to exactly one play loop, and the loop can end without playing every play it planned - the combat
/// ends, the owner dies - so consuming it on the last play would leave one behind after every such loop. Clearing on
/// the way in instead needs nothing to go right: Hook.ModifyCardPlayCount runs once per play loop, for every card,
/// before any of that loop's plays exist, and TagTeamPower's own postfix records into the cleared slot from inside
/// this very call.
/// </summary>
[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyCardPlayCount))]
internal static class TagTeamPlayCountPatches
{
    [HarmonyPrefix]
    private static void Prefix(CardModel card)
    {
        TagTeamCredit.Forget(card);
    }
}

/// <summary>
/// Which of a card's plays a Tag Team bought, and for whom. Keyed weakly by the card being played, since a grant is
/// only ever read during that card's own play loop.
/// </summary>
internal static class TagTeamCredit
{
    private sealed class Grant
    {
        public int Extras { get; set; }

        /// <summary>Extra plays granted per player, so two teammates who each marked the enemy split the credit.</summary>
        public Dictionary<ulong, decimal> Weights { get; } = new();
    }

    private static readonly ConditionalWeakTable<CardModel, Grant> Grants = new();
    private static readonly object Lock = new();

    public static void Record(CardModel card, int extras, IReadOnlyDictionary<ulong, decimal> shares)
    {
        if (extras <= 0)
        {
            return;
        }

        lock (Lock)
        {
            Grant grant = Grants.GetOrCreateValue(card);
            grant.Extras += extras;
            foreach ((ulong netId, decimal fraction) in shares)
            {
                grant.Weights[netId] = grant.Weights.GetValueOrDefault(netId) + extras * fraction;
            }
        }
    }

    public static void Forget(CardModel card)
    {
        lock (Lock)
        {
            Grants.Remove(card);
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Grants.Clear();
        }
    }

    /// <summary>
    /// The attribution again, with the dealer's own share moved to whoever bought this play. Returns it untouched
    /// unless the hit belongs to a play a Tag Team added.
    ///
    /// Which of the plays those are is decided by index: the loop runs the base plays first, so the granted ones are
    /// the last <c>Extras</c> of them. When something else added plays too - Echo Form doubling the same card - the
    /// split between the two grantors is arbitrary, and harmlessly so: an echo is the dealer's own work and is
    /// credited to the dealer either way, so only the count of Tag Team plays matters, never which index they are.
    /// </summary>
    public static HitAttribution Redirect(HitAttribution attribution, CardPlay? cardPlay)
    {
        if (cardPlay is not { } play || attribution.DealerPreBlock <= 0m || attribution.DealerNetId is not ulong dealer)
        {
            return attribution;
        }

        Grant? grant;
        lock (Lock)
        {
            if (!Grants.TryGetValue(play.Card, out grant))
            {
                return attribution;
            }
        }

        if (play.PlayIndex < play.PlayCount - grant.Extras)
        {
            return attribution;
        }

        decimal totalWeight = grant.Weights.Values.Sum();
        if (totalWeight <= 0m)
        {
            return attribution;
        }

        // A Tag Team cannot double its own applier's card - the power returns early on that - so the dealer is never
        // among the grantors. Skipping them anyway keeps this true of any future caller rather than by luck.
        var externals = attribution.Externals.ToList();
        decimal moved = 0m;
        foreach ((ulong netId, decimal weight) in grant.Weights)
        {
            if (netId == dealer)
            {
                continue;
            }

            decimal portion = attribution.DealerPreBlock * weight / totalWeight;
            externals.Add(new ExternalContribution(netId, EffectName(), portion));
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

    /// <summary>
    /// What the row is called: the game's own name for the card, so it reads "Tag Team" without the mod shipping the
    /// words and reads whatever the player's language calls it everywhere else - the route card rows already take.
    /// The type name is the fallback rather than "(none)", since it is a real name and the only way to get here is a
    /// loc table that has lost the entry.
    /// </summary>
    private static string EffectName()
    {
        try
        {
            string title = ModelDb.Card<TagTeam>().TitleLocString.GetFormattedText();
            return string.IsNullOrWhiteSpace(title) ? nameof(TagTeam) : title;
        }
        catch (Exception)
        {
            return nameof(TagTeam);
        }
    }
}

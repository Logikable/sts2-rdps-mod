using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter.Patches;

/// <summary>
/// Records who paid for each point of a pet's hit points, so <see cref="PetAbsorption"/> can credit the mitigation to
/// them rather than to whoever happens to be standing behind the pet.
///
/// Every summon in the game funnels through this one command - 17 call sites across cards, relics, potions and powers -
/// and it carries both halves of the question in its arguments: <c>summoner</c> is who receives the pet, <c>source</c>
/// is what caused it. For almost all of them those are the same player. Two are not, and they are the reason this
/// exists: <b>Legion of Bone</b> loops every living ally summoning onto each in turn, and <b>Bone Brew</b> is a
/// targeted potion that can be thrown at a teammate.
///
/// The amount comes off the result rather than the argument, because Hook.ModifySummonAmount runs inside and can
/// change it - reading the parameter would book HP the pet never got.
///
/// The postfix wraps the returned Task instead of doing the work directly. Summon is async, so a plain postfix runs
/// when the Task is *handed back*, long before the pet has been created or grown, and <c>SummonResult.Creature</c>
/// would be the pet from a previous summon or nothing at all. This is the same rule the effect-stack patches follow
/// for the same reason.
/// </summary>
[HarmonyPatch(typeof(OstyCmd), nameof(OstyCmd.Summon))]
internal static class PetSummonPatch
{
    /// <summary>
    /// Whether the pet was alive going in, which is what decides between the command's two arms - and so between
    /// adding to the pool and replacing it. It has to be read here: by the time the postfix runs the pet has been
    /// revived and healed, so asking then always answers "alive".
    /// </summary>
    [HarmonyPrefix]
    private static void Prefix(Player summoner, out bool __state)
    {
        __state = summoner.IsOstyAlive;
    }

    [HarmonyPostfix]
    private static void Postfix(Player summoner, AbstractModel? source, bool __state, ref Task<SummonResult> __result)
    {
        __result = Record(__result, summoner, source, __state);
    }

    private static async Task<SummonResult> Record(
        Task<SummonResult> inner, Player summoner, AbstractModel? source, bool wasAlive)
    {
        SummonResult result = await inner;

        try
        {
            if (result.Creature is { } pet)
            {
                // A summon onto a living pet tops its max HP up (GainMaxHp); one onto a dead or absent pet sets it
                // outright (SetMaxHp), discarding whatever it had before. The pool has to follow, or the hit points a
                // revived Osty died with go on earning their buyer a share of every absorb the replacement soaks.
                // Gated on a summon that actually happened - Hook.ModifySummonAmount can zero one out, and that path
                // returns before touching the pet's max HP at all.
                //
                // Reading the game's own branch condition rather than reproducing the rule is deliberate: the two arms
                // are one `if` in OstyCmd.Summon, and a copy of it here would keep agreeing with today's build long
                // after the game stopped agreeing with the copy.
                if (!wasAlive && result.Amount > 0m)
                {
                    PetPool.Reset(pet);
                }

                PetPool.Contribute(pet, ContributorOf(summoner, source), result.Amount);
            }
        }
        catch (Exception ex)
        {
            // Bookkeeping is never worth failing a summon over; an unrecorded one falls back to crediting the owner.
            GD.PrintErr($"[RdpsMeter] Could not record who paid for a summon - it will be credited to the pet's owner: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// The player whose card, relic, potion or power this was, falling back to the summoner.
    ///
    /// Note that a potion answers correctly here, unlike in the block funnel: <c>PotionModel.Owner</c> is the thrower,
    /// and the reason <c>BlockSource</c> has to go through <see cref="PotionSource"/> instead is that block arrives at
    /// its funnel with the potion already out of scope. Here the game hands us the model itself, so there is nothing
    /// to reconstruct.
    /// </summary>
    private static ulong ContributorOf(Player summoner, AbstractModel? source)
    {
        Player? owner = source switch
        {
            CardModel card => card.Owner,
            PotionModel potion => potion.Owner,
            RelicModel relic => relic.Owner,
            PowerModel power => power.Owner?.Player,
            _ => null,
        };

        return owner?.NetId ?? summoner.NetId;
    }
}

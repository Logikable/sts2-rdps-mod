using MegaCrit.Sts2.Core.Entities.Creatures;

namespace RdpsMeter;

/// <summary>
/// Who paid for a pet's hit points. Summoning is the Necrobinder's Defend, but it is not always their *own* Defend:
/// Legion of Bone summons onto every ally at once and Bone Brew can be thrown at a teammate, so an Osty standing in
/// front of one player may be made largely of another player's cards. Without this the whole absorb was credited to
/// the pet's owner and the Necrobinder who bought it read as having mitigated nothing - the same shape as Beacon of
/// Hope handing block away, and wrong for the same reason.
///
/// The pool records contributed *max HP* per player and splits every absorb pro-rata by share. It deliberately does
/// not try to say which hit point was spent. Osty is a long-lived resource with sinks the meter has no business
/// modelling - it heals, it revives, it eats an enemy sweep as a target in its own right, it gets Sacrificed - and a
/// FIFO pool over that would need a reconciliation pass for each, every one of them a way to drift. Shares over
/// lifetime contributions have nothing to reconcile: they are computed fresh at each absorb from what has been put in
/// so far, so an absorb before a teammate's summon simply does not know about it, which is the right answer anyway.
///
/// This is why block does it the other way and both are right. Block is a turn-scoped pot spent to zero and refilled,
/// so "the wearer's own first, oldest first" is a distinction a player can see; the pool is reconciled against the
/// creature's real Block before every read precisely because that ordering has to stay honest. A pet's HP has no such
/// ordering to get right - nobody thinks of Osty's sixth hit point as older than its seventh.
/// </summary>
internal static class PetPool
{
    private static readonly Dictionary<Creature, Dictionary<ulong, decimal>> Contributed = new();
    private static readonly object Lock = new();

    /// <summary>
    /// Books max HP onto a pet under the player who paid for it. Called for every summon, including a player's own -
    /// the pro-rata split needs the whole picture, not just the foreign part of it.
    /// </summary>
    public static void Contribute(Creature pet, ulong contributorNetId, decimal maxHp)
    {
        if (maxHp <= 0m)
        {
            return;
        }

        lock (Lock)
        {
            if (!Contributed.TryGetValue(pet, out Dictionary<ulong, decimal>? byPlayer))
            {
                byPlayer = new Dictionary<ulong, decimal>();
                Contributed[pet] = byPlayer;
            }

            byPlayer[contributorNetId] = byPlayer.GetValueOrDefault(contributorNetId) + maxHp;
        }
    }

    /// <summary>
    /// Each contributor's fraction of the pet, summing to 1. Null when nothing was recorded - a mod installed
    /// mid-combat, or a summon route that never reached <see cref="Contribute"/> - which the caller reads as "credit
    /// the owner", the behaviour that shipped before this existed.
    /// </summary>
    public static IReadOnlyList<(ulong NetId, decimal Fraction)>? Shares(Creature pet)
    {
        lock (Lock)
        {
            if (!Contributed.TryGetValue(pet, out Dictionary<ulong, decimal>? byPlayer) || byPlayer.Count == 0)
            {
                return null;
            }

            decimal total = 0m;
            foreach (decimal amount in byPlayer.Values)
            {
                total += amount;
            }

            if (total <= 0m)
            {
                return null;
            }

            var shares = new List<(ulong, decimal)>(byPlayer.Count);
            foreach ((ulong netId, decimal amount) in byPlayer)
            {
                shares.Add((netId, amount / total));
            }

            return shares;
        }
    }

    /// <summary>
    /// Forgets what a pet was made of, for a summon that <em>replaces</em> its hit points instead of adding to them.
    ///
    /// OstyCmd.Summon has two arms and they differ in exactly this way: a living Osty is topped up with GainMaxHp,
    /// which adds, while one being created or revived gets SetMaxHp, which does not. A revived pet is therefore built
    /// wholly out of the summon that brought it back, and the cards that paid for the hit points it died with bought
    /// nothing that is still standing. Without this the dead pet's funding keeps drawing a share of every later absorb
    /// - so a teammate who summoned once early keeps taking credit for a pet that is now entirely somebody else's.
    /// </summary>
    public static void Reset(Creature pet)
    {
        lock (Lock)
        {
            Contributed.Remove(pet);
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Contributed.Clear();
        }
    }
}

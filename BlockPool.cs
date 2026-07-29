using MegaCrit.Sts2.Core.Entities.Creatures;

namespace RdpsMeter;

/// <summary>
/// The block a creature is standing behind, still itemized by who paid for it, so that when a hit finally lands the
/// meter can say whose block actually stopped it.
///
/// Block only counts once it is spent. Casting a shield nobody swings at achieved nothing, so nothing is booked at the
/// moment block is gained - the pool just remembers the gain - and the meter moves only when damage arrives and block
/// eats some of it. That is what makes overblock free: block still standing when the turn ends is dropped unbooked, so
/// the third Defend of a turn nobody hit you through scores zero rather than padding the total.
///
/// Which gain gets the credit for an absorbed hit is decided in two passes:
///
///  - The wearer's own block goes first, oldest gain first. Alone that is plain FIFO across the turn's cards, which is
///    what makes the *later* excess the ignored part rather than an arbitrary slice of everything.
///  - Only what the wearer could not cover themselves reaches the teammates who topped them up, and that is split
///    pro-rata between them rather than by order: once you are into somebody else's contribution, no teammate's block
///    was "first" in any sense the player would recognise.
///
/// The pool is reconciled against the creature's real Block before every read, so the block the game removes without
/// telling us - the turn's own expiry, an enemy stripping it - simply leaves the pool the size the creature is, with no
/// need to know every path that can take block away.
/// </summary>
internal static class BlockPool
{
    // One gain, as it was attributed when it landed. Amounts shrink in place as the block is spent.
    private sealed class Chunk
    {
        public required List<BlockStrand> Strands { get; init; }

        public decimal Total => Strands.Sum(s => s.Amount);
    }

    private static readonly Dictionary<Creature, List<Chunk>> ByCreature = new();
    private static readonly object Lock = new();

    /// <summary>Files a settled block gain, after squaring the pool with whatever block the creature actually has.</summary>
    public static void Gained(BlockGrant grant, int blockBefore)
    {
        if (grant.Strands.Count == 0)
        {
            return;
        }

        lock (Lock)
        {
            List<Chunk> chunks = Reconcile(grant.Receiver, blockBefore);
            chunks.Add(new Chunk { Strands = grant.Strands.ToList() });
        }
    }

    /// <summary>
    /// Spends <paramref name="absorbed"/> of a creature's block on a hit and returns who it belonged to, biggest share
    /// first. <paramref name="blockBefore"/> is the block standing when the hit arrived, since the game has already
    /// taken it down by the time this is asked.
    /// </summary>
    public static IReadOnlyList<BlockStrand> Spent(Creature creature, decimal absorbed, int blockBefore)
    {
        if (absorbed <= 0m)
        {
            return Array.Empty<BlockStrand>();
        }

        lock (Lock)
        {
            List<Chunk> chunks = Reconcile(creature, blockBefore);
            ulong wearer = creature.Player?.NetId ?? 0uL;
            var spent = new Dictionary<(ulong NetId, string Source), decimal>();

            // The wearer's own block, oldest gain first.
            decimal remaining = Take(chunks, spent, absorbed, s => s.OwnerNetId == wearer);

            // Then everyone who topped them up, pro-rata by what each still has in the pool.
            if (remaining > 0m)
            {
                decimal others = chunks.Sum(c => c.Strands.Where(s => s.OwnerNetId != wearer).Sum(s => s.Amount));
                if (others > 0m)
                {
                    decimal share = Math.Min(1m, remaining / others);
                    foreach (Chunk chunk in chunks)
                    {
                        for (int i = 0; i < chunk.Strands.Count; i++)
                        {
                            BlockStrand strand = chunk.Strands[i];
                            if (strand.OwnerNetId == wearer)
                            {
                                continue;
                            }

                            decimal portion = strand.Amount * share;
                            var key = (strand.OwnerNetId, strand.Source);
                            spent[key] = spent.GetValueOrDefault(key) + portion;
                            chunk.Strands[i] = strand with { Amount = strand.Amount - portion };
                        }
                    }
                }
            }

            Compact(chunks);
            if (chunks.Count == 0)
            {
                ByCreature.Remove(creature);
            }

            return spent
                .Select(kv => new BlockStrand(kv.Key.NetId, kv.Key.Source, kv.Value))
                .Where(s => s.Amount > 0m)
                .OrderByDescending(s => s.Amount)
                .ToList();
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            ByCreature.Clear();
        }
    }

    // Draws down the matching strands in pool order, returning what could not be covered. Callers hold Lock.
    private static decimal Take(
        List<Chunk> chunks,
        Dictionary<(ulong NetId, string Source), decimal> spent,
        decimal wanted,
        Func<BlockStrand, bool> matches)
    {
        foreach (Chunk chunk in chunks)
        {
            for (int i = 0; i < chunk.Strands.Count && wanted > 0m; i++)
            {
                BlockStrand strand = chunk.Strands[i];
                if (!matches(strand))
                {
                    continue;
                }

                decimal portion = Math.Min(strand.Amount, wanted);
                var key = (strand.OwnerNetId, strand.Source);
                spent[key] = spent.GetValueOrDefault(key) + portion;
                chunk.Strands[i] = strand with { Amount = strand.Amount - portion };
                wanted -= portion;
            }

            if (wanted <= 0m)
            {
                break;
            }
        }

        return wanted;
    }

    /// <summary>
    /// Brings the pool back in line with the block the creature really has. Anything the pool cannot account for is
    /// filed as the wearer's own unnamed block, so a source the meter does not follow shows up as an unlabelled row
    /// rather than silently vanishing from the total; anything it over-accounts for is trimmed oldest-first, matching
    /// the order block is spent in. Callers hold Lock.
    /// </summary>
    private static List<Chunk> Reconcile(Creature creature, int block)
    {
        if (!ByCreature.TryGetValue(creature, out List<Chunk>? chunks))
        {
            chunks = new List<Chunk>();
            ByCreature[creature] = chunks;
        }

        decimal tracked = chunks.Sum(c => c.Total);
        if (block <= 0)
        {
            chunks.Clear();
            return chunks;
        }

        if (tracked > block)
        {
            var spent = new Dictionary<(ulong NetId, string Source), decimal>();
            Take(chunks, spent, tracked - block, _ => true);
            Compact(chunks);
            return chunks;
        }

        if (tracked < block)
        {
            chunks.Add(new Chunk
            {
                Strands = new List<BlockStrand>
                {
                    new(creature.Player?.NetId ?? 0uL, AttributionEngine.UnknownSource, block - tracked),
                },
            });
        }

        return chunks;
    }

    // Drops strands that have been spent down to nothing, and the chunks left holding none. The tolerance is there
    // because the pro-rata pass divides: a strand meant to be exhausted can land a hair above zero. Callers hold Lock.
    private static void Compact(List<Chunk> chunks)
    {
        foreach (Chunk chunk in chunks)
        {
            chunk.Strands.RemoveAll(s => s.Amount <= 0.0001m);
        }

        chunks.RemoveAll(c => c.Strands.Count == 0);
    }
}

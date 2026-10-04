using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter;

/// <summary>
/// What players did to an enemy's hit before it landed, booked on the Blocked meter alongside block: damage it never
/// dealt because a player had weakened it, and - as a negative - damage it dealt extra because a player had made it
/// stronger.
///
/// Three things a player can do to the attacker, all credited by the counterfactual the damage side uses:
///
///  - <b>Debuffs on the attacker</b> that the game's modifier list names - Weak is the case - are removed one at a time
///    and the hit re-run.
///  - <b>Strength a player took away</b> is not a modifier of its own (see <see cref="EnemyStrength"/>), so it is put
///    back instead: the hit is re-run with that Strength added in the additive stage, which is the only place
///    Strength ever acts.
///  - <b>Strength a player gave it</b> (Fight Me!, Philosopher's Stone, Brimstone) is taken back out the same way, and
///    the difference is booked as a cost.
///
/// The cost is measured first, with every debuff in place, because it should be the damage the gift actually added: a
/// Weak enemy given 1 Strength hits 0.75 harder, not 1. The debuffs are then measured from that baseline - the hit as
/// it would have been without the gifts - so the two always sum to the whole: credit minus cost is exactly what the
/// hit would have done with no player touching the attacker, minus what it did. Measuring each against the other's
/// world instead would count the overlap between Weak and a gift twice.
///
/// Within the debuffs, the parts are scaled so they sum to the whole, which is what shares out the interaction between
/// them: Weak multiplies after Strength is added, so each makes the other worth less, and neither can claim the overlap
/// alone.
///
/// Counted <b>before block</b>, which is the game's own order - damage is modified, then block takes what it can, then
/// HP takes the rest. So a hit the debuffs shrank spends less block, and the block they saved stands unspent and
/// unbooked like any other overblock. Total mitigation of a hit is therefore always what it would have cost minus the
/// HP it did cost, however that work was split between block and debuffs.
///
/// Totals are taken in whole points - the game truncates the damage it actually deals - so a debuff that only moves a
/// hit from 4.9 to 4.5 is worth nothing, and the decimal shares are scaled to the whole points they made a difference
/// of.
/// </summary>
internal static class DebuffMitigation
{
    /// <summary>
    /// What players' work on <paramref name="dealer"/> changed of this hit, as strands owned by the players who did it
    /// and named by what did it - positive for damage prevented, negative for damage added; null when it changed
    /// nothing.
    /// </summary>
    public static IReadOnlyList<BlockStrand>? Attribute(
        decimal baseAmount,
        ValueProp props,
        Creature target,
        Creature dealer,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType flags,
        IReadOnlyList<AbstractModel> modifiers,
        decimal finalResult)
    {
        decimal Replay(ISet<AbstractModel> exclude, decimal strength)
        {
            return AttributionEngine.Recompute(
                baseAmount, props, target, dealer, cardSource, cardPlay, flags, modifiers, exclude, strength);
        }

        var none = new HashSet<AbstractModel>();
        var byKey = new Dictionary<(ulong NetId, string Source), decimal>();

        // Strength only feeds a powered attack, so neither a loss nor a gain is worth anything to any other kind of hit.
        bool powered = props.IsPoweredAttack();
        IReadOnlyList<(ulong NetId, string Source, decimal Amount)> lost = powered
            ? EnemyStrength.LostOf(dealer)
            : Array.Empty<(ulong, string, decimal)>();
        IReadOnlyList<(ulong NetId, string Source, decimal Amount)> gained = powered
            ? EnemyStrength.GainedOf(dealer)
            : Array.Empty<(ulong, string, decimal)>();
        decimal lostTotal = lost.Sum(l => l.Amount);
        decimal gainedTotal = gained.Sum(g => g.Amount);

        // The hit without players' gifts, debuffs and all still in place. Its gap to the real hit is what the gifts
        // cost, and everything the debuffs did is measured from here.
        decimal baseline = gainedTotal > 0m ? Replay(none, -gainedTotal) : finalResult;
        decimal cost = Math.Truncate(finalResult) - Math.Truncate(baseline);
        if (cost > 0m)
        {
            foreach ((ulong netId, string source, decimal amount) in gained)
            {
                var key = (netId, source);
                byKey[key] = byKey.GetValueOrDefault(key) - cost * (amount / gainedTotal);
            }
        }

        // Players' debuffs on the attacker, each kept only if removing it alone would raise the hit: a player-applied
        // power that *helps* the enemy is no mitigation, and excluding it from the combined counterfactual would eat
        // into what the real debuffs are credited. Strength is left to the passes around this one - its player shares
        // are the gifts, and its losses are not in the list at all.
        var debuffs = new List<(AbstractModel Mod, IReadOnlyList<(ulong NetId, string Effect, decimal Fraction)> Shares, decimal Raw)>();
        foreach (AbstractModel modifier in modifiers)
        {
            if (modifier is not PowerModel power
                || power is StrengthPower
                || power.Owner != dealer
                || AttributionEngine.NamedShares(power) is not { } shares)
            {
                continue;
            }

            decimal raw = Replay(new HashSet<AbstractModel> { modifier }, -gainedTotal) - baseline;
            if (raw > 0m)
            {
                debuffs.Add((modifier, shares, raw));
            }
        }

        decimal lostRaw = lostTotal > 0m ? Replay(none, lostTotal - gainedTotal) - baseline : 0m;
        decimal sumRaw = debuffs.Sum(d => d.Raw) + Math.Max(0m, lostRaw);
        if (sumRaw > 0m)
        {
            var excluded = new HashSet<AbstractModel>(debuffs.Select(d => d.Mod));
            decimal without = Replay(excluded, (lostRaw > 0m ? lostTotal : 0m) - gainedTotal);
            decimal prevented = Math.Truncate(without) - Math.Truncate(baseline);
            if (prevented > 0m)
            {
                decimal factor = prevented / sumRaw;
                foreach ((AbstractModel _, IReadOnlyList<(ulong NetId, string Effect, decimal Fraction)> shares, decimal raw) in debuffs)
                {
                    foreach ((ulong netId, string effect, decimal fraction) in shares)
                    {
                        var key = (netId, effect);
                        byKey[key] = byKey.GetValueOrDefault(key) + raw * factor * fraction;
                    }
                }

                // Strength is additive, so its losses stack linearly and split by how much each one took.
                if (lostRaw > 0m)
                {
                    foreach ((ulong netId, string source, decimal amount) in lost)
                    {
                        var key = (netId, source);
                        byKey[key] = byKey.GetValueOrDefault(key) + lostRaw * factor * (amount / lostTotal);
                    }
                }
            }
        }

        List<BlockStrand> strands = byKey
            .Where(kv => kv.Value != 0m)
            .Select(kv => new BlockStrand(kv.Key.NetId, kv.Key.Source, kv.Value))
            .ToList();
        return strands.Count > 0 ? strands : null;
    }
}

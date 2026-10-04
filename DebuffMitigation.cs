using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter;

/// <summary>
/// Damage an enemy never dealt because a player had weakened it, booked on the Blocked meter alongside block.
///
/// Two kinds of weakening, credited by the same counterfactual the damage side uses:
///
///  - <b>Debuffs on the attacker</b> that the game's modifier list names - Weak is the case - are removed one at a time
///    and the hit re-run.
///  - <b>Strength a player took away</b> is not a modifier of its own (see <see cref="StrengthLoss"/>), so it is put
///    back instead: the hit is re-run with that Strength added in the additive stage, which is the only place
///    Strength ever acts.
///
/// The parts are scaled so they sum to the whole, which is what shares out the interaction between them: Weak
/// multiplies after Strength is added, so each makes the other worth less, and neither can claim the overlap alone.
///
/// Counted <b>before block</b>, which is the game's own order - damage is modified, then block takes what it can, then
/// HP takes the rest. So a hit the debuffs shrank spends less block, and the block they saved stands unspent and
/// unbooked like any other overblock. Total mitigation of a hit is therefore always what it would have cost minus the
/// HP it did cost, however that work was split between block and debuffs.
///
/// The total is taken in whole points - the game truncates the damage it actually deals - so a debuff that only moves
/// a hit from 4.9 to 4.5 is worth nothing, and the decimal shares are scaled to the whole points they prevented.
/// </summary>
internal static class DebuffMitigation
{
    /// <summary>
    /// What players' debuffs on <paramref name="dealer"/> prevented of this hit, as strands owned by the players who
    /// applied them and named by what applied them; null when they prevented nothing.
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
        var none = new HashSet<AbstractModel>();

        // Players' debuffs on the attacker, each kept only if removing it alone would raise the hit: a player-applied
        // power that *helps* the enemy is no mitigation, and excluding it from the combined counterfactual would eat
        // into what the real debuffs are credited. Strength is left to the second pass - its player shares are the
        // ones that raised it (Fight Me!), and its losses are not in the list at all.
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

            decimal raw = AttributionEngine.Recompute(baseAmount, props, target, dealer, cardSource, cardPlay, flags, modifiers,
                new HashSet<AbstractModel> { modifier }, 0m) - finalResult;
            if (raw > 0m)
            {
                debuffs.Add((modifier, shares, raw));
            }
        }

        // Strength only feeds a powered attack, so a loss is worth nothing to any other kind of hit.
        IReadOnlyList<(ulong NetId, string Source, decimal Amount)> lost = props.IsPoweredAttack()
            ? StrengthLoss.Of(dealer)
            : Array.Empty<(ulong, string, decimal)>();
        decimal lostTotal = lost.Sum(l => l.Amount);
        decimal lostRaw = lostTotal > 0m
            ? AttributionEngine.Recompute(baseAmount, props, target, dealer, cardSource, cardPlay, flags, modifiers, none, lostTotal) - finalResult
            : 0m;

        decimal sumRaw = debuffs.Sum(d => d.Raw) + Math.Max(0m, lostRaw);
        if (sumRaw <= 0m)
        {
            return null;
        }

        var excluded = new HashSet<AbstractModel>(debuffs.Select(d => d.Mod));
        decimal without = AttributionEngine.Recompute(baseAmount, props, target, dealer, cardSource, cardPlay, flags, modifiers,
            excluded, lostRaw > 0m ? lostTotal : 0m);
        decimal prevented = Math.Truncate(without) - Math.Truncate(finalResult);
        if (prevented <= 0m)
        {
            return null;
        }

        decimal factor = prevented / sumRaw;
        var byKey = new Dictionary<(ulong NetId, string Source), decimal>();
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

        return byKey
            .Where(kv => kv.Value > 0m)
            .Select(kv => new BlockStrand(kv.Key.NetId, kv.Key.Source, kv.Value))
            .ToList();
    }
}

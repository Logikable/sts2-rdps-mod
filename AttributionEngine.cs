using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter;

/// <summary>
/// The attributed decomposition of a single hit's pre-block damage: how much of the final number belongs to the
/// dealer versus to teammates whose buffs/debuffs boosted it. Amounts are in the pre-block damage space (the value
/// Hook.ModifyDamage returned); the ledger rescales them onto the settled unblocked HP loss so block reduces every
/// share proportionally. All shares sum to <see cref="Total"/>.
/// </summary>
internal sealed class HitAttribution
{
    public required Creature? Target { get; init; }
    public required decimal Total { get; init; }
    public required ulong? DealerNetId { get; init; }
    public required decimal DealerPreBlock { get; init; }
    public required IReadOnlyDictionary<ulong, decimal> ExternalPreBlock { get; init; }

    public bool HasDealer => DealerNetId.HasValue;
}

/// <summary>
/// Counterfactual rDPS attribution. When a hit resolves, every modifier that changed its damage and was applied by a
/// *different* player is a candidate for credit. For each such modifier we recompute the damage as if that one
/// modifier were absent; the shortfall is the damage it was responsible for. Because multiplicative modifiers stack
/// (removing either of two 1.5x debuffs individually understates neither's true share), the raw per-modifier gains
/// can sum to more than the total gain from all external modifiers together, so we scale them proportionally to
/// conserve the total. The dealer keeps whatever remains - exactly the damage they would have dealt with no
/// teammate help.
///
/// The recomputation mirrors Hook.ModifyDamage's pipeline (enchantment, then additive, then multiplicative, then
/// cap) restricted to the participating modifier list. Non-participating listeners are identity operations, so the
/// restricted pipeline reproduces the game's result exactly - see <see cref="Recompute"/>, whose no-exclusion output
/// equals the final damage and is asserted at the call site during validation.
/// </summary>
internal static class AttributionEngine
{
    public static HitAttribution Attribute(
        decimal baseAmount,
        ValueProp props,
        Creature? target,
        Creature? dealer,
        CardModel? cardSource,
        ModifyDamageHookType flags,
        IReadOnlyList<AbstractModel> modifiers,
        decimal finalResult)
    {
        ulong? dealerNetId = dealer?.Player?.NetId ?? cardSource?.Owner?.NetId;

        var externals = new List<(AbstractModel Mod, ulong Applier)>();
        foreach (AbstractModel modifier in modifiers)
        {
            if (modifier is PowerModel power)
            {
                ulong? applier = power.Applier?.Player?.NetId;
                if (applier.HasValue && applier != dealerNetId)
                {
                    externals.Add((modifier, applier.Value));
                }
            }
        }

        if (externals.Count == 0)
        {
            return new HitAttribution
            {
                Target = target,
                Total = finalResult,
                DealerNetId = dealerNetId,
                DealerPreBlock = finalResult,
                ExternalPreBlock = EmptyShares,
            };
        }

        decimal total = finalResult;
        var externalSet = new HashSet<AbstractModel>(externals.Select(e => e.Mod));
        decimal withoutAllExternals =
            Recompute(baseAmount, props, target, dealer, cardSource, flags, modifiers, externalSet);
        decimal totalExternalGain = total - withoutAllExternals;

        // Raw counterfactual gain per applier (an applier with two contributing powers accumulates both).
        var rawGains = new Dictionary<ulong, decimal>();
        decimal sumRawGains = 0m;
        foreach ((AbstractModel mod, ulong applier) in externals)
        {
            decimal without = Recompute(baseAmount, props, target, dealer, cardSource, flags, modifiers, Single(mod));
            decimal gain = total - without;
            rawGains[applier] = rawGains.GetValueOrDefault(applier) + gain;
            sumRawGains += gain;
        }

        // Normalize so the per-applier gains sum to the true total external gain, then hand the remainder to the
        // dealer. When gains are purely additive the factor is 1 and nothing changes; the scaling only bites when
        // overlapping multipliers inflate the raw sum.
        decimal factor = sumRawGains != 0m ? totalExternalGain / sumRawGains : 0m;
        var attributed = new Dictionary<ulong, decimal>(rawGains.Count);
        decimal attributedSum = 0m;
        foreach ((ulong applier, decimal gain) in rawGains)
        {
            decimal share = gain * factor;
            attributed[applier] = share;
            attributedSum += share;
        }

        return new HitAttribution
        {
            Target = target,
            Total = total,
            DealerNetId = dealerNetId,
            DealerPreBlock = total - attributedSum,
            ExternalPreBlock = attributed,
        };
    }

    /// <summary>
    /// Replays the damage pipeline over the participating modifiers, skipping any in <paramref name="exclude"/>.
    /// With an empty exclusion set the result equals Hook.ModifyDamage's return value.
    /// </summary>
    public static decimal Recompute(
        decimal baseAmount,
        ValueProp props,
        Creature? target,
        Creature? dealer,
        CardModel? cardSource,
        ModifyDamageHookType flags,
        IReadOnlyList<AbstractModel> modifiers,
        ISet<AbstractModel> exclude)
    {
        decimal num = baseAmount;

        // Enchantment is the card owner's own effect, applied outside the listener loop; it is never attributable to
        // another player, so it stays folded into the dealer's baseline and is present in every counterfactual.
        if (cardSource?.Enchantment != null)
        {
            if (flags.HasFlag(ModifyDamageHookType.Additive))
            {
                num += cardSource.Enchantment.EnchantDamageAdditive(num, props);
            }

            if (flags.HasFlag(ModifyDamageHookType.Multiplicative))
            {
                num *= cardSource.Enchantment.EnchantDamageMultiplicative(num, props);
            }
        }

        if (flags.HasFlag(ModifyDamageHookType.Additive))
        {
            foreach (AbstractModel modifier in modifiers)
            {
                if (!exclude.Contains(modifier))
                {
                    num += modifier.ModifyDamageAdditive(target, num, props, dealer, cardSource);
                }
            }
        }

        if (flags.HasFlag(ModifyDamageHookType.Multiplicative))
        {
            foreach (AbstractModel modifier in modifiers)
            {
                if (!exclude.Contains(modifier))
                {
                    num *= modifier.ModifyDamageMultiplicative(target, num, props, dealer, cardSource);
                }
            }
        }

        if (flags.HasFlag(ModifyDamageHookType.Cap))
        {
            decimal cap = decimal.MaxValue;
            foreach (AbstractModel modifier in modifiers)
            {
                if (exclude.Contains(modifier))
                {
                    continue;
                }

                decimal candidate = modifier.ModifyDamageCap(target, props, dealer, cardSource);
                if (candidate < cap)
                {
                    cap = candidate;
                    if (num > candidate)
                    {
                        num = candidate;
                    }
                }
            }
        }

        return Math.Max(0m, num);
    }

    private static readonly IReadOnlyDictionary<ulong, decimal> EmptyShares =
        new Dictionary<ulong, decimal>();

    private static HashSet<AbstractModel> Single(AbstractModel modifier)
    {
        return new HashSet<AbstractModel> { modifier };
    }
}

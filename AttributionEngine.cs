using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter;

/// <summary>
/// One teammate's contribution to a single hit: how much of the damage a given effect they applied was responsible
/// for, in pre-block space. Carries the effect name and applier so the meter can itemize "Vulnerable from clcy: 9"
/// the way an rDPS tooltip does.
/// </summary>
internal readonly record struct ExternalContribution(ulong ApplierNetId, string Effect, decimal PreBlock);

/// <summary>
/// The attributed decomposition of a single hit's pre-block damage: the dealer's own share plus each teammate's
/// buff/debuff contribution. Amounts are pre-block (the value Hook.ModifyDamage returned); the ledger rescales them
/// onto settled unblocked HP loss. Dealer share plus all contributions sum to <see cref="Total"/>.
/// </summary>
internal sealed class HitAttribution
{
    public required Creature? Target { get; init; }
    public required decimal Total { get; init; }
    public required ulong? DealerNetId { get; init; }
    public required string DealerCard { get; init; }
    public required decimal DealerPreBlock { get; init; }
    public required IReadOnlyList<ExternalContribution> Externals { get; init; }

    public bool HasDealer => DealerNetId.HasValue;
}

/// <summary>
/// Counterfactual rDPS attribution. When a hit resolves, every modifier that changed its damage and was applied by a
/// *different* player is a candidate for credit. For each such modifier we recompute the damage as if that one
/// modifier were absent; the shortfall is the damage it was responsible for. Because multiplicative modifiers stack,
/// the raw per-modifier gains can sum to more than the total gain from all external modifiers together, so we scale
/// them proportionally to conserve the total. The dealer keeps whatever remains - exactly the damage they would have
/// dealt with no teammate help.
///
/// The recomputation mirrors Hook.ModifyDamage's pipeline (enchantment, then additive, then multiplicative, then
/// cap) restricted to the participating modifier list. Non-participating listeners are identity operations, so the
/// restricted pipeline reproduces the game's result exactly - see <see cref="Recompute"/>, whose no-exclusion output
/// equals the final damage.
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
        string dealerCard = cardSource?.Id.ToString() ?? "(none)";

        // A modifier is a credit candidate if it is a power with at least one owner who is not the dealer. Ownership
        // comes from PowerOwnership (per-player stack contributions), falling back to the single Applier the game
        // records when the mod never saw the power applied.
        var externalPowers = new List<(AbstractModel Mod, string Effect, IReadOnlyDictionary<ulong, decimal> Shares)>();
        foreach (AbstractModel modifier in modifiers)
        {
            if (modifier is PowerModel power)
            {
                IReadOnlyDictionary<ulong, decimal>? shares = OwnershipShares(power);
                if (shares != null && shares.Keys.Any(netId => netId != dealerNetId))
                {
                    externalPowers.Add((modifier, EffectName(modifier), shares));
                }
            }
        }

        if (externalPowers.Count == 0)
        {
            return new HitAttribution
            {
                Target = target,
                Total = finalResult,
                DealerNetId = dealerNetId,
                DealerCard = dealerCard,
                DealerPreBlock = finalResult,
                Externals = Array.Empty<ExternalContribution>(),
            };
        }

        decimal total = finalResult;
        var externalSet = new HashSet<AbstractModel>(externalPowers.Select(e => e.Mod));
        decimal withoutAllExternals =
            Recompute(baseAmount, props, target, dealer, cardSource, flags, modifiers, externalSet);
        decimal combinedExternalGain = total - withoutAllExternals;

        // Normalize each power's raw counterfactual gain against the combined gain, so overlapping multipliers
        // conserve the total, then split each power's conserved contribution across its owners by share. A co-owner
        // who is the dealer keeps their fraction (it never leaves the dealer); only the other players' fractions
        // become credited contributions.
        var rawGainByPower = new Dictionary<AbstractModel, decimal>();
        decimal sumRawGains = 0m;
        foreach ((AbstractModel mod, string _, IReadOnlyDictionary<ulong, decimal> _) in externalPowers)
        {
            decimal gain = total - Recompute(baseAmount, props, target, dealer, cardSource, flags, modifiers, Single(mod));
            rawGainByPower[mod] = gain;
            sumRawGains += gain;
        }

        decimal factor = sumRawGains != 0m ? combinedExternalGain / sumRawGains : 0m;
        var creditByKey = new Dictionary<(ulong NetId, string Effect), decimal>();
        decimal attributedSum = 0m;
        foreach ((AbstractModel mod, string effect, IReadOnlyDictionary<ulong, decimal> shares) in externalPowers)
        {
            decimal conserved = rawGainByPower[mod] * factor;
            foreach ((ulong netId, decimal fraction) in shares)
            {
                if (netId == dealerNetId)
                {
                    continue;
                }

                var key = (netId, effect);
                decimal portion = conserved * fraction;
                creditByKey[key] = creditByKey.GetValueOrDefault(key) + portion;
                attributedSum += portion;
            }
        }

        var contributions = creditByKey
            .Select(kv => new ExternalContribution(kv.Key.NetId, kv.Key.Effect, kv.Value))
            .ToList();

        return new HitAttribution
        {
            Target = target,
            Total = total,
            DealerNetId = dealerNetId,
            DealerCard = dealerCard,
            DealerPreBlock = total - attributedSum,
            Externals = contributions,
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

    /// <summary>
    /// Per-player ownership shares (netId -> fraction summing to 1) for a power. Prefers the tracked per-player stack
    /// contributions; falls back to the single Applier the game records when the power was applied before the mod
    /// saw it. Null when no player is responsible (a monster-applied power, e.g. a self-buff).
    /// </summary>
    internal static IReadOnlyDictionary<ulong, decimal>? OwnershipShares(PowerModel power)
    {
        IReadOnlyDictionary<ulong, decimal>? tracked = PowerOwnership.Instance.Shares(power);
        if (tracked != null)
        {
            return tracked;
        }

        ulong? applier = power.Applier?.Player?.NetId;
        return applier.HasValue
            ? new Dictionary<ulong, decimal> { [applier.Value] = 1m }
            : null;
    }

    /// <summary>
    /// Display name for a power: the class name with the "Power" suffix dropped (VulnerablePower -> Vulnerable).
    /// </summary>
    private static string EffectName(AbstractModel modifier)
    {
        string name = modifier.GetType().Name;
        return name.EndsWith("Power", StringComparison.Ordinal) ? name[..^"Power".Length] : name;
    }

    private static HashSet<AbstractModel> Single(AbstractModel modifier)
    {
        return new HashSet<AbstractModel> { modifier };
    }
}

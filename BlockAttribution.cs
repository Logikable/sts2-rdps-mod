using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter;

/// <summary>
/// One thread of a block gain: how much of it a single source is responsible for, and which player that source belongs
/// to. A plain Defend is one strand; a Defend boosted by a teammate's Dexterity Potion is two, one per owner.
/// </summary>
internal readonly record struct BlockStrand(ulong OwnerNetId, string Source, decimal Amount);

/// <summary>One settled block gain, decomposed into the strands that paid for it. The strands sum to <see cref="Amount"/>.</summary>
internal sealed class BlockGrant
{
    public required Creature Receiver { get; init; }
    public required decimal Amount { get; init; }
    public required IReadOnlyList<BlockStrand> Strands { get; init; }
}

/// <summary>
/// Counterfactual attribution for block, the mirror of <see cref="AttributionEngine"/>. When a block gain settles, each
/// modifier that changed it is recomputed away and the shortfall is what that modifier was worth; overlapping modifiers
/// are scaled proportionally so the parts still sum to the whole, and whatever is left over belongs to the card, potion,
/// relic or power that granted the block in the first place.
///
/// It differs from the damage engine in one deliberate way: it splits out *every* player-owned modifier, not only the
/// ones a teammate applied. On the damage meter a player's own Strength is not worth a line of its own - it is simply
/// part of what they hit for - but on the Blocked meter the whole point is to see where your block came from, so your
/// own Dexterity is itemized under the potion or card that granted it exactly as a teammate's would be.
///
/// The recomputation mirrors Hook.ModifyBlock's pipeline (enchantment, then additive, then multiplicative) over the
/// participating modifiers, so with nothing excluded it reproduces the game's own number.
/// </summary>
internal static class BlockAttributionEngine
{
    public static BlockGrant Attribute(
        decimal baseAmount,
        ValueProp props,
        Creature target,
        CardModel? cardSource,
        CardPlay? cardPlay,
        IReadOnlyList<AbstractModel> modifiers,
        decimal finalResult)
    {
        // Who is credited with the grant itself. A card says so outright, and says whose it is - which matters in co-op,
        // where the block you are wearing may have been played by somebody else. Otherwise the potion, relic or power
        // captured on the way in (see BlockSource) names it, and anything still nameless falls to the player wearing it.
        BlockGranter granter = cardSource == null ? BlockSource.Take(target) : default;
        ulong baseOwner = cardSource?.Owner?.NetId
            ?? granter.OwnerNetId
            ?? target.Player?.NetId
            ?? 0uL;
        string baseName = cardSource?.TitleLocString.GetFormattedText()
            ?? granter.Name
            ?? AttributionEngine.UnknownSource;

        var owned = new List<(AbstractModel Mod, IReadOnlyList<(ulong NetId, string Effect, decimal Fraction)> Shares)>();
        foreach (AbstractModel modifier in modifiers)
        {
            if (modifier is PowerModel power && AttributionEngine.NamedShares(power) is { } shares)
            {
                owned.Add((modifier, shares));
            }
        }

        if (owned.Count == 0)
        {
            return Whole(target, finalResult, baseOwner, baseName);
        }

        var ownedSet = new HashSet<AbstractModel>(owned.Select(o => o.Mod));
        decimal withoutOwned = Recompute(baseAmount, props, target, cardSource, cardPlay, modifiers, ownedSet);
        decimal combinedGain = finalResult - withoutOwned;

        // Net-negative overall: the player-owned modifiers in play cost more block than they gave (Frail against a
        // little Dexterity). There is no positive contribution to hand out, and crediting a source with negative block
        // would read as nonsense, so the whole gain - already reduced - stays on what granted it.
        if (combinedGain <= 0m)
        {
            return Whole(target, finalResult, baseOwner, baseName);
        }

        var rawGain = new Dictionary<AbstractModel, decimal>();
        decimal sumRawGains = 0m;
        foreach ((AbstractModel mod, IReadOnlyList<(ulong, string, decimal)> _) in owned)
        {
            decimal gain = Math.Max(
                0m, finalResult - Recompute(baseAmount, props, target, cardSource, cardPlay, modifiers, Single(mod)));
            rawGain[mod] = gain;
            sumRawGains += gain;
        }

        decimal factor = sumRawGains != 0m ? combinedGain / sumRawGains : 0m;
        var byKey = new Dictionary<(ulong NetId, string Effect), decimal>();
        decimal attributed = 0m;
        foreach ((AbstractModel mod, IReadOnlyList<(ulong NetId, string Effect, decimal Fraction)> shares) in owned)
        {
            decimal conserved = rawGain[mod] * factor;
            foreach ((ulong netId, string effect, decimal fraction) in shares)
            {
                var key = (netId, effect);
                decimal portion = conserved * fraction;
                byKey[key] = byKey.GetValueOrDefault(key) + portion;
                attributed += portion;
            }
        }

        var strands = new List<BlockStrand>();
        decimal own = finalResult - attributed;
        if (own > 0m)
        {
            strands.Add(new BlockStrand(baseOwner, baseName, own));
        }

        foreach (((ulong netId, string effect), decimal amount) in byKey)
        {
            if (amount > 0m)
            {
                strands.Add(new BlockStrand(netId, effect, amount));
            }
        }

        return new BlockGrant { Receiver = target, Amount = finalResult, Strands = strands };
    }

    private static BlockGrant Whole(Creature target, decimal amount, ulong owner, string name)
    {
        return new BlockGrant
        {
            Receiver = target,
            Amount = amount,
            Strands = amount > 0m
                ? new[] { new BlockStrand(owner, name, amount) }
                : Array.Empty<BlockStrand>(),
        };
    }

    /// <summary>
    /// Replays Hook.ModifyBlock over the participating modifiers, skipping any in <paramref name="exclude"/>. With an
    /// empty exclusion set the result equals what the game returned.
    /// </summary>
    public static decimal Recompute(
        decimal baseAmount,
        ValueProp props,
        Creature target,
        CardModel? cardSource,
        CardPlay? cardPlay,
        IReadOnlyList<AbstractModel> modifiers,
        ISet<AbstractModel> exclude)
    {
        decimal num = baseAmount;

        // The card's own enchantment belongs to whoever played it and can never be somebody else's doing, so it stays
        // folded into the grant's baseline and is present in every counterfactual.
        if (cardSource?.Enchantment is { } enchantment)
        {
            num += enchantment.EnchantBlockAdditive(num);
            num *= enchantment.EnchantBlockMultiplicative(num);
        }

        foreach (AbstractModel modifier in modifiers)
        {
            if (!exclude.Contains(modifier))
            {
                num += Invoke(AdditiveListener, modifier, cardPlay, target, num, props, cardSource);
            }
        }

        foreach (AbstractModel modifier in modifiers)
        {
            if (!exclude.Contains(modifier))
            {
                num *= Invoke(MultiplicativeListener, modifier, cardPlay, target, num, props, cardSource);
            }
        }

        return Math.Max(0m, num);
    }

    // Resolved once and invoked reflectively, with the trailing CardPlay? appended only where this build's method
    // declares one - the same version tolerance the damage listeners are called with, and for the same reason.
    private readonly record struct Listener(MethodInfo Method, bool TakesCardPlay);

    private static readonly Listener AdditiveListener = Resolve("ModifyBlockAdditive", baseArity: 4);
    private static readonly Listener MultiplicativeListener = Resolve("ModifyBlockMultiplicative", baseArity: 4);

    private static Listener Resolve(string name, int baseArity)
    {
        MethodInfo method = AccessTools.Method(typeof(AbstractModel), name)
            ?? throw new MissingMethodException(nameof(AbstractModel), name);
        return new Listener(method, method.GetParameters().Length > baseArity);
    }

    private static decimal Invoke(Listener listener, AbstractModel modifier, CardPlay? cardPlay, params object?[] baseArgs)
    {
        object?[] args = listener.TakesCardPlay ? [.. baseArgs, cardPlay] : baseArgs;
        return (decimal)listener.Method.Invoke(modifier, args)!;
    }

    private static HashSet<AbstractModel> Single(AbstractModel modifier)
    {
        return new HashSet<AbstractModel> { modifier };
    }
}

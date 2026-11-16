using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
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
/// Part of the dealer's own share that one of their own percentage buffs bought - Lethality, Pen Nib, a modded relic
/// like Pressure Points - in pre-block space. Still the dealer's damage; it is only filed under the buff's name instead
/// of the card's, so "Strike 10, Lethality 5" rather than "Strike 15".
/// </summary>
internal readonly record struct OwnContribution(string Effect, decimal PreBlock);

/// <summary>
/// The attributed decomposition of a single hit's pre-block damage: the dealer's own share plus each teammate's
/// buff/debuff contribution. Amounts are pre-block (the value Hook.ModifyDamage returned), which is also what the
/// ledger books, since damage absorbed by block still counts. Dealer share plus all contributions sum to
/// <see cref="Total"/>.
/// </summary>
internal sealed class HitAttribution
{
    public required Creature? Target { get; init; }
    public required decimal Total { get; init; }
    public required ulong? DealerNetId { get; init; }
    public required string DealerCard { get; init; }
    public required decimal DealerPreBlock { get; init; }
    public required IReadOnlyList<ExternalContribution> Externals { get; init; }

    /// <summary>
    /// The slices of <see cref="DealerPreBlock"/> the dealer's own percentage buffs account for. A breakdown of the
    /// dealer's share, not an addition to it: the card's row is <see cref="DealerPreBlock"/> less these.
    /// </summary>
    public IReadOnlyList<OwnContribution> OwnEffects { get; init; } = Array.Empty<OwnContribution>();

    public bool HasDealer => DealerNetId.HasValue;

    /// <summary>
    /// The same split at a different size: every share scaled by <paramref name="total"/> / <see cref="Total"/>, on a
    /// new target. For a hit whose size is copied from another hit rather than built up from modifiers (see
    /// <c>SpillAttribution</c>), so the copy is divided the way the hit it copies was.
    /// </summary>
    public HitAttribution ScaledTo(Creature? target, decimal total)
    {
        decimal ratio = Total == 0m ? 0m : total / Total;
        return new HitAttribution
        {
            Target = target,
            Total = total,
            DealerNetId = DealerNetId,
            DealerCard = DealerCard,
            DealerPreBlock = DealerPreBlock * ratio,
            Externals = Externals.Select(e => e with { PreBlock = e.PreBlock * ratio }).ToList(),
            OwnEffects = OwnEffects.Select(e => e with { PreBlock = e.PreBlock * ratio }).ToList(),
        };
    }
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
///
/// One modifier is not one model's work. Vulnerable's multiplier folds in up to three other models before it returns,
/// none of which is a listener and so none of which the game's modifier list mentions; <see cref="VulnerableBoosts"/>
/// puts them back in the list and makes them excludable, so Debilitate is credited to the player who played it rather
/// than to whoever applied Vulnerable. Every other rule here applies to them unchanged - including the one that keeps
/// the dealer's own contributions with the dealer, which is what filters out a Cruelty cast on oneself.
/// </summary>
internal static class AttributionEngine
{
    /// <summary>
    /// What a hit is filed under when nothing identifies where it came from. Stored in the ledger as-is - the saved
    /// tally stays language-neutral - and translated on its way to the screen by <see cref="Loc.SourceName"/>.
    /// </summary>
    public const string UnknownSource = "(none)";

    /// <summary>
    /// The counterfactual decomposition of one hit, then one correction the counterfactual cannot make on its own: a
    /// play a teammate paid for is not a modified hit, so nothing in the modifier list mentions it. A Tag Team mark
    /// buys an extra play of a card the dealer owns; a gifted card buys every play of a card they would not have had
    /// at all. <see cref="Patches.BoughtPlay"/> moves the dealer's own share of such a play to whoever bought it,
    /// leaving every other teammate's share exactly where the engine put it.
    /// </summary>
    public static HitAttribution Attribute(
        decimal baseAmount,
        ValueProp props,
        Creature? target,
        Creature? dealer,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType flags,
        IReadOnlyList<AbstractModel> gameModifiers,
        decimal finalResult)
    {
        HitAttribution attribution = Decompose(
            baseAmount, props, target, dealer, cardSource, cardPlay, flags, gameModifiers, finalResult);
        return Patches.BoughtPlay.Redirect(attribution, cardPlay);
    }

    private static HitAttribution Decompose(
        decimal baseAmount,
        ValueProp props,
        Creature? target,
        Creature? dealer,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType flags,
        IReadOnlyList<AbstractModel> gameModifiers,
        decimal finalResult)
    {
        // Vulnerable arrives as a single modifier carrying up to four models' work; augment the list with the hidden
        // boosters so each can be excluded, and credited, on its own.
        IReadOnlyList<AbstractModel> modifiers = VulnerableBoosts.Augment(gameModifiers, target, dealer);

        ulong? dealerNetId = dealer?.Player?.NetId ?? cardSource?.Owner?.NetId;
        // The card's human-readable title, without the upgrade marker, so "Anger" and "Anger+" share one row (Title
        // appends a "+"/"+N"; TitleLocString is the bare name). A hit with no card is a potion or a damaging
        // power/relic (Thorns, a retaliation relic, ...); name it by whichever the player is resolving so its damage
        // reads "Fire Potion" or "Thorns" rather than the "(none)" a null card source would leave behind.
        string? cardName = cardSource?.TitleLocString.GetFormattedText();
        // The effect outranks the potion, because it is the inner one. A potion that draws a card (Cure All, Clarity,
        // Swift Potion, ...) runs Speedster's own hook inside its OnUse, so both windows are open at once and only the
        // innermost names the hit that Speedster actually dealt - the potion merely caused the draw.
        //
        // Safe in the other direction too: EffectSource is set - or cleared - in the prefix of this very hit, from
        // whatever power/relic/orb the game had on top of its model stack. A potion's own damage runs with the potion
        // itself on top, which is none of those, so the entry is cleared and the potion name still wins. And its other
        // supplier, ExecutingEffect, is pushed only around the end-of-turn power hooks, during which no potion can be
        // drunk.
        string? sourceName = dealerNetId is ulong id ? EffectSource.Current(id) ?? PotionSource.Current(id) : null;
        string dealerCard = cardName ?? sourceName ?? UnknownSource;

        // A modifier is a credit candidate if it is a power with at least one owner who is not the dealer. Ownership
        // comes from PowerOwnership (per-player stack contributions), falling back to the single Applier the game
        // records when the mod never saw the power applied. Each share carries the name to credit it under: the
        // granting card or potion where that was recorded, and the power's own name otherwise.
        var externalPowers = new List<(AbstractModel Mod, IReadOnlyList<(ulong NetId, string Effect, decimal Fraction)> Shares)>();
        foreach (AbstractModel modifier in modifiers)
        {
            if (modifier is PowerModel power)
            {
                IReadOnlyList<(ulong NetId, string Effect, decimal Fraction)>? shares = NamedShares(power);
                if (shares != null && shares.Any(s => s.NetId != dealerNetId))
                {
                    externalPowers.Add((modifier, shares));
                }
            }
        }

        decimal total = finalResult;
        var externalSet = new HashSet<AbstractModel>(externalPowers.Select(e => e.Mod));
        if (externalPowers.Count == 0)
        {
            return new HitAttribution
            {
                Target = target,
                Total = total,
                DealerNetId = dealerNetId,
                DealerCard = dealerCard,
                DealerPreBlock = total,
                Externals = Array.Empty<ExternalContribution>(),
                OwnEffects = OwnPercentages(
                    baseAmount, props, target, dealer, dealerNetId, cardSource, cardPlay, flags, modifiers,
                    externalSet, withoutExternals: total, dealerShare: total),
            };
        }

        decimal withoutAllExternals =
            Recompute(baseAmount, props, target, dealer, cardSource, cardPlay, flags, modifiers, externalSet);
        decimal combinedExternalGain = total - withoutAllExternals;

        // Normalize each power's raw counterfactual gain against the combined gain, so overlapping multipliers
        // conserve the total, then split each power's conserved contribution across its owners by share. A co-owner
        // who is the dealer keeps their fraction (it never leaves the dealer); only the other players' fractions
        // become credited contributions.
        var rawGainByPower = new Dictionary<AbstractModel, decimal>();
        decimal sumRawGains = 0m;
        foreach ((AbstractModel mod, IReadOnlyList<(ulong, string, decimal)> _) in externalPowers)
        {
            decimal gain = total - Recompute(baseAmount, props, target, dealer, cardSource, cardPlay, flags, modifiers, Single(mod));
            rawGainByPower[mod] = gain;
            sumRawGains += gain;
        }

        decimal factor = sumRawGains != 0m ? combinedExternalGain / sumRawGains : 0m;
        var creditByKey = new Dictionary<(ulong NetId, string Effect), decimal>();
        decimal attributedSum = 0m;
        foreach ((AbstractModel mod, IReadOnlyList<(ulong NetId, string Effect, decimal Fraction)> shares) in externalPowers)
        {
            decimal conserved = rawGainByPower[mod] * factor;
            foreach ((ulong netId, string effect, decimal fraction) in shares)
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
            OwnEffects = OwnPercentages(
                baseAmount, props, target, dealer, dealerNetId, cardSource, cardPlay, flags, modifiers,
                externalSet, withoutExternals: withoutAllExternals, dealerShare: total - attributedSum),
        };
    }

    /// <summary>
    /// Splits the dealer's own share between the card and the dealer's own percentage buffs. Credit does not move:
    /// every slice stays the dealer's, so aDPS and rDPS are unchanged and only the breakdown gains rows.
    ///
    /// The split is measured in the world without teammates' buffs, which is what the dealer's share is. Teammates'
    /// credit is computed first and left alone, so it stays what it always was. Measuring the dealer's buffs in the same
    /// pass as theirs would shrink it: Lethality and a teammate's Vulnerable on one hit overlap, and a shared
    /// normalization would make the teammate pay for half of the overlap. The engine's rule everywhere is that a
    /// teammate's buff is worth the damage it actually enabled, and that is larger when the dealer is amplifying it.
    ///
    /// Only *multiplicative* buffs are split out. Flat ones (Strength, Vigor) stay on the card. A buff qualifies by
    /// what its multiplier does on this hit, not by whether its class overrides ModifyDamageMultiplicative: Hades
    /// Ancients' Pressure Points never overrides it, and gets in through the mod's own postfix on the base method. That
    /// postfix is also why the replay reproduces it, since the listener is invoked virtually through the patched base.
    /// </summary>
    private static IReadOnlyList<OwnContribution> OwnPercentages(
        decimal baseAmount,
        ValueProp props,
        Creature? target,
        Creature? dealer,
        ulong? dealerNetId,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType flags,
        IReadOnlyList<AbstractModel> modifiers,
        ISet<AbstractModel> externalSet,
        decimal withoutExternals,
        decimal dealerShare)
    {
        if (dealerNetId == null || dealerShare <= 0m || withoutExternals <= 0m
            || !flags.HasFlag(ModifyDamageHookType.Multiplicative))
        {
            return Array.Empty<OwnContribution>();
        }

        var candidates = new List<(AbstractModel Mod, string Effect)>();
        foreach (AbstractModel modifier in modifiers)
        {
            if (externalSet.Contains(modifier) || OwnBuffName(modifier, dealer, dealerNetId.Value) is not { } effect)
            {
                continue;
            }

            decimal factor = Invoke(
                MultiplicativeListener, modifier, cardPlay, target, baseAmount, props, dealer, cardSource);
            if (factor > 1m)
            {
                candidates.Add((modifier, effect));
            }
        }

        if (candidates.Count == 0)
        {
            return Array.Empty<OwnContribution>();
        }

        var gains = new List<(string Effect, decimal Gain)>();
        decimal sumRawGains = 0m;
        foreach ((AbstractModel mod, string effect) in candidates)
        {
            var exclude = new HashSet<AbstractModel>(externalSet) { mod };
            decimal gain = withoutExternals
                - Recompute(baseAmount, props, target, dealer, cardSource, cardPlay, flags, modifiers, exclude);
            if (gain > 0m)
            {
                gains.Add((effect, gain));
                sumRawGains += gain;
            }
        }

        if (sumRawGains <= 0m)
        {
            return Array.Empty<OwnContribution>();
        }

        var allOwn = new HashSet<AbstractModel>(externalSet);
        allOwn.UnionWith(candidates.Select(c => c.Mod));
        decimal combined = withoutExternals
            - Recompute(baseAmount, props, target, dealer, cardSource, cardPlay, flags, modifiers, allOwn);
        if (combined <= 0m)
        {
            return Array.Empty<OwnContribution>();
        }

        // Normalized to the combined gain the way teammates' buffs are, then carried from the no-teammates world onto the
        // dealer's actual share - which differs from it only when the dealer co-owns a teammate's power.
        decimal scale = combined / sumRawGains * (dealerShare / withoutExternals);
        return gains
            .GroupBy(g => g.Effect)
            .Select(g => new OwnContribution(g.Key, Math.Min(g.Sum(x => x.Gain) * scale, dealerShare)))
            .ToList();
    }

    /// <summary>
    /// The row name for a modifier that is the dealer's own buff, or null if it is not one: a buff power worn by the
    /// dealer (or by their character, when a pet is dealing), or a relic the dealer holds. Debuffs on the dealer and
    /// anything worn by the target are left alone - a Vulnerable the dealer applied themselves stays on the card,
    /// exactly as it always has.
    /// </summary>
    private static string? OwnBuffName(AbstractModel modifier, Creature? dealer, ulong dealerNetId)
    {
        try
        {
            return modifier switch
            {
                PowerModel { Type: PowerType.Buff } power
                    when power.Owner == dealer || power.Owner?.Player?.NetId == dealerNetId
                    => power.Title.GetFormattedText(),
                RelicModel relic when relic.Owner?.NetId == dealerNetId => relic.Title.GetFormattedText(),
                _ => null,
            };
        }
        catch (Exception)
        {
            // A name is never worth breaking a hit over; the slice just stays on the card.
            return null;
        }
    }

    /// <summary>
    /// Replays the damage pipeline over the participating modifiers, skipping any in <paramref name="exclude"/>.
    /// With an empty exclusion set the result equals Hook.ModifyDamage's return value.
    ///
    /// <paramref name="strengthAdjustment"/> changes the dealer's Strength for the replay - positive to give back a loss,
    /// negative to take away a gain - for Strength that is not a modifier of its own (see <see cref="EnemyStrength"/>).
    /// It joins the additive stage, the one Strength acts in; the caller decides whether the hit is one Strength feeds
    /// at all.
    /// </summary>
    public static decimal Recompute(
        decimal baseAmount,
        ValueProp props,
        Creature? target,
        Creature? dealer,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType flags,
        IReadOnlyList<AbstractModel> modifiers,
        ISet<AbstractModel> exclude,
        decimal strengthAdjustment = 0m)
    {
        // A hidden Vulnerable booster cannot be skipped by leaving it out of the loop below - it is not a listener,
        // and the game reads it from inside VulnerablePower's own multiplier. Suppressing it for the span of the
        // replay is how an excluded one is removed; the finally is what stops that leaking onto real damage.
        VulnerableBoosts.Begin(exclude);
        try
        {
            return RecomputeInternal(
                baseAmount, props, target, dealer, cardSource, cardPlay, flags, modifiers, exclude, strengthAdjustment);
        }
        finally
        {
            VulnerableBoosts.End();
        }
    }

    private static decimal RecomputeInternal(
        decimal baseAmount,
        ValueProp props,
        Creature? target,
        Creature? dealer,
        CardModel? cardSource,
        CardPlay? cardPlay,
        ModifyDamageHookType flags,
        IReadOnlyList<AbstractModel> modifiers,
        ISet<AbstractModel> exclude,
        decimal strengthAdjustment)
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
            num += strengthAdjustment;
            foreach (AbstractModel modifier in modifiers)
            {
                if (!exclude.Contains(modifier))
                {
                    num += Invoke(AdditiveListener, modifier, cardPlay, target, num, props, dealer, cardSource);
                }
            }
        }

        if (flags.HasFlag(ModifyDamageHookType.Multiplicative))
        {
            foreach (AbstractModel modifier in modifiers)
            {
                if (!exclude.Contains(modifier))
                {
                    num *= Invoke(MultiplicativeListener, modifier, cardPlay, target, num, props, dealer, cardSource);
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

                decimal candidate = Invoke(CapListener, modifier, cardPlay, target, props, dealer, cardSource);
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

    // The game's per-modifier ModifyDamage listeners gained a trailing CardPlay? parameter in a later build. Each is
    // resolved once and invoked reflectively, appending cardPlay only when this build's method declares it, so the
    // recompute pipeline reproduces the game's damage on either version. Recompute runs only for hits a teammate's buff
    // or one of the dealer's own percentage buffs actually changed, so this reflection stays off the common damage path.
    private readonly record struct Listener(MethodInfo Method, bool TakesCardPlay);

    private static readonly Listener AdditiveListener = Resolve("ModifyDamageAdditive", baseArity: 5);
    private static readonly Listener MultiplicativeListener = Resolve("ModifyDamageMultiplicative", baseArity: 5);
    private static readonly Listener CapListener = Resolve("ModifyDamageCap", baseArity: 4);

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
    /// Ownership shares with the name to credit each one under. Most powers name themselves - a Vulnerable share is
    /// "Vulnerable" whichever card applied it, which is what the player wants to read. A pooled power is the
    /// exception: every source of Strength stacks into one instance, so "Strength" alone would merge a teammate's
    /// Coordinate, their Blaze and their thrown Flex Potion into a single unattributable row. Where the granting card
    /// or potion was recorded (see <see cref="Patches.PowerOwnershipPatches"/>), that name is used for the share
    /// instead, so each shows separately. Dexterity pools the same way and is named the same way, for the Blocked meter.
    /// </summary>
    internal static IReadOnlyList<(ulong NetId, string Effect, decimal Fraction)>? NamedShares(PowerModel power)
    {
        IReadOnlyList<(ulong NetId, string? Source, decimal Fraction)>? tracked =
            PowerOwnership.Instance.SourcedShares(power);
        if (tracked != null)
        {
            return tracked.Select(s => (s.NetId, s.Source ?? EffectName(power), s.Fraction)).ToList();
        }

        ulong? applier = power.Applier?.Player?.NetId;
        return applier.HasValue
            ? new[] { (applier.Value, EffectName(power), 1m) }
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

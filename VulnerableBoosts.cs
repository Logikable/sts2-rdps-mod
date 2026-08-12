using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter;

/// <summary>
/// Vulnerable's multiplier is not one model's work, and the meter used to pretend it was.
///
/// <c>VulnerablePower.ModifyDamageMultiplicative</c> starts from its own DamageIncrease (1.5) and then hands the
/// running value to up to three other models, in this fixed order: the dealer's <c>PaperPhrog</c> relic (+0.25), the
/// dealer's <c>CrueltyPower</c> (+Amount/100), and the target's <c>DebilitatePower</c>, which doubles whatever bonus
/// has accumulated so far (<c>amount + (amount - 1)</c>). None of the three is a hook listener, so none of them ever
/// reaches <c>Hook.ModifyDamage</c>'s modifier list - only VulnerablePower does, carrying all four contributions
/// folded into one number. The attribution engine can only exclude what it can see, so the whole stacked multiplier
/// was credited to whoever applied Vulnerable, and a Necrobinder who spent a card doubling it got nothing.
///
/// These are *hidden* modifiers: real contributors the counterfactual engine has no handle on. This class gives it
/// one. The two that a player other than the dealer can own - Debilitate and Cruelty - are appended to the modifier
/// list the engine reasons over, which makes them ordinary credit candidates; <see cref="Patches.DebilitateBoostPatch"/>
/// and <see cref="Patches.CrueltyBoostPatch"/> then let a counterfactual neutralize one in place.
///
/// The neutralizing is a Harmony prefix rather than a reimplementation of VulnerablePower's arithmetic, and that is
/// the whole point. A mirror of the formula would reproduce today's numbers and then go quietly wrong the first time
/// the game adds a fourth booster or reorders the three - wrong in the direction that still looks plausible, since
/// every row would still sum to the right total. Letting the game's own method run with one participant switched off
/// cannot drift: a booster this class does not know about simply stays folded into Vulnerable's share, which is
/// exactly the behaviour that shipped before, rather than a number that is confidently incorrect.
///
/// Paper Phrog is deliberately not here. It is a <c>RelicModel</c> read off <c>dealer.Player</c>, so it always belongs
/// to the dealer, who keeps their own contribution by construction - there is no one else to credit it to. It still
/// participates in every counterfactual, which is what makes Debilitate's credit come out larger when the dealer is
/// wearing it: Debilitate doubles the Phrog-inflated bonus, and the counterfactual measures that.
/// </summary>
internal static class VulnerableBoosts
{
    // The exclusion set of the counterfactual currently being replayed, or null when the game is computing damage for
    // real. Thread-static for the same reason AttributionPatches' cardPlay slot is: the damage pipeline is
    // synchronous, so one slot per thread pairs each Begin with its own End.
    [ThreadStatic]
    private static ISet<AbstractModel>? _suppressed;

    /// <summary>
    /// The modifier list the engine should reason over: the game's own, plus any hidden Vulnerable booster in play on
    /// this hit. Returns the input list untouched when there is nothing to add, so hits with no Vulnerable on them -
    /// the overwhelming majority - allocate nothing.
    ///
    /// Appending is safe: neither booster overrides a ModifyDamage listener, so the replay pipeline invokes them and
    /// gets the identity value back (0 additive, 1 multiplicative, no cap). They exist in the list purely to be
    /// excludable.
    /// </summary>
    public static IReadOnlyList<AbstractModel> Augment(
        IReadOnlyList<AbstractModel> modifiers, Creature? target, Creature? dealer)
    {
        if (target == null)
        {
            return modifiers;
        }

        // A VulnerablePower only enters the modifier list when it actually multiplied this hit, which it only does
        // for its own owner, so its presence is the whole test - no need to re-check whose it is.
        bool vulnerable = false;
        foreach (AbstractModel modifier in modifiers)
        {
            if (modifier is VulnerablePower)
            {
                vulnerable = true;
                break;
            }
        }

        if (!vulnerable)
        {
            return modifiers;
        }

        List<AbstractModel>? augmented = null;
        try
        {
            Add(target.GetPower<DebilitatePower>());
            // A pet swings with its owner's Cruelty, which is the lookup VulnerablePower itself performs.
            Add(dealer?.GetPower<CrueltyPower>() ?? dealer?.PetOwner?.Creature.GetPower<CrueltyPower>());
        }
        catch (Exception)
        {
            // Reading powers off a creature mid-hit is not worth breaking damage over; fall back to crediting the
            // stacked multiplier the way the mod always did.
            return modifiers;
        }

        return augmented ?? modifiers;

        void Add(PowerModel? booster)
        {
            if (booster == null || modifiers.Contains(booster))
            {
                return;
            }

            augmented ??= new List<AbstractModel>(modifiers);
            augmented.Add(booster);
        }
    }

    /// <summary>
    /// Opens a counterfactual: every booster in <paramref name="exclude"/> returns the multiplier it was handed,
    /// unchanged, until <see cref="End"/>. Always paired in a finally - a leaked suppression would silently flatten
    /// the real damage the game computes next.
    /// </summary>
    public static void Begin(ISet<AbstractModel> exclude)
    {
        _suppressed = exclude;
    }

    public static void End()
    {
        _suppressed = null;
    }

    public static bool IsSuppressed(AbstractModel booster)
    {
        return _suppressed != null && _suppressed.Contains(booster);
    }
}

using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace RdpsMeter;

/// <summary>
/// The Strength players have taken off each enemy, by who took it and with what, so a hit the enemy lands can be
/// re-run as if they had not.
///
/// The enemy's <see cref="StrengthPower"/> cannot answer this itself. Every source stacks into that one instance - the
/// enemy's own Ritual as well as a player's Piercing Wail - so removing it from a hit's modifier list would also remove
/// the enemy's own Strength, and when the two cancel to zero the power is not in the list at all.
///
/// Two kinds of loss, read two ways:
///
///  - <b>Temporary</b> (Piercing Wail, Enfeebling Touch, Dark Shackles, Shackling Potion, ...) is a
///    <see cref="TemporaryStrengthPower"/> debuff standing on the enemy, which quietly applies the matching negative
///    Strength and gives it back at the end of the enemy's turn. While it stands, its own amount is exactly the Strength
///    it is holding down, so it is read live and needs no bookkeeping - expiry included.
///  - <b>Permanent</b> (Malaise, Resonance, Shared Fate) is negative Strength applied directly, which nothing gives
///    back. That is recorded here as it happens.
///
/// The catch is that a temporary loss also arrives as a direct negative Strength - the debuff's own inner application -
/// and must not be booked a second time as permanent. The two are paired off by applier: the debuff's stack change and
/// its inner Strength change each arrive once, in an order that depends on whether the debuff was fresh (Strength
/// first, from BeforeApplied) or a merge (debuff first, from its AfterPowerAmountChanged), so whichever comes second
/// cancels the first. Pairing deliberately ignores the amount: a hook that adjusts how much Strength is applied could
/// make the two differ, and a missed pair would count the same loss twice for the rest of the combat.
/// </summary>
internal static class StrengthLoss
{
    private sealed record Entry(ulong NetId, string Source, decimal Amount);

    private sealed class State
    {
        public List<Entry> Permanent { get; } = new();

        // A debuff change whose inner Strength change has not arrived yet, by applier.
        public List<ulong> AwaitingStrength { get; } = new();

        // The most recent Strength change, while a debuff change could still claim it as its own inner half.
        public Entry? Unpaired { get; set; }
    }

    private static readonly Dictionary<Creature, State> ByEnemy = new();
    private static readonly object Lock = new();

    /// <summary>A player lowered an enemy's Strength by <paramref name="lost"/> (a positive number).</summary>
    public static void StrengthLowered(Creature enemy, ulong netId, string source, decimal lost)
    {
        lock (Lock)
        {
            State state = StateOf(enemy);
            int awaiting = state.AwaitingStrength.IndexOf(netId);
            if (awaiting >= 0)
            {
                state.AwaitingStrength.RemoveAt(awaiting);
                return;
            }

            var entry = new Entry(netId, source, lost);
            state.Permanent.Add(entry);
            state.Unpaired = entry;
        }
    }

    /// <summary>
    /// A player's temporary Strength-down debuff on an enemy gained stacks. <paramref name="fresh"/> is whether this
    /// application created it rather than merging onto one already standing: only a fresh one has already applied its
    /// Strength, so only a fresh one may claim the Strength change before it. A merge claiming it instead would take a
    /// permanent loss the same player had just applied - Malaise between two Piercing Wails - for its own.
    /// </summary>
    public static void TemporaryApplied(Creature enemy, ulong netId, bool fresh)
    {
        lock (Lock)
        {
            State state = StateOf(enemy);
            if (fresh && state.Unpaired is { } entry && entry.NetId == netId)
            {
                state.Permanent.Remove(entry);
            }
            else
            {
                state.AwaitingStrength.Add(netId);
            }

            state.Unpaired = null;
        }
    }

    /// <summary>
    /// Every player share of the Strength this enemy is missing, as (applier, what did it, how much). Empty when no
    /// player has lowered it.
    /// </summary>
    public static IReadOnlyList<(ulong NetId, string Source, decimal Amount)> Of(Creature enemy)
    {
        var lost = new List<(ulong NetId, string Source, decimal Amount)>();
        lock (Lock)
        {
            if (ByEnemy.TryGetValue(enemy, out State? state))
            {
                lost.AddRange(state.Permanent.Select(e => (e.NetId, e.Source, e.Amount)));
            }
        }

        foreach (PowerModel power in enemy.Powers)
        {
            if (power is not TemporaryStrengthPower { Type: PowerType.Debuff } || power.Amount <= 0)
            {
                continue;
            }

            if (AttributionEngine.OwnershipShares(power) is not { } shares)
            {
                continue;
            }

            // The debuff's title is its origin's - the card, potion or relic that applied it - which is the name the
            // row should carry.
            string source = TitleOf(power);
            foreach ((ulong netId, decimal fraction) in shares)
            {
                lost.Add((netId, source, power.Amount * fraction));
            }
        }

        return lost;
    }

    public static void Clear()
    {
        lock (Lock)
        {
            ByEnemy.Clear();
        }
    }

    private static State StateOf(Creature enemy)
    {
        if (!ByEnemy.TryGetValue(enemy, out State? state))
        {
            state = new State();
            ByEnemy[enemy] = state;
        }

        return state;
    }

    private static string TitleOf(PowerModel power)
    {
        try
        {
            return power.Title.GetFormattedText();
        }
        catch (Exception)
        {
            // TemporaryStrengthPower.Title throws for an origin it does not recognise; a name is never worth a hit.
            return "Strength";
        }
    }
}

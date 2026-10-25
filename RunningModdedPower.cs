using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter;

/// <summary>
/// The other mods' powers whose methods are running right now, whoever wears them - pushed and popped by
/// <see cref="Patches.ModdedSourcePatches"/> around the same spans it pushes onto <see cref="ExecutingEffect"/>.
///
/// It exists for the dealer-less damage a modded DoT deals on a choice context of its own making. The Engineer's Oil
/// hands CreatureCmd.Damage the context it was given, so the game's stack still has the power on top and
/// LastInvolvedModel names it. ARAM: Mayhem's Burn builds a fresh <c>ThrowingPlayerChoiceContext</c> for its
/// enemy-side tick, which starts empty - the Black Hole trap, in a mod - and ExecutingEffect does not help either,
/// being keyed by player while Burn is worn by an enemy. This stack answers the question neither can: which modded
/// power is resolving.
///
/// Only ever read for a hit with no dealer, and only for a power worn by that hit's target - a DoT ticks on whoever
/// wears it - so a modded power that happens to be running when some unrelated hit lands cannot claim it.
/// </summary>
internal static class RunningModdedPower
{
    private static readonly List<PowerModel> Running = new();
    private static readonly object Lock = new();

    public static void Push(PowerModel power)
    {
        lock (Lock)
        {
            Running.Add(power);
        }
    }

    /// <summary>
    /// Removes the innermost entry for this power rather than blindly the top: a span that ends out of order costs
    /// its own entry, never a neighbour's.
    /// </summary>
    public static void Pop(PowerModel power)
    {
        lock (Lock)
        {
            int index = Running.LastIndexOf(power);
            if (index >= 0)
            {
                Running.RemoveAt(index);
            }
        }
    }

    /// <summary>The innermost running modded power worn by one of these creatures, or null.</summary>
    public static PowerModel? WornBy(IEnumerable<Creature>? targets)
    {
        if (targets == null)
        {
            return null;
        }

        // Materialized outside the lock: a caller's lazy query should not run while we hold it.
        HashSet<Creature> hit = targets.ToHashSet();
        lock (Lock)
        {
            for (int i = Running.Count - 1; i >= 0; i--)
            {
                if (Running[i].Owner is { } owner && hit.Contains(owner))
                {
                    return Running[i];
                }
            }
        }

        return null;
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Running.Clear();
        }
    }
}

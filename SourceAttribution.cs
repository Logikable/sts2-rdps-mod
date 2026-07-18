using MegaCrit.Sts2.Core.Entities.Creatures;

namespace RdpsMeter;

/// <summary>
/// Pending attributions for single-shot, dealer-less source damage - the player DoTs whose damage the game deals with
/// no dealer (Demise, Strangle, Haunt), so <see cref="AttributionEngine"/> cannot see who to credit. Each such power's
/// patch registers its owner shares here at the moment it is about to deal damage; <see cref="Patches.AttributionPatches"/>
/// then books the settled HP loss as those players' own aDPS via <see cref="CombatLedger.ApplyDot"/>.
///
/// Entries are queued per target because one creature can take several such hits in a single resolution, and the game
/// deals each synchronously through to AfterDamageGiven before the next fires - so FIFO per target matches each booked
/// hit to the registration that caused it. Poison keeps its own richer path (Accelerant tick-splitting); this covers
/// the simpler effects that deal one hit per trigger.
/// </summary>
internal static class SourceAttribution
{
    private sealed record Entry(string Effect, IReadOnlyDictionary<ulong, decimal> Shares);

    private static readonly Dictionary<Creature, Queue<Entry>> Pending = new();
    private static readonly object Lock = new();
    private static readonly IReadOnlyDictionary<ulong, decimal> NoShares = new Dictionary<ulong, decimal>();

    public static void Register(Creature target, string effect, IReadOnlyDictionary<ulong, decimal> shares)
    {
        lock (Lock)
        {
            if (!Pending.TryGetValue(target, out Queue<Entry>? queue))
            {
                queue = new Queue<Entry>();
                Pending[target] = queue;
            }

            queue.Enqueue(new Entry(effect, shares));
        }
    }

    public static bool TryConsume(Creature target, out string effect, out IReadOnlyDictionary<ulong, decimal> shares)
    {
        lock (Lock)
        {
            if (Pending.TryGetValue(target, out Queue<Entry>? queue) && queue.Count > 0)
            {
                Entry entry = queue.Dequeue();
                if (queue.Count == 0)
                {
                    Pending.Remove(target);
                }

                effect = entry.Effect;
                shares = entry.Shares;
                return true;
            }
        }

        effect = string.Empty;
        shares = NoShares;
        return false;
    }

    public static void Clear()
    {
        lock (Lock)
        {
            Pending.Clear();
        }
    }
}

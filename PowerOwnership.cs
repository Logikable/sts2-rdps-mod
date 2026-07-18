using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter;

/// <summary>
/// Records which players contributed stacks to each power instance, so credit for a debuff whose stacks came from
/// several players can be split pro-rata. The game keeps only the first applier on PowerModel.Applier and discards
/// the rest; this recovers the full breakdown by watching every stack change.
///
/// Only positive contributions are recorded. Stack removal (decay, cleanse) is modeled as proportional - it lowers
/// the live count without changing who owns what share - so cumulative contributions stand in faithfully for live
/// stack ownership and no decay bookkeeping is needed. Entries are keyed weakly by the power instance, so a debuff
/// that expires and is later re-applied starts fresh.
/// </summary>
internal sealed class PowerOwnership
{
    public static PowerOwnership Instance { get; } = new();

    private readonly ConditionalWeakTable<PowerModel, Dictionary<ulong, decimal>> _contributions = new();
    private readonly object _lock = new();

    private PowerOwnership()
    {
    }

    public void Record(PowerModel power, ulong applierNetId, decimal stacks)
    {
        if (stacks <= 0m)
        {
            return;
        }

        lock (_lock)
        {
            Dictionary<ulong, decimal> byApplier = _contributions.GetOrCreateValue(power);
            byApplier[applierNetId] = byApplier.GetValueOrDefault(applierNetId) + stacks;
        }
    }

    /// <summary>
    /// Ownership shares (netId -> fraction summing to 1) for a power, or null if no player contributions were
    /// recorded. Callers fall back to PowerModel.Applier when this returns null - the power was applied before the
    /// mod saw it, or by a non-player.
    /// </summary>
    public IReadOnlyDictionary<ulong, decimal>? Shares(PowerModel power)
    {
        lock (_lock)
        {
            if (!_contributions.TryGetValue(power, out Dictionary<ulong, decimal>? byApplier) || byApplier.Count == 0)
            {
                return null;
            }

            decimal total = byApplier.Values.Sum();
            if (total <= 0m)
            {
                return null;
            }

            return byApplier.ToDictionary(kv => kv.Key, kv => kv.Value / total);
        }
    }
}

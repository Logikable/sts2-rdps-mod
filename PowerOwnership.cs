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

    // Keyed by applier *and* by what granted the stacks, so a pooled power (Strength, which every source stacks into
    // one instance) can still say which card or potion each teammate's share came from. Source is null wherever the
    // power's own name already identifies the effect - the usual case - and those entries behave exactly as before.
    private readonly ConditionalWeakTable<PowerModel, Dictionary<(ulong NetId, string? Source), decimal>> _contributions = new();
    private readonly object _lock = new();

    private PowerOwnership()
    {
    }

    public void Record(PowerModel power, ulong applierNetId, decimal stacks, string? source = null)
    {
        if (stacks <= 0m)
        {
            return;
        }

        lock (_lock)
        {
            Dictionary<(ulong, string?), decimal> byApplier = _contributions.GetOrCreateValue(power);
            var key = (applierNetId, source);
            byApplier[key] = byApplier.GetValueOrDefault(key) + stacks;
        }
    }

    /// <summary>
    /// Ownership shares (netId -> fraction summing to 1) for a power, or null if no player contributions were
    /// recorded. Callers fall back to PowerModel.Applier when this returns null - the power was applied before the
    /// mod saw it, or by a non-player.
    /// </summary>
    public IReadOnlyDictionary<ulong, decimal>? Shares(PowerModel power)
    {
        return SourcedShares(power)
            ?.GroupBy(s => s.NetId)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.Fraction));
    }

    /// <summary>
    /// The same shares, but split by what granted each one as well as by who: (applier, source, fraction), summing to
    /// 1 across the whole list. A null source means the power's own name is the effect's name.
    /// </summary>
    public IReadOnlyList<(ulong NetId, string? Source, decimal Fraction)>? SourcedShares(PowerModel power)
    {
        lock (_lock)
        {
            if (!_contributions.TryGetValue(power, out Dictionary<(ulong NetId, string? Source), decimal>? byApplier)
                || byApplier.Count == 0)
            {
                return null;
            }

            decimal total = byApplier.Values.Sum();
            if (total <= 0m)
            {
                return null;
            }

            return byApplier.Select(kv => (kv.Key.NetId, kv.Key.Source, kv.Value / total)).ToList();
        }
    }
}

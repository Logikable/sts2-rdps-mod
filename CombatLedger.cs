using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace RdpsMeter;

/// <summary>
/// Per-combat damage tallies for every player. <see cref="Raw"/> is the classic damage-meter number - the whole
/// unblocked amount of each hit credited to whoever dealt it. <see cref="Rdps"/> redistributes that same total by
/// attribution: a hit's dealer keeps only their own share and the rest flows to the teammates whose buffs boosted
/// it. Across all players both columns sum to the same total damage; the difference between a player's two columns is
/// their net support contribution.
/// </summary>
internal sealed class PlayerTotals
{
    public decimal Raw { get; set; }
    public decimal Rdps { get; set; }
}

internal sealed class CombatLedger
{
    public static CombatLedger Instance { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<ulong, PlayerTotals> _totals = new();
    private readonly Dictionary<ulong, string> _names = new();

    private CombatLedger()
    {
    }

    public void Reset()
    {
        lock (_lock)
        {
            _totals.Clear();
        }
    }

    /// <summary>
    /// Folds one settled hit into the tallies. The attribution carries pre-block shares; here they are rescaled onto
    /// the actual unblocked HP loss so block reduces every share by the same proportion. Fully-blocked or
    /// zero-damage hits contribute nothing. Monster-dealt hits (no dealer player) are ignored - this is a meter of
    /// player damage output.
    /// </summary>
    public void ApplyHit(HitAttribution attribution, DamageResult result)
    {
        if (!attribution.HasDealer || attribution.Total <= 0m)
        {
            return;
        }

        int unblocked = result.UnblockedDamage + result.OverkillDamage;
        if (unblocked <= 0)
        {
            return;
        }

        lock (_lock)
        {
            ulong dealer = attribution.DealerNetId!.Value;
            Totals(dealer).Raw += unblocked;
            Totals(dealer).Rdps += unblocked * attribution.DealerPreBlock / attribution.Total;

            foreach ((ulong applier, decimal preBlock) in attribution.ExternalPreBlock)
            {
                Totals(applier).Rdps += unblocked * preBlock / attribution.Total;
            }
        }
    }

    public void RecordName(ulong netId, string name)
    {
        lock (_lock)
        {
            _names[netId] = name;
        }
    }

    /// <summary>
    /// Snapshot of the current tallies, sorted by rDPS descending, with display names resolved.
    /// </summary>
    public IReadOnlyList<(ulong NetId, string Name, decimal Raw, decimal Rdps)> Snapshot()
    {
        lock (_lock)
        {
            return _totals
                .Select(kv => (
                    kv.Key,
                    _names.GetValueOrDefault(kv.Key, kv.Key.ToString()),
                    kv.Value.Raw,
                    kv.Value.Rdps))
                .OrderByDescending(row => row.Rdps)
                .ToList();
        }
    }

    public void PrintSummary()
    {
        IReadOnlyList<(ulong NetId, string Name, decimal Raw, decimal Rdps)> rows = Snapshot();
        if (rows.Count == 0)
        {
            return;
        }

        GD.Print("[RdpsMeter] === combat summary (raw damage -> rDPS) ===");
        foreach ((ulong _, string name, decimal raw, decimal rdps) in rows)
        {
            decimal delta = rdps - raw;
            string sign = delta >= 0m ? "+" : "";
            GD.Print($"[RdpsMeter]   {name,-20} raw {Math.Round(raw),5} | rDPS {Math.Round(rdps),5} ({sign}{Math.Round(delta)})");
        }
    }

    private PlayerTotals Totals(ulong netId)
    {
        if (!_totals.TryGetValue(netId, out PlayerTotals? totals))
        {
            totals = new PlayerTotals();
            _totals[netId] = totals;
        }

        return totals;
    }
}

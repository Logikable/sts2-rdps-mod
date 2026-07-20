using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace RdpsMeter;

/// <summary>
/// Per-player rDPS accounting for one combat, decomposed the way an rDPS tooltip reads:
///
///   rDPS = aDPS + given - received
///
/// where aDPS is the raw unblocked damage the player actually dealt (itemized by card), given is the damage their
/// buffs/debuffs enabled on teammates' hits (itemized by effect and beneficiary), and received is the damage
/// teammates' buffs enabled on this player's own hits (itemized by effect and applier). Every given entry has a
/// matching received entry on the other player, so across the table given and received cancel and total rDPS equals
/// total damage dealt.
/// </summary>
internal sealed class PlayerLedger
{
    public Dictionary<string, decimal> DealtByCard { get; } = new();

    // Of the damage in DealtByCard, the portion that came from teammates' buffs on those hits (the rest is the card's
    // own damage). Lets the breakdown split each card's bar into own-vs-buff.
    public Dictionary<string, decimal> BuffByCard { get; } = new();

    public Dictionary<(string Effect, ulong Other), decimal> GivenBySource { get; } = new();
    public Dictionary<(string Effect, ulong Other), decimal> ReceivedBySource { get; } = new();

    public decimal ADps => DealtByCard.Values.Sum();
    public decimal Given => GivenBySource.Values.Sum();
    public decimal Received => ReceivedBySource.Values.Sum();
    public decimal Rdps => ADps + Given - Received;
}

/// <summary>
/// One player's rendered rDPS line: totals plus the itemized sources, names already resolved, sorted biggest-first.
/// A frozen copy the overlay can read without touching the live ledger or holding its lock.
/// </summary>
internal sealed class RdpsRow
{
    public required ulong NetId { get; init; }
    public required string Name { get; init; }
    public required decimal ADps { get; init; }
    public required decimal Given { get; init; }
    public required decimal Received { get; init; }
    public required IReadOnlyList<(string Card, decimal Amount, decimal Buff)> Dealt { get; init; }
    public required IReadOnlyList<(string Effect, ulong Other, decimal Amount)> GivenBy { get; init; }
    public required IReadOnlyList<(string Effect, ulong Other, decimal Amount)> ReceivedBy { get; init; }

    public decimal Rdps => ADps + Given - Received;
}

internal sealed class CombatLedger
{
    // Two tallies fed in parallel: Current is wiped at the start of each combat (this fight only), Total accumulates
    // for the whole session (every fight). The overlay shows whichever one the player has toggled to.
    public static CombatLedger Current { get; } = new();
    public static CombatLedger Total { get; } = new();

    private static readonly IReadOnlyList<CombatLedger> Writers = new[] { Current, Total };

    private readonly object _lock = new();
    private readonly Dictionary<ulong, PlayerLedger> _ledgers = new();
    private readonly Dictionary<ulong, string> _names = new();

    private CombatLedger()
    {
    }

    /// <summary>Folds one settled hit into every tally.</summary>
    public static void Record(HitAttribution attribution, DamageResult result)
    {
        foreach (CombatLedger ledger in Writers)
        {
            ledger.ApplyHit(attribution, result);
        }
    }

    /// <summary>Folds one damage-over-time tick into every tally.</summary>
    public static void Record(string effect, IReadOnlyDictionary<ulong, decimal> shares, int effectiveDamage)
    {
        foreach (CombatLedger ledger in Writers)
        {
            ledger.ApplyDot(effect, shares, effectiveDamage);
        }
    }

    /// <summary>Records a resolved player name in every tally.</summary>
    public static void Name(ulong netId, string name)
    {
        foreach (CombatLedger ledger in Writers)
        {
            ledger.RecordName(netId, name);
        }
    }

    /// <summary>Wipes the current-combat tally at each combat start; Total is left to accumulate across fights.</summary>
    public static void ResetCurrent()
    {
        Current.Reset();
    }

    public void Reset()
    {
        lock (_lock)
        {
            _ledgers.Clear();
        }
    }

    /// <summary>
    /// Folds one settled hit into the tallies. The attribution carries pre-block shares; here they are rescaled onto
    /// the actual unblocked HP loss so block reduces every share by the same proportion. The dealer's full unblocked
    /// damage counts as aDPS; each teammate contribution is booked as received (on the dealer) and given (on the
    /// applier). Monster-dealt and fully-blocked hits contribute nothing.
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
            ulong dealerNetId = attribution.DealerNetId!.Value;
            PlayerLedger dealer = Ledger(dealerNetId);
            dealer.DealtByCard[attribution.DealerCard] =
                dealer.DealtByCard.GetValueOrDefault(attribution.DealerCard) + unblocked;

            decimal buffTotal = 0m;
            foreach (ExternalContribution contribution in attribution.Externals)
            {
                decimal amount = unblocked * contribution.PreBlock / attribution.Total;
                buffTotal += amount;

                var received = (contribution.Effect, contribution.ApplierNetId);
                dealer.ReceivedBySource[received] = dealer.ReceivedBySource.GetValueOrDefault(received) + amount;

                PlayerLedger applier = Ledger(contribution.ApplierNetId);
                var given = (contribution.Effect, dealerNetId);
                applier.GivenBySource[given] = applier.GivenBySource.GetValueOrDefault(given) + amount;
            }

            if (buffTotal > 0m)
            {
                dealer.BuffByCard[attribution.DealerCard] =
                    dealer.BuffByCard.GetValueOrDefault(attribution.DealerCard) + buffTotal;
            }
        }
    }

    /// <summary>
    /// Damage <paramref name="netId"/> received on their own hits from <paramref name="effect"/> applied by
    /// <paramref name="other"/>. Zero if there is no such entry. For the self-test harness to assert attribution.
    /// </summary>
    public decimal ReceivedFrom(ulong netId, string effect, ulong other)
    {
        lock (_lock)
        {
            return _ledgers.TryGetValue(netId, out PlayerLedger? l)
                ? l.ReceivedBySource.GetValueOrDefault((effect, other))
                : 0m;
        }
    }

    /// <summary>
    /// Damage <paramref name="netId"/> gave to <paramref name="other"/> via <paramref name="effect"/>. Zero if absent.
    /// </summary>
    public decimal GivenTo(ulong netId, string effect, ulong other)
    {
        lock (_lock)
        {
            return _ledgers.TryGetValue(netId, out PlayerLedger? l)
                ? l.GivenBySource.GetValueOrDefault((effect, other))
                : 0m;
        }
    }

    /// <summary>
    /// Raw damage <paramref name="netId"/> dealt themselves through <paramref name="card"/> (aDPS). Zero if absent.
    /// </summary>
    public decimal DealtWith(ulong netId, string card)
    {
        lock (_lock)
        {
            return _ledgers.TryGetValue(netId, out PlayerLedger? l)
                ? l.DealtByCard.GetValueOrDefault(card)
                : 0m;
        }
    }

    /// <summary>
    /// Folds one damage-over-time tick into the tallies. Unlike a struck hit, a DoT has no separate dealer - the
    /// players who applied it (or, for Accelerant-driven extra ticks, who forced it) are the source, so the effective
    /// HP loss counts as their own aDPS split by <paramref name="shares"/>. There is no given/received: nobody is
    /// crediting anyone else's swing. <paramref name="effectiveDamage"/> is the actual HP removed, so an overkilling
    /// tick only distributes the HP that was there.
    /// </summary>
    public void ApplyDot(string effect, IReadOnlyDictionary<ulong, decimal> shares, int effectiveDamage)
    {
        if (effectiveDamage <= 0 || shares.Count == 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach ((ulong netId, decimal fraction) in shares)
            {
                PlayerLedger l = Ledger(netId);
                l.DealtByCard[effect] = l.DealtByCard.GetValueOrDefault(effect) + effectiveDamage * fraction;
            }
        }
    }

    /// <summary>
    /// A frozen, name-resolved snapshot of every player's line, sorted by rDPS descending, for live rendering.
    /// </summary>
    public IReadOnlyList<RdpsRow> Snapshot()
    {
        lock (_lock)
        {
            return _ledgers
                .Select(kv => new RdpsRow
                {
                    NetId = kv.Key,
                    Name = NameOf(kv.Key),
                    ADps = kv.Value.ADps,
                    Given = kv.Value.Given,
                    Received = kv.Value.Received,
                    Dealt = kv.Value.DealtByCard
                        .Select(d => (d.Key, d.Value, kv.Value.BuffByCard.GetValueOrDefault(d.Key)))
                        .OrderByDescending(d => d.Value).ToList(),
                    GivenBy = kv.Value.GivenBySource
                        .Select(d => (d.Key.Effect, d.Key.Other, d.Value)).OrderByDescending(d => d.Value).ToList(),
                    ReceivedBy = kv.Value.ReceivedBySource
                        .Select(d => (d.Key.Effect, d.Key.Other, d.Value)).OrderByDescending(d => d.Value).ToList(),
                })
                .OrderByDescending(r => r.Rdps)
                .ToList();
        }
    }

    public void RecordName(ulong netId, string name)
    {
        lock (_lock)
        {
            _names[netId] = name;
        }
    }

    public void PrintSummary()
    {
        lock (_lock)
        {
            if (_ledgers.Count == 0)
            {
                return;
            }

            GD.Print("[RdpsMeter] === combat summary ===");
            foreach ((ulong netId, PlayerLedger ledger) in _ledgers.OrderByDescending(kv => kv.Value.Rdps))
            {
                GD.Print($"[RdpsMeter] {NameOf(netId),-20} "
                    + $"aDPS {Round(ledger.ADps),5} + given {Round(ledger.Given),4} - recv {Round(ledger.Received),4} "
                    + $"= rDPS {Round(ledger.Rdps),5}");

                foreach ((string card, decimal amount) in ledger.DealtByCard.OrderByDescending(kv => kv.Value))
                {
                    GD.Print($"[RdpsMeter]     dealt  {card} {Round(amount)}");
                }

                foreach (((string effect, ulong other), decimal amount) in ledger.GivenBySource.OrderByDescending(kv => kv.Value))
                {
                    GD.Print($"[RdpsMeter]     given  {effect} -> {NameOf(other)} {Round(amount)}");
                }

                foreach (((string effect, ulong other), decimal amount) in ledger.ReceivedBySource.OrderByDescending(kv => kv.Value))
                {
                    GD.Print($"[RdpsMeter]     recv   {effect} <- {NameOf(other)} {Round(amount)}");
                }
            }
        }
    }

    public string NameOf(ulong netId)
    {
        return _names.GetValueOrDefault(netId, netId.ToString());
    }

    private PlayerLedger Ledger(ulong netId)
    {
        if (!_ledgers.TryGetValue(netId, out PlayerLedger? ledger))
        {
            ledger = new PlayerLedger();
            _ledgers[netId] = ledger;
        }

        return ledger;
    }

    private static decimal Round(decimal value)
    {
        return Math.Round(value, MidpointRounding.AwayFromZero);
    }
}

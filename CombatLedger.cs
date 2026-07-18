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
    public Dictionary<(string Effect, ulong Other), decimal> GivenBySource { get; } = new();
    public Dictionary<(string Effect, ulong Other), decimal> ReceivedBySource { get; } = new();

    public decimal ADps => DealtByCard.Values.Sum();
    public decimal Given => GivenBySource.Values.Sum();
    public decimal Received => ReceivedBySource.Values.Sum();
    public decimal Rdps => ADps + Given - Received;
}

internal sealed class CombatLedger
{
    public static CombatLedger Instance { get; } = new();

    private readonly object _lock = new();
    private readonly Dictionary<ulong, PlayerLedger> _ledgers = new();
    private readonly Dictionary<ulong, string> _names = new();

    private CombatLedger()
    {
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

            foreach (ExternalContribution contribution in attribution.Externals)
            {
                decimal amount = unblocked * contribution.PreBlock / attribution.Total;

                var received = (contribution.Effect, contribution.ApplierNetId);
                dealer.ReceivedBySource[received] = dealer.ReceivedBySource.GetValueOrDefault(received) + amount;

                PlayerLedger applier = Ledger(contribution.ApplierNetId);
                var given = (contribution.Effect, dealerNetId);
                applier.GivenBySource[given] = applier.GivenBySource.GetValueOrDefault(given) + amount;
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

    private string NameOf(ulong netId)
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

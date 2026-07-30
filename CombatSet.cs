namespace RdpsMeter;

/// <summary>
/// The combats of one run and the party that fought them: the shape both a live run
/// (<see cref="RunLedger"/>) and a finished one read back off disk (<see cref="ArchivedRun"/>) are made of.
///
/// The two used to keep their own copies of all of this - the same dictionary, the same order list, the same loop for
/// "the nth fight of act N", the same rebuild-from-a-DTO. They are the same thing read at different times, so they are
/// one type now, and a reader that works on a live run works on an archived one for free.
///
/// Deliberately not thread-safe and deliberately mutable: the live run needs a lock around whole operations rather than
/// around each field (a snapshot taken between two writes would be torn), so the lock belongs to the owner. Both owners
/// hold theirs across every call into here.
///
/// <see cref="Order"/> is the keys in the order the fights were entered, kept apart from the dictionary so fight numbers
/// stay stable when a re-entered combat replaces its slot.
/// </summary>
internal sealed class CombatSet
{
    public Dictionary<string, CombatLedger> Combats { get; } = new();
    public List<string> Order { get; } = new();
    public Dictionary<ulong, RosterEntryDto> Party { get; } = new();

    /// <summary>Rebuilds a set from a saved run, or an empty one from null.</summary>
    public static CombatSet From(RunLedgerDto? dto)
    {
        var set = new CombatSet();
        if (dto == null)
        {
            return set;
        }

        foreach (CombatEntryDto entry in dto.Combats)
        {
            set.Combats[entry.Key] = CombatLedger.FromState(entry);
            set.Order.Add(entry.Key);
        }

        foreach (RosterEntryDto player in dto.Roster)
        {
            set.Party[player.NetId] = player;
        }

        return set;
    }

    public void Clear()
    {
        Combats.Clear();
        Order.Clear();
        Party.Clear();
    }

    /// <summary>
    /// The nth combat recorded in one act, or null when the act has no such fight. Combat keys lead with the act index
    /// (see <see cref="RunContext.CombatKey"/>), so this counts within an act in entry order - what the run history page
    /// needs to line its map points up against a ledger without a shared key. See <see cref="RunHistoryLink"/>.
    /// </summary>
    public CombatInfo? FightInAct(int act, int ordinal)
    {
        string prefix = $"{act}:";
        int seen = 0;
        foreach (string key in Order)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)
                || !Combats.TryGetValue(key, out CombatLedger? combat))
            {
                continue;
            }

            if (seen++ == ordinal)
            {
                return new CombatInfo(key, combat.Label);
            }
        }

        return null;
    }

    /// <summary>One combat's rows, or empty when the set has no such fight.</summary>
    public IReadOnlyList<RdpsRow> SnapshotOf(string key)
    {
        return Combats.TryGetValue(key, out CombatLedger? combat) ? combat.Snapshot() : Array.Empty<RdpsRow>();
    }

    /// <summary>The character this player was running, as a ModelId string, or null for one the set never saw.</summary>
    public string? CharacterOf(ulong netId)
    {
        return Party.TryGetValue(netId, out RosterEntryDto? entry) && !string.IsNullOrEmpty(entry.Character)
            ? entry.Character
            : null;
    }
}

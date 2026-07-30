using Godot;

namespace RdpsMeter;

/// <summary>
/// A finished run's breakdown, read back off disk so the run history page can show numbers for a run that is not the
/// one loaded in <see cref="RunLedger"/>.
///
/// The page browses every run the game remembers, but the meter only ever holds one run in memory - the one being
/// played. Everything else is on disk, one file per seed, and until this existed the page simply refused to resolve
/// those fights and drew an empty meter under their names. Nothing here writes: an archived run is finished, so it is
/// loaded, read and never persisted back.
///
/// One run cached at a time rather than all of them, because that is exactly how the page is used - the arrows move to
/// a run and the player then walks its map points, so every hit after the first lands on the same run. Moving to
/// another run evicts the previous one, which keeps a long browsing session from holding every run it passed.
///
/// A run with no file is a normal outcome, not a failure: it may predate the mod, or have been pruned. That caches as a
/// miss so a page full of old runs does not re-read the disk on every map point, and shows as the same empty meter it
/// always did.
/// </summary>
internal static class ArchivedRun
{
    private static readonly object Lock = new();

    // The run currently cached, and its rebuilt tallies. Both null together when nothing is cached; _combats stays null
    // for a run whose file is missing, which is the cached miss.
    private static string? _runId;
    private static Dictionary<string, CombatLedger>? _combats;
    private static List<string>? _order;
    private static Dictionary<ulong, RosterEntryDto>? _party;

    /// <summary>
    /// The nth combat recorded in one act of an archived run, or null when that run or fight is not on disk. Mirrors
    /// <see cref="RunLedger.FightInAct"/>, and for the same reason - the history page has no key in common with the
    /// ledger, only the order fights were entered in.
    /// </summary>
    public static CombatInfo? FightInAct(string runId, int act, int ordinal)
    {
        string prefix = $"{act}:";
        lock (Lock)
        {
            if (!Ensure(runId))
            {
                return null;
            }

            int seen = 0;
            foreach (string key in _order!)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal)
                    || !_combats!.TryGetValue(key, out CombatLedger? combat))
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
    }

    /// <summary>One archived combat's rows, or empty when the run or the fight is not on disk.</summary>
    public static IReadOnlyList<RdpsRow> SnapshotOf(string runId, string key)
    {
        lock (Lock)
        {
            return Ensure(runId) && _combats!.TryGetValue(key, out CombatLedger? combat)
                ? combat.Snapshot()
                : Array.Empty<RdpsRow>();
        }
    }

    /// <summary>The character an archived run's player was running, so their row keeps its class colour.</summary>
    public static string? CharacterOf(string runId, ulong netId)
    {
        lock (Lock)
        {
            return Ensure(runId) && _party!.TryGetValue(netId, out RosterEntryDto? entry)
                && !string.IsNullOrEmpty(entry.Character)
                    ? entry.Character
                    : null;
        }
    }

    /// <summary>Drops the cached run. For the self-test, and for a save that has just rewritten a run's file.</summary>
    public static void Forget()
    {
        lock (Lock)
        {
            _runId = null;
            _combats = null;
            _order = null;
            _party = null;
        }
    }

    // Brings the named run into the cache, returning whether it has anything to read. Callers hold Lock.
    private static bool Ensure(string runId)
    {
        if (string.IsNullOrEmpty(runId))
        {
            return false;
        }

        if (_runId == runId)
        {
            return _combats != null;
        }

        _runId = runId;
        _combats = null;
        _order = null;
        _party = null;

        try
        {
            RunLedgerDto? saved = RunLedgerStore.Load(runId);

            // The run id inside the file is what decides it, not the file's name: two seeds can fold to the same
            // filename (see RunLedgerStore.PathFor), and showing one run's damage under another's fights is the one
            // outcome worse than showing none.
            if (saved == null || saved.RunId != runId)
            {
                return false;
            }

            var combats = new Dictionary<string, CombatLedger>();
            var order = new List<string>();
            foreach (CombatEntryDto entry in saved.Combats)
            {
                combats[entry.Key] = CombatLedger.FromState(entry);
                order.Add(entry.Key);
            }

            var party = new Dictionary<ulong, RosterEntryDto>();
            foreach (RosterEntryDto player in saved.Roster)
            {
                party[player.NetId] = player;
            }

            _combats = combats;
            _order = order;
            _party = party;
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Could not read the archived run '{runId}': {ex}");
            return false;
        }
    }
}

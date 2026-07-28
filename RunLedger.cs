namespace RdpsMeter;

/// <summary>One combat in the run, for the fight picker: its stable key and its short display label.</summary>
internal readonly record struct CombatInfo(string Key, string Label);

/// <summary>
/// The rDPS accounting for a whole run, kept as one tally per combat rather than a single running total. Each combat is
/// filed under its RunLocation key (see <see cref="RunContext.CombatKey"/>) and remembers the order it was entered, so
/// the overlay can offer a "Fight 1, Fight 2, ..." picker alongside the current-combat and whole-run views. The "current"
/// view is the active combat's tally and the "total" view is the sum of every combat's, so the panes stay consistent by
/// construction.
///
/// Keying by combat is what makes a mid-combat save reload correct: the game restarts that combat from the top, and
/// <see cref="BeginCombat"/> replaces its slot (keeping its place in the order), so the aborted attempt's damage is
/// discarded from every view instead of being counted twice. The whole map is persisted under the run's own seed (see
/// <see cref="RunLedgerStore"/>), so a run paused today and resumed another day keeps its breakdown and its fight names,
/// and runs paused in parallel - a solo one and a co-op one - keep separate breakdowns rather than overwriting.
/// </summary>
internal static class RunLedger
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, CombatLedger> Combats = new();

    // The combat keys in the order they were first entered, so fights number stably (Fight 1, 2, 3) even as the dict
    // is re-keyed by a reloaded combat.
    private static readonly List<string> Order = new();

    // The active combat's tally, where live hits are booked. Defaults to a detached ledger so writes before the first
    // combat (or after a run ends) go somewhere harmless rather than throwing.
    private static CombatLedger _active = new();
    private static string _runId = string.Empty;

    // Bumped every time a run is started or resumed, so the overlay can tell the roster changed and drop cached
    // per-player visuals (a new run may be a different character on the same local net id).
    public static int Generation { get; private set; }

    /// <summary>The active combat's tally. All live writes land here.</summary>
    public static CombatLedger Active
    {
        get
        {
            lock (Lock)
            {
                return _active;
            }
        }
    }

    /// <summary>A new run is starting: drop every combat from the previous run and start its saved file fresh.</summary>
    public static void StartNewRun(string runId)
    {
        lock (Lock)
        {
            Combats.Clear();
            Order.Clear();
            _active = new CombatLedger();
            _runId = runId;
            Generation++;
        }

        Persist();
    }

    /// <summary>
    /// A saved run is resuming: reload its breakdown from disk if the saved file belongs to this run, otherwise start
    /// fresh. The active tally is left detached until the resumed combat's <see cref="BeginCombat"/> re-points it.
    /// </summary>
    public static void ResumeRun(string runId)
    {
        RunLedgerDto? saved = RunLedgerStore.Load(runId);

        lock (Lock)
        {
            if (saved == null && _runId == runId)
            {
                // Nothing readable on disk, but what is already in memory belongs to this same run - the breakdown
                // loaded at startup, say, or one this session already tallied. Keep it rather than wiping good numbers
                // over a missing file; only the active tally detaches, as a full restore would leave it.
                _active = new CombatLedger();
            }
            else
            {
                Restore(saved != null && saved.RunId == runId ? saved : null, runId);
            }

            Generation++;
        }
    }

    /// <summary>
    /// Startup: adopt the breakdown of whichever run was played last, so the meter is already showing something at the
    /// main menu instead of coming up blank until the next fight starts. Whatever run the player then starts or
    /// continues replaces this through <see cref="StartNewRun"/> or <see cref="ResumeRun"/>.
    /// </summary>
    public static void LoadLastPlayed()
    {
        RunLedgerDto? saved = RunLedgerStore.LoadMostRecent();
        if (saved == null)
        {
            return;
        }

        lock (Lock)
        {
            Restore(saved, saved.RunId);
            Generation++;
        }
    }

    /// <summary>
    /// Whether any combat in the loaded run recorded anybody. This is what decides that the meter has something worth
    /// showing - deliberately not "does the picked view have rows", so switching to a fight that happens to be empty,
    /// or leaving combat with the live view selected, never makes the window disappear out from under the player.
    /// </summary>
    public static bool HasData
    {
        get
        {
            lock (Lock)
            {
                foreach (CombatLedger combat in Combats.Values)
                {
                    if (!combat.IsEmpty)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// A combat is beginning. Point the active tally at a fresh ledger for this combat, replacing any tally already
    /// filed under the same key - that only happens when a mid-combat save was reloaded and the fight is being replayed,
    /// in which case the aborted attempt must be discarded. Replacing keeps the combat's place in the order, so fight
    /// numbers don't shuffle, and the wipe drops the old attempt from the total view too.
    /// </summary>
    public static void BeginCombat(string key, string label)
    {
        lock (Lock)
        {
            var ledger = new CombatLedger { Label = label };
            if (!Combats.ContainsKey(key))
            {
                Order.Add(key);
            }

            Combats[key] = ledger;
            _active = ledger;
        }

        Persist();
    }

    /// <summary>A combat has ended: the active tally is final, so print it and save the run's breakdown.</summary>
    public static void EndCombat()
    {
        Active.PrintSummary();
        Persist();
    }

    public static IReadOnlyList<RdpsRow> CurrentSnapshot()
    {
        return Active.Snapshot();
    }

    /// <summary>The whole run's tally: every combat's ledger folded into one, then snapshotted like a single combat.</summary>
    public static IReadOnlyList<RdpsRow> TotalSnapshot()
    {
        var aggregate = new CombatLedger();
        lock (Lock)
        {
            foreach (CombatLedger combat in Combats.Values)
            {
                combat.AccumulateInto(aggregate);
            }
        }

        return aggregate.Snapshot();
    }

    /// <summary>A single combat's tally, or an empty snapshot if that combat is no longer in the run.</summary>
    public static IReadOnlyList<RdpsRow> SnapshotOf(string key)
    {
        lock (Lock)
        {
            return Combats.TryGetValue(key, out CombatLedger? combat) ? combat.Snapshot() : Array.Empty<RdpsRow>();
        }
    }

    /// <summary>The run's combats in entry order, for building the fight picker.</summary>
    public static IReadOnlyList<CombatInfo> Fights()
    {
        lock (Lock)
        {
            var list = new List<CombatInfo>(Order.Count);
            foreach (string key in Order)
            {
                if (Combats.TryGetValue(key, out CombatLedger? combat))
                {
                    list.Add(new CombatInfo(key, combat.Label));
                }
            }

            return list;
        }
    }

    /// <summary>Which run the loaded breakdown belongs to, so a caller can tell whether a run's numbers are in memory.</summary>
    public static string LoadedRunId
    {
        get
        {
            lock (Lock)
            {
                return _runId;
            }
        }
    }

    /// <summary>
    /// The nth combat recorded in one act, or null when the act has no such fight. Combat keys lead with the act index
    /// (see <see cref="RunContext.CombatKey"/>), so this counts within an act in entry order - what the run history page
    /// needs to line its map points up against the ledger without a shared key. See <see cref="RunHistoryLink"/>.
    /// </summary>
    public static CombatInfo? FightInAct(int act, int ordinal)
    {
        string prefix = $"{act}:";
        lock (Lock)
        {
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
    }

    public static bool HasCombat(string key)
    {
        lock (Lock)
        {
            return Combats.ContainsKey(key);
        }
    }

    public static RunLedgerDto ToDto()
    {
        lock (Lock)
        {
            var dto = new RunLedgerDto { RunId = _runId };
            foreach (string key in Order)
            {
                if (Combats.TryGetValue(key, out CombatLedger? combat))
                {
                    dto.Combats.Add(combat.ToState(key));
                }
            }

            return dto;
        }
    }

    /// <summary>Replaces the in-memory tallies with a loaded snapshot. For the round-trip self-test.</summary>
    public static void LoadDto(RunLedgerDto? dto)
    {
        lock (Lock)
        {
            Restore(dto, dto?.RunId ?? string.Empty);
        }
    }

    // Rebuild the in-memory state from a saved snapshot (or empty when null), preserving the saved combat order. Callers
    // hold Lock.
    private static void Restore(RunLedgerDto? dto, string runId)
    {
        Combats.Clear();
        Order.Clear();
        _active = new CombatLedger();
        _runId = runId;
        if (dto == null)
        {
            return;
        }

        foreach (CombatEntryDto entry in dto.Combats)
        {
            Combats[entry.Key] = CombatLedger.FromState(entry);
            Order.Add(entry.Key);
        }
    }

    private static void Persist()
    {
        RunLedgerStore.Save(ToDto());
    }
}

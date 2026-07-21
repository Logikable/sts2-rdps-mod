namespace RdpsMeter;

/// <summary>
/// The rDPS accounting for a whole run, kept as one tally per combat rather than a single running total. Each combat is
/// filed under its RunLocation key (see <see cref="RunContext.CombatKey"/>); the "current" view is the active combat's
/// tally and the "total" view is the sum of every combat's, so the two panes stay consistent by construction.
///
/// Keying by combat is what makes a mid-combat save reload correct: the game restarts that combat from the top, and
/// <see cref="BeginCombat"/> replaces its slot, so the aborted attempt's damage is discarded from both the current and
/// the total pane instead of being counted twice. The whole map is persisted per run (keyed by the run seed), so a run
/// paused today and resumed another day keeps its breakdown.
/// </summary>
internal static class RunLedger
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, CombatLedger> Combats = new();

    // The active combat's tally, where live hits are booked. Defaults to a detached ledger so writes before the first
    // combat (or after a run ends) go somewhere harmless rather than throwing.
    private static CombatLedger _active = new();
    private static string _runId = string.Empty;

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
            _active = new CombatLedger();
            _runId = runId;
        }

        Persist();
    }

    /// <summary>
    /// A saved run is resuming: reload its breakdown from disk if the saved file belongs to this run, otherwise start
    /// fresh. The active tally is left detached until the resumed combat's <see cref="BeginCombat"/> re-points it.
    /// </summary>
    public static void ResumeRun(string runId)
    {
        RunLedgerDto? saved = RunLedgerStore.Load();

        lock (Lock)
        {
            Combats.Clear();
            _active = new CombatLedger();
            _runId = runId;

            if (saved != null && saved.RunId == runId)
            {
                foreach (CombatEntryDto entry in saved.Combats)
                {
                    Combats[entry.Key] = CombatLedger.FromState(entry);
                }
            }
        }
    }

    /// <summary>
    /// A combat is beginning. Point the active tally at a fresh ledger for this combat, replacing any tally already
    /// filed under the same key - that only happens when a mid-combat save was reloaded and the fight is being replayed,
    /// in which case the aborted attempt must be discarded. The wipe drops it from the total pane too, since the total
    /// is the sum of the surviving per-combat tallies.
    /// </summary>
    public static void BeginCombat(string key)
    {
        lock (Lock)
        {
            var ledger = new CombatLedger();
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

    public static RunLedgerDto ToDto()
    {
        lock (Lock)
        {
            var dto = new RunLedgerDto { RunId = _runId };
            foreach ((string key, CombatLedger combat) in Combats)
            {
                dto.Combats.Add(combat.ToState(key));
            }

            return dto;
        }
    }

    /// <summary>Replaces the in-memory tallies with a loaded snapshot. For the round-trip self-test.</summary>
    public static void LoadDto(RunLedgerDto? dto)
    {
        lock (Lock)
        {
            Combats.Clear();
            _active = new CombatLedger();
            _runId = dto?.RunId ?? string.Empty;
            if (dto == null)
            {
                return;
            }

            foreach (CombatEntryDto entry in dto.Combats)
            {
                Combats[entry.Key] = CombatLedger.FromState(entry);
            }
        }
    }

    private static void Persist()
    {
        RunLedgerStore.Save(ToDto());
    }
}

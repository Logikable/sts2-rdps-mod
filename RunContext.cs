using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Runs;

namespace RdpsMeter;

/// <summary>
/// The live run the meter is accounting for, captured from the RunManager setup hooks (the manager's own State became
/// private in 0.109, but the setup methods still hand us the RunState). It answers two questions the ledger needs: what
/// identifies this run (its seed, used to key the saved breakdown so a different run's file is never loaded) and what
/// identifies the current combat (its RunLocation - act, map coordinate and sub-room - which is stable across a
/// mid-combat save reload, so re-entering the same fight lands on the same slot and overwrites the aborted attempt).
/// </summary>
internal static class RunContext
{
    public static RunState? State { get; set; }

    public static string RunId => State?.Rng.StringSeed ?? "unknown";

    /// <summary>
    /// A stable string key for the combat the run is currently in. Two visits to the same fight (e.g. after loading a
    /// mid-combat save) produce the same key, so the ledger can recognise and replace the restarted combat.
    /// </summary>
    public static string CombatKey
    {
        get
        {
            if (State?.RunLocation is not RunLocation loc)
            {
                return "unknown";
            }

            MapCoord? coord = loc.mapLocation.coord;
            string col = coord.HasValue ? coord.Value.col.ToString() : "-";
            string row = coord.HasValue ? coord.Value.row.ToString() : "-";
            string room = loc.roomId.HasValue ? loc.roomId.Value.ToString() : "-";
            return $"{loc.mapLocation.actIndex}:{col}:{row}:{room}";
        }
    }
}

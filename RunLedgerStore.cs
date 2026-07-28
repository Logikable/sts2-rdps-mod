using System.Text.Json;
using Godot;
using FileAccess = Godot.FileAccess;

namespace RdpsMeter;

// The saved shape of a run's rDPS breakdown: a run id (the run seed) plus one entry per combat, each holding every
// player's itemized damage. Plain records so System.Text.Json round-trips them without custom converters; amounts stay
// decimal so the saved numbers match the live ledger exactly.
internal sealed class RunLedgerDto
{
    public string RunId { get; set; } = string.Empty;
    public List<CombatEntryDto> Combats { get; set; } = new();
}

internal sealed class CombatEntryDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public List<PlayerEntryDto> Players { get; set; } = new();
}

internal sealed class PlayerEntryDto
{
    public ulong NetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<CardEntryDto> Dealt { get; set; } = new();
    public List<SourceEntryDto> Given { get; set; } = new();
    public List<SourceEntryDto> Received { get; set; } = new();
}

internal sealed class CardEntryDto
{
    public string Card { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal Buff { get; set; }
}

internal sealed class SourceEntryDto
{
    public string Effect { get; set; } = string.Empty;
    public ulong Other { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>
/// Reads and writes each run's rDPS breakdown to the game's user data, so pausing a run and returning another day keeps
/// the numbers. One JSON file per run (named after the run's seed, under a folder in user:// rather than beside the
/// read-only mod dll), because the game holds several runs in progress at once - a solo run and a co-op one - and a
/// single shared file would let whichever was played last erase the other. The run id is also stored inside the file and
/// checked on load, so the breakdown is used only for the run it belongs to whatever the file ends up named. A missing
/// or unreadable file just means "no saved breakdown", and any IO or parse error is swallowed - the meter must never
/// break a run to save a stat.
/// </summary>
internal static class RunLedgerStore
{
    private const string Folder = "user://rdps_meter";

    // Names the run whose breakdown was written last, so a launch can restore the run that was being played without
    // guessing from file timestamps. Not a .json, so the per-run pruning never sees it.
    private const string LastRunPath = $"{Folder}/last-run.txt";

    // Where the breakdown lived when the meter kept only one run's worth; adopted once, then removed.
    private const string LegacyPath = "user://rdps_meter_run.json";

    // Abandoned runs are never cleaned up by the game, so keep only the most recently written few files. Far more than
    // the handful of runs anyone has going at once, and each file is a few KB.
    private const int KeepRuns = 12;

    public static string Serialize(RunLedgerDto dto)
    {
        return JsonSerializer.Serialize(dto);
    }

    public static RunLedgerDto? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<RunLedgerDto>(json);
    }

    public static void Save(RunLedgerDto dto)
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(Folder);
            string path = PathFor(dto.RunId);
            using (FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write))
            {
                if (file == null)
                {
                    GD.PrintErr($"[RdpsMeter] Could not open {path} to save the run breakdown: {FileAccess.GetOpenError()}");
                    return;
                }

                file.StoreString(Serialize(dto));
            }

            WriteLastRunId(dto.RunId);
            DiscardLegacy(dto.RunId);
            Prune();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Failed to save the run breakdown: {ex}");
        }
    }

    /// <summary>The saved breakdown for one run, or null when that run has none.</summary>
    public static RunLedgerDto? Load(string runId)
    {
        RunLedgerDto? saved = Read(PathFor(runId));
        if (saved != null)
        {
            return saved;
        }

        RunLedgerDto? legacy = Read(LegacyPath);
        return legacy != null && legacy.RunId == runId ? legacy : null;
    }

    /// <summary>
    /// The breakdown of the run that was played last, or null when nothing has been saved yet. Used at startup so the
    /// meter comes up already showing that run rather than sitting empty until the next fight.
    ///
    /// Which run that is comes from the pointer file, not from comparing timestamps: modified times are only
    /// second-resolution, so two runs saved in the same second would order arbitrarily. The newest file is the fallback
    /// for when the pointer is missing or names a run whose breakdown has since been pruned.
    /// </summary>
    public static RunLedgerDto? LoadMostRecent()
    {
        try
        {
            if (ReadLastRunId() is string runId && Load(runId) is RunLedgerDto pointed)
            {
                return pointed;
            }

            using DirAccess? dir = DirAccess.Open(Folder);
            IEnumerable<string> newestFirst = dir == null
                ? Array.Empty<string>()
                : dir.GetFiles()
                    .Where(f => f.EndsWith(".json"))
                    .OrderByDescending(f => FileAccess.GetModifiedTime($"{Folder}/{f}"));

            // The first one that still parses. A file left half-written by a crash should cost its own run's breakdown,
            // not the startup restore, so keep walking back through the older runs.
            foreach (string name in newestFirst)
            {
                if (Read($"{Folder}/{name}") is RunLedgerDto saved)
                {
                    return saved;
                }
            }

            return Read(LegacyPath);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Failed to find the last played run's breakdown: {ex}");
            return null;
        }
    }

    /// <summary>Forgets one run's saved breakdown. For the self-test to clean up after itself.</summary>
    public static void Delete(string runId)
    {
        try
        {
            string path = PathFor(runId);
            if (FileAccess.FileExists(path))
            {
                DirAccess.RemoveAbsolute(path);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Failed to delete a saved run breakdown: {ex}");
        }
    }

    private static void WriteLastRunId(string runId)
    {
        using FileAccess? file = FileAccess.Open(LastRunPath, FileAccess.ModeFlags.Write);
        file?.StoreString(runId);
    }

    private static string? ReadLastRunId()
    {
        if (!FileAccess.FileExists(LastRunPath))
        {
            return null;
        }

        using FileAccess? file = FileAccess.Open(LastRunPath, FileAccess.ModeFlags.Read);
        string? runId = file?.GetAsText().Trim();
        return string.IsNullOrEmpty(runId) ? null : runId;
    }

    private static RunLedgerDto? Read(string path)
    {
        try
        {
            if (!FileAccess.FileExists(path))
            {
                return null;
            }

            using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            return file == null ? null : Deserialize(file.GetAsText());
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Failed to load the run breakdown (starting fresh): {ex}");
            return null;
        }
    }

    // One file per run, named after its seed. Anything that is not a plain name character is folded to '_' so the seed
    // can never walk out of the folder; two seeds could in principle fold to the same name, which costs one breakdown
    // and no wrong numbers, since the run id inside the file is what decides whether it is loaded.
    private static string PathFor(string runId)
    {
        var name = new System.Text.StringBuilder();
        foreach (char c in runId)
        {
            name.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }

        return $"{Folder}/{(name.Length == 0 ? "unknown" : name.ToString())}.json";
    }

    // The old single-file breakdown is dead weight once the run it holds has been written to its own file (and junk if
    // it no longer parses).
    private static void DiscardLegacy(string runId)
    {
        if (!FileAccess.FileExists(LegacyPath))
        {
            return;
        }

        RunLedgerDto? legacy = Read(LegacyPath);
        if (legacy == null || legacy.RunId == runId)
        {
            DirAccess.RemoveAbsolute(LegacyPath);
        }
    }

    private static void Prune()
    {
        using DirAccess? dir = DirAccess.Open(Folder);
        if (dir == null)
        {
            return;
        }

        List<string> files = dir.GetFiles().Where(f => f.EndsWith(".json")).ToList();
        if (files.Count <= KeepRuns)
        {
            return;
        }

        foreach (string name in files.OrderByDescending(f => FileAccess.GetModifiedTime($"{Folder}/{f}")).Skip(KeepRuns))
        {
            DirAccess.RemoveAbsolute($"{Folder}/{name}");
        }
    }
}

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

    // Who was in this run, once for the whole run rather than once per combat: the party is fixed for a run's lifetime,
    // and a fight the meter missed should still get coloured rows. A file written before the roster existed simply has
    // none, and those runs fall back to the neutral tint they already drew with.
    public List<RosterEntryDto> Roster { get; set; } = new();
}

/// <summary>
/// One player's identity for the whole run: enough to draw their row without a live combat to read it off.
///
/// The character is stored as its ModelId ("CHARACTER.IRONCLAD"), not as a colour and an icon path. A model id is the
/// game's own stable key and survives a re-theme; a saved colour would freeze whatever the class looked like the day the
/// file was written, and a saved texture path would break the moment the game moved its art. Everything drawn is
/// recovered from the prototype in <c>ModelDb</c> at load - the same trick <see cref="BlockSource"/> uses to name a
/// relic it only knows by type.
/// </summary>
internal sealed class RosterEntryDto
{
    public ulong NetId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Character { get; set; } = string.Empty;
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

    // The block half, added later: a file written before the Blocked meter existed simply has none of these, and the
    // empty defaults are the right answer for it - that run recorded no block, and now never will.
    public List<CardEntryDto> Blocked { get; set; } = new();
    public List<SourceEntryDto> BlockGiven { get; set; } = new();
    public List<SourceEntryDto> BlockReceived { get; set; } = new();
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

    // Abandoned runs are never cleaned up by the game, so keep only the most recently written files. This was 12, which
    // was sized for "runs anyone has in progress at once" - but the run history page reaches back over finished runs
    // too, and a pruned file is exactly the zeroes bug it used to show. Sized for browsing now rather than for
    // resuming; each file is a few KB, so the whole cap is well under a megabyte.
    private const int KeepRuns = 60;

    public static string Serialize(RunLedgerDto dto)
    {
        return JsonSerializer.Serialize(dto);
    }

    public static RunLedgerDto? Deserialize(string json)
    {
        return Normalize(JsonSerializer.Deserialize<RunLedgerDto>(json));
    }

    /// <summary>
    /// Fills in anything the file left null, so nothing downstream has to ask.
    ///
    /// A property initializer does not survive deserialization: System.Text.Json assigns what the document says, so an
    /// explicit null in the file leaves the list null rather than empty, and a null string reaches the restore path as
    /// a dictionary key. The mod never writes either, but a file it did not write - hand-edited, half-written by a
    /// crash, or produced by an older or newer shape of this DTO - can carry them, and every one of the nine loops
    /// that walk this thing would throw on the first.
    ///
    /// That matters more than it sounds, because of where the throw would land. <see cref="Load"/> is called from the
    /// meter's prefix on RunManager.SetUpSavedMultiplayer, whose one caller turns any exception into "kicked to the
    /// main menu with an internal error" - so a stray null in a saved breakdown would read to a player as the mod
    /// breaking their run on load. This file already promises the opposite in its own summary; normalizing once, here,
    /// is what makes that promise true for every consumer rather than for the ones that remembered to check.
    /// </summary>
    private static RunLedgerDto? Normalize(RunLedgerDto? dto)
    {
        if (dto == null)
        {
            return null;
        }

        dto.RunId ??= string.Empty;
        dto.Combats ??= new List<CombatEntryDto>();
        dto.Roster ??= new List<RosterEntryDto>();

        foreach (RosterEntryDto player in dto.Roster)
        {
            player.Name ??= string.Empty;
            player.Character ??= string.Empty;
        }

        foreach (CombatEntryDto combat in dto.Combats)
        {
            combat.Key ??= string.Empty;
            combat.Label ??= string.Empty;
            combat.Players ??= new List<PlayerEntryDto>();

            foreach (PlayerEntryDto player in combat.Players)
            {
                player.Name ??= string.Empty;
                player.Dealt = Cards(player.Dealt);
                player.Blocked = Cards(player.Blocked);
                player.Given = Sources(player.Given);
                player.Received = Sources(player.Received);
                player.BlockGiven = Sources(player.BlockGiven);
                player.BlockReceived = Sources(player.BlockReceived);
            }
        }

        return dto;
    }

    // Card and effect names are used as dictionary keys, so a null one is not merely a blank row - it throws.
    private static List<CardEntryDto> Cards(List<CardEntryDto>? entries)
    {
        entries ??= new List<CardEntryDto>();
        foreach (CardEntryDto entry in entries)
        {
            entry.Card ??= string.Empty;
        }

        return entries;
    }

    private static List<SourceEntryDto> Sources(List<SourceEntryDto>? entries)
    {
        entries ??= new List<SourceEntryDto>();
        foreach (SourceEntryDto entry in entries)
        {
            entry.Effect ??= string.Empty;
        }

        return entries;
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

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
/// Reads and writes the current run's rDPS breakdown to the game's user data, so pausing a run and returning another day
/// keeps the numbers. Stored as a single JSON file (not beside the read-only mod dll); a missing or unreadable file just
/// means "no saved breakdown", and any IO or parse error is swallowed - the meter must never break a run to save a stat.
/// </summary>
internal static class RunLedgerStore
{
    private const string Path = "user://rdps_meter_run.json";

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
            using FileAccess? file = FileAccess.Open(Path, FileAccess.ModeFlags.Write);
            if (file == null)
            {
                GD.PrintErr($"[RdpsMeter] Could not open {Path} to save the run breakdown: {FileAccess.GetOpenError()}");
                return;
            }

            file.StoreString(Serialize(dto));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Failed to save the run breakdown: {ex}");
        }
    }

    public static RunLedgerDto? Load()
    {
        try
        {
            if (!FileAccess.FileExists(Path))
            {
                return null;
            }

            using FileAccess? file = FileAccess.Open(Path, FileAccess.ModeFlags.Read);
            if (file == null)
            {
                return null;
            }

            return Deserialize(file.GetAsText());
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Failed to load the run breakdown (starting fresh): {ex}");
            return null;
        }
    }
}

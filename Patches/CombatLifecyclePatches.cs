using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

namespace RdpsMeter.Patches;

/// <summary>
/// Resets the ledger at the start of each combat and prints the attribution summary at the end. Both target methods
/// are async; a Harmony prefix runs at their synchronous entry, which is exactly when we want to clear (combat about
/// to begin) and report (combat ending, tallies complete).
/// </summary>
[HarmonyPatch(typeof(CombatManager))]
internal static class CombatLifecyclePatches
{
    [HarmonyPatch(nameof(CombatManager.StartCombatInternal))]
    [HarmonyPrefix]
    private static void StartCombatInternalPrefix()
    {
        AttributionPatches.ClearPending();

        // Before BeginCombat, not after: BeginCombat is what writes the run's file, so recording the party first means
        // a run that is quit after one fight still has its roster saved.
        RecordRoster();

        // Open (or, on a mid-combat save reload, reopen and wipe) this combat's tally, keyed by where the run is and
        // named after the enemies it starts with (toughest first, so a mix reads by its most notable enemy).
        RunLedger.BeginCombat(RunContext.CombatKey, StartingFightLabel());

#if RDPS_HARNESS
        // The F9 self-test drives live combat with fake players; only arm it for developer builds, never for players.
        if (DevMode.Enabled)
        {
            SelfTest.Install();
        }
#endif
    }

    [HarmonyPatch(nameof(CombatManager.EndCombatInternal))]
    [HarmonyPrefix]
    private static void EndCombatInternalPrefix()
    {
        RunLedger.EndCombat();
    }

    // Who is in this fight, filed against the run so their rows keep their class colour and icon after the combat state
    // is gone - including in a later session, where there is no live player to read the colour off at all. Only the
    // character's model id is kept; the colour and the icon are recovered from it (see CharacterVisuals).
    private static void RecordRoster()
    {
        try
        {
            CombatState? state = CombatManager.Instance?.DebugOnlyGetState();
            foreach (Player player in state?.Players ?? (IReadOnlyList<Player>)Array.Empty<Player>())
            {
                RunLedger.RecordRoster(player.NetId, PlayerIdentity.Name(player), player.Character.Id.ToString());
            }
        }
        catch (Exception ex)
        {
            // A row's colour is never worth breaking combat start over; the rows just draw in the neutral tint.
            GD.PrintErr($"[RdpsMeter] Could not record the party roster: {ex}");
        }
    }

    // The fight's name. The game names its own encounters ("Group of Slimes", "Knight Gang", "The Kin") and those read
    // far better than anything assembled from the roster, so they win; the roster is only used when a combat has no
    // encounter behind it (the debug/harness path). The state and its creatures already exist at this point
    // (StartCombatInternal iterates them right after), so the starting roster is intact; ordering by max HP puts the
    // toughest enemy first so FightLabel names a mixed fight after it.
    private static string StartingFightLabel()
    {
        try
        {
            CombatState? state = CombatManager.Instance?.DebugOnlyGetState();
            string? title = EncounterTitle(state);
            if (!string.IsNullOrWhiteSpace(title))
            {
                Trace($"[RdpsMeter] Fight named '{title}' from the encounter");
                return title;
            }

            List<string> enemies = state?.HittableEnemies
                .OrderByDescending(c => c.MaxHp)
                .Select(c => c.Name)
                .ToList() ?? new List<string>();
            string label = FightLabel.From(enemies);
            Trace($"[RdpsMeter] Fight named '{label}' from the roster [{string.Join(", ", enemies)}] - no encounter title");
            return label;
        }
        catch (Exception ex)
        {
            // A fight name is never worth breaking combat start over; fall back to a generic label.
            GD.PrintErr($"[RdpsMeter] Could not name the fight: {ex}");
            return Loc.T("combat");
        }
    }

    // Which of the two naming sources a fight got its name from - worth seeing while developing, silent in play.
    private static void Trace(string message)
    {
        if (DevMode.Enabled)
        {
            GD.Print(message);
        }
    }

    // Kept apart so a missing or unlocalized encounter title falls back to the roster rather than losing the name.
    private static string? EncounterTitle(CombatState? state)
    {
        try
        {
            return state?.Encounter?.Title.GetFormattedText();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

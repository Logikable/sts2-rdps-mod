using System.Linq;
using System.Reflection;
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
///
/// Neither is bound by name alone, because 0.110.0 rewrote CombatManager to keep its per-combat fields in a
/// CombatTurnState object and gave both methods a private overload taking one. See <see cref="LifecycleTarget"/> for
/// why picking the wrong overload is a silent failure rather than a loud one.
/// </summary>
internal static class LifecycleTarget
{
    /// <summary>
    /// The overload of <paramref name="name"/> that every path to it funnels through.
    ///
    /// Through 0.109.1 there was exactly one: a public, no-argument StartCombatInternal / EndCombatInternal. 0.110.0
    /// moved the body into a private overload taking the combat's CombatTurnState and left the old signature behind as
    /// a wrapper - but only as *one* of the callers. The ordinary end of a fight runs
    /// CheckWinCondition -> EndCombatInternal(turnState) and never touches the wrapper, so a patch left on the
    /// no-argument version would stop seeing combats end while still binding perfectly.
    ///
    /// That is the failure worth naming: the binding verifier cannot catch it. `nameof(EndCombatInternal)` still
    /// resolves on 0.110.0, so the bind reports ok and the meter simply stops closing out fights. Prefer the
    /// one-argument overload wherever it exists, and fall back to the no-argument one for the older versions the mod
    /// still supports. Resolution is by parameter count rather than by naming CombatTurnState, which does not exist as
    /// a type before 0.110.0.
    /// </summary>
    internal static MethodBase Resolve(string name)
    {
        MethodInfo[] candidates = AccessTools.GetDeclaredMethods(typeof(CombatManager))
            .Where(m => m.Name == name)
            .ToArray();

        return candidates.FirstOrDefault(m => m.GetParameters().Length == 1)
            ?? candidates.First(m => m.GetParameters().Length == 0);
    }
}

[HarmonyPatch]
internal static class CombatLifecyclePatches
{
    private static MethodBase TargetMethod()
    {
        return LifecycleTarget.Resolve("StartCombatInternal");
    }

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

/// <summary>
/// Closes out the combat's tally. A separate class only because Harmony resolves one target method per patch class,
/// and this one is chosen the same careful way - see <see cref="LifecycleTarget.Resolve"/>.
/// </summary>
[HarmonyPatch]
internal static class CombatEndPatch
{
    private static MethodBase TargetMethod()
    {
        return LifecycleTarget.Resolve("EndCombatInternal");
    }

    [HarmonyPrefix]
    private static void EndCombatInternalPrefix()
    {
        RunLedger.EndCombat();
    }
}

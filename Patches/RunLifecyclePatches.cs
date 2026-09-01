using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

namespace RdpsMeter.Patches;

/// <summary>
/// Tracks which run the meter is accounting for by hooking the four RunManager setup paths. The manager's own State
/// property became private in 0.109, but each setup method is still handed the RunState, so we capture it here (for the
/// combat-location key and the run seed) and, in the same place, tell the ledger whether this is a brand-new run (wipe
/// the breakdown) or a resumed one (reload it from disk). A prefix runs at the synchronous entry of each - before the
/// run's first combat - which is exactly when the ledger needs to be pointed at the right run.
/// </summary>
[HarmonyPatch(typeof(RunManager))]
internal static class RunLifecyclePatches
{
    [HarmonyPatch(nameof(RunManager.SetUpNewSingleplayer))]
    [HarmonyPrefix]
    private static void SetUpNewSingleplayerPrefix(RunState state)
    {
        StartNewRun(state, singleplayer: true);
    }

    [HarmonyPatch(nameof(RunManager.SetUpNewMultiplayer))]
    [HarmonyPrefix]
    private static void SetUpNewMultiplayerPrefix(RunState state)
    {
        StartNewRun(state, singleplayer: false);
    }

    [HarmonyPatch(nameof(RunManager.SetUpSavedSingleplayer))]
    [HarmonyPrefix]
    private static void SetUpSavedSingleplayerPrefix(RunState state)
    {
        ResumeRun(state, singleplayer: true);
    }

    [HarmonyPatch(nameof(RunManager.SetUpSavedMultiplayer))]
    [HarmonyPrefix]
    private static void SetUpSavedMultiplayerPrefix(RunState state)
    {
        ResumeRun(state, singleplayer: false);
    }

    private static void StartNewRun(RunState state, bool singleplayer)
    {
        Adopt(state, singleplayer);
        Guarded(() => RunLedger.StartNewRun(RunContext.RunId), "start");
    }

    private static void ResumeRun(RunState state, bool singleplayer)
    {
        Adopt(state, singleplayer);
        Guarded(() => RunLedger.ResumeRun(RunContext.RunId), "resume");
    }

    // Which run the meter is accounting for, which is the half that must happen whatever else goes wrong: without it
    // the ledger would keep writing under the previous run's id.
    private static void Adopt(RunState state, bool singleplayer)
    {
        RunContext.State = state;
        RunContext.IsSingleplayer = singleplayer;
    }

    /// <summary>
    /// Runs the ledger's side of a run setup without letting it break the run.
    ///
    /// These prefixes sit on RunManager's four setup methods, and the saved-multiplayer one is loaded inside a
    /// try/catch that answers any exception by walking the player back to the main menu with an internal error. So a
    /// meter that threw here would not lose a statistic - it would look to the player like the mod refusing to load
    /// their run, which is how the "rejoining crashes my game" report reached us in the first place. A breakdown is
    /// never worth that, exactly as a row's colour is never worth breaking combat start over.
    ///
    /// The saved data itself is normalized on read (see <see cref="RunLedgerStore"/>), so this is a backstop rather
    /// than the fix for anything known - which is why it logs loudly instead of passing over in silence.
    /// </summary>
    private static void Guarded(Action work, string what)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Could not {what} the run's breakdown - the meter will begin this run empty: {ex}");
        }
    }
}

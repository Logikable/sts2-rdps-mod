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
        StartNewRun(state);
    }

    [HarmonyPatch(nameof(RunManager.SetUpNewMultiplayer))]
    [HarmonyPrefix]
    private static void SetUpNewMultiplayerPrefix(RunState state)
    {
        StartNewRun(state);
    }

    [HarmonyPatch(nameof(RunManager.SetUpSavedSingleplayer))]
    [HarmonyPrefix]
    private static void SetUpSavedSingleplayerPrefix(RunState state)
    {
        ResumeRun(state);
    }

    [HarmonyPatch(nameof(RunManager.SetUpSavedMultiplayer))]
    [HarmonyPrefix]
    private static void SetUpSavedMultiplayerPrefix(RunState state)
    {
        ResumeRun(state);
    }

    private static void StartNewRun(RunState state)
    {
        RunContext.State = state;
        RunLedger.StartNewRun(RunContext.RunId);
    }

    private static void ResumeRun(RunState state)
    {
        RunContext.State = state;
        RunLedger.ResumeRun(RunContext.RunId);
    }
}

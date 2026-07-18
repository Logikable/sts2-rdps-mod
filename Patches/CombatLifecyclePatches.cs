using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;

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
        CombatLedger.Instance.Reset();
        SelfTest.Install();
    }

    [HarmonyPatch(nameof(CombatManager.EndCombatInternal))]
    [HarmonyPrefix]
    private static void EndCombatInternalPrefix()
    {
        CombatLedger.Instance.PrintSummary();
    }
}

using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.RunHistoryScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace RdpsMeter.Patches;

/// <summary>
/// Makes the meter follow the run history page: moving onto a fight switches the overlay to that fight's damage, so the
/// window reads as a caption on whatever map point is being looked at. The page's own entries know both the run they
/// belong to and their map point, so the focus handler is all that needs hooking - see <see cref="RunHistoryLink"/> for
/// how a map point becomes a combat.
///
/// Only focusing another map point changes what is shown; moving off one leaves it, since moving the mouse over to read
/// the numbers would otherwise take them away as it went.
/// </summary>
[HarmonyPatch(typeof(NMapPointHistoryEntry))]
internal static class RunHistoryPatches
{
    [HarmonyPatch("OnFocus")]
    [HarmonyPostfix]
    private static void OnFocusPostfix(RunHistory ____runHistory, MapPointHistoryEntry ____entry)
    {
        if (____runHistory == null || ____entry == null)
        {
            return;
        }

        if (RunHistoryLink.Locate(____runHistory, ____entry) is HistoryFight fight)
        {
            RunHistoryView.Show(fight);
        }
    }
}

/// <summary>
/// Hands the meter back to its own view when the run history page stops driving it: on close, and when the arrows page
/// to a different run, whose map points have not been looked at yet.
/// </summary>
[HarmonyPatch(typeof(NRunHistory))]
internal static class RunHistoryScreenPatches
{
    [HarmonyPatch("DisplayRun")]
    [HarmonyPostfix]
    private static void DisplayRunPostfix()
    {
        RunHistoryView.Release();
    }

    [HarmonyPatch("OnSubmenuHidden")]
    [HarmonyPostfix]
    private static void OnSubmenuHiddenPostfix()
    {
        RunHistoryView.Release();
    }
}

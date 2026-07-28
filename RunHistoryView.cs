namespace RdpsMeter;

/// <summary>
/// One fight picked out on the run history page: what to caption the meter with, and which combat in the ledger holds
/// its numbers. <see cref="Key"/> is null when the fight is not in memory - an older run than the one loaded, or a
/// fight the meter was not installed for - which is shown as an empty meter rather than someone else's damage.
/// </summary>
internal readonly record struct HistoryFight(string Caption, string? Key);

/// <summary>
/// The fight the run history page is looking at, if any. The page sets this as the player moves across the map points
/// (see <see cref="Patches.RunHistoryPatches"/>) and the overlay renders it in place of its own picked view, so the
/// meter reads as a caption on whatever the page is showing.
///
/// Deliberately plain data with no game types in it: the mapping from a hovered map point to a combat lives in
/// <see cref="RunHistoryLink"/>, which leaves this readable from the overlay and the self-test without a live screen.
/// Everything here runs on the main thread - Godot UI callbacks and the overlay's _Process - so no locking.
/// </summary>
internal static class RunHistoryView
{
    /// <summary>The fight being looked at, or null when the page is closed or nothing on it has been focused.</summary>
    public static HistoryFight? Fight { get; private set; }

    /// <summary>The page moved onto a fight. Replaces whatever was showing before.</summary>
    public static void Show(HistoryFight fight)
    {
        Fight = fight;
    }

    /// <summary>
    /// Hand the meter back to its own view: the page closed, or switched to a different run. Not called when the
    /// player merely moves off a map point - walking the mouse over to read the numbers would otherwise take them
    /// away again.
    /// </summary>
    public static void Release()
    {
        Fight = null;
    }
}

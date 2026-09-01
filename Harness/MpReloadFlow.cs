// Developer-only two-peer harness - compiled in only under -p:Harness=true (see RdpsMeter.csproj). Never ships.
#if RDPS_HARNESS
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace RdpsMeter.Harness;

/// <summary>
/// The "everyone quit and came back" half of the second bug report.
///
/// Mid-run rejoining is not implemented in this build - the game answers a reconnect with
/// <c>NotImplementedException</c> - so a player who says they disconnected and rejoined can only have meant this:
/// the party leaves, the host loads the saved co-op run from the main menu, and the others join that load lobby.
/// That path runs <c>RunManager.SetUpSavedMultiplayer</c>, which the meter prefixes to reload the run's breakdown
/// from disk, and whose one caller wraps everything in a try/catch that walks the player back to the main menu with
/// an internal error. An exception out of the meter's prefix would therefore look exactly like "the mod crashes my
/// game when I rejoin" - which is why this flow exists.
///
/// The host side is driven here rather than through the game's own <c>--fastmp=load</c> shortcut, for one reason:
/// that shortcut asks Steam for the local player id whenever Steam initialized, while a session played over ENet
/// loopback has player ids 1 and 1000. <c>RunManager.CanonicalizeSave</c> throws when the id it is given is not in
/// the save, so the shortcut reports "Failed to load multiplayer save" and never hosts. Asking the null platform for
/// the id - which is what the run was actually played under - is the whole difference.
/// </summary>
internal static class MpReloadFlow
{
    /// <summary>
    /// Loads the saved co-op run and hosts a load lobby for it. Returns false while the save is not readable, which
    /// is a real outcome worth reporting rather than an error: it means the fresh phase never left one.
    /// </summary>
    public static bool StartHostingSavedRun()
    {
        ulong localId = PlatformUtil.GetLocalPlayerId(PlatformType.None);
        ReadSaveResult<SerializableRun> save = SaveManager.Instance.LoadAndCanonicalizeMultiplayerRunSave(localId);
        if (save.SaveData == null)
        {
            MpHarness.Log($"no readable co-op save for local id {localId} ({save.Status})");
            return false;
        }

        // Reuse the submenu the game has already opened rather than pushing a second one: any --fastmp value at all
        // makes NMainMenu open it before it looks at what the value says, so by the time this runs it is on screen.
        NMultiplayerSubmenu submenu = Find<NMultiplayerSubmenu>() ?? NGame.Instance!.MainMenu.OpenMultiplayerSubmenu();
        MpHarness.Log($"hosting the saved co-op run (local id {localId}, {save.SaveData.Players.Count} player(s))");
        submenu.StartHost(save.SaveData);
        return true;
    }

    /// <summary>The load lobby screen, once whichever side's flow has pushed it, or null while it has not.</summary>
    public static NMultiplayerLoadGameScreen? LoadScreen()
    {
        return Find<NMultiplayerLoadGameScreen>();
    }

    /// <summary>
    /// The first live node of a type anywhere under the tree root. The screens here are pushed by the game's own menu
    /// stack, not by us, so there is no handle to hold onto - finding them is the only way to drive them.
    /// </summary>
    private static T? Find<T>() where T : Node
    {
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        return tree?.Root == null ? null : FindIn<T>(tree.Root);
    }

    private static T? FindIn<T>(Node node) where T : Node
    {
        if (node is T match)
        {
            return match;
        }

        foreach (Node child in node.GetChildren())
        {
            if (FindIn<T>(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
#endif

using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace RdpsMeter;

/// <summary>
/// Headless self-test driver. When an "autotest.marker" file sits next to the mod DLL, this waits for the main menu
/// to finish loading, programmatically starts a single-player run and drops straight into a combat (the same path
/// the game's own debug bootstrapper uses), runs the cross-player Vulnerable self-test, prints the ledger, and quits.
///
/// That lets the whole attribution stack be exercised by launching the game headless with the marker present - no
/// window, no clicks, no second player - and reading the resulting log. The marker gate means it never fires in
/// normal play. Every distinct outcome is logged with a grep-able sentinel (HARNESS COMPLETE / HARNESS FAILED) so a
/// launch can be judged from the log alone.
/// </summary>
internal static class AutoHarness
{
    private const string CompleteSentinel = "[RdpsMeter] === HARNESS COMPLETE ===";
    private const string FailedSentinel = "[RdpsMeter] === HARNESS FAILED ===";

    public static bool Armed()
    {
        string? dir = Path.GetDirectoryName(typeof(AutoHarness).Assembly.Location);
        return dir != null && File.Exists(Path.Combine(dir, "autotest.marker"));
    }

    public static void Install()
    {
        if (Engine.GetMainLoop() is SceneTree { Root: not null } tree)
        {
            // Install runs during mod init, while the tree root is still setting up its own children and rejects a
            // direct AddChild; defer it to the next idle frame as the engine requires.
            tree.Root.CallDeferred(Node.MethodName.AddChild, new AutoHarnessNode());
            GD.Print("[RdpsMeter] Auto-harness armed - waiting for main menu");
        }
        else
        {
            GD.PrintErr("[RdpsMeter] Auto-harness could not attach: no scene tree");
        }
    }

    public static async Task RunAsync()
    {
        try
        {
            await EnterDebugCombat();
            bool passed = await SelfTest.RunAsync();
            CombatLedger.Instance.PrintSummary();
            GD.Print(passed ? CompleteSentinel : FailedSentinel);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"{FailedSentinel} {ex}");
        }
        finally
        {
            (Engine.GetMainLoop() as SceneTree)?.Quit();
        }
    }

    /// <summary>
    /// Builds a fresh single-player Ironclad run and enters a first-act monster room, mirroring
    /// NSceneBootstrapper.StartNewRun but running inside the already-live main-menu game rather than a bootstrap scene.
    /// </summary>
    private static async Task EnterDebugCombat()
    {
        CharacterModel character = ModelDb.GetById<CharacterModel>(new ModelId("CHARACTER", "IRONCLAD"));

        // Pick a real registered monster encounter rather than hardcode an id; AllEncounters is populated at load.
        EncounterModel encounter = ModelDb.AllEncounters.First(e => e.RoomType == RoomType.Monster);
        GD.Print($"[RdpsMeter] Harness: using encounter {encounter.Id}");

        Player player = Player.CreateForNewRun(character, SaveManager.Instance.GenerateUnlockStateFromProgress(), 1uL);
        RunState runState = RunState.CreateForNewRun(
            new[] { player },
            ActModel.GetDefaultList().Select(act => act.ToMutable()).ToList(),
            new List<ModifierModel>(),
            GameMode.Standard,
            0,
            SeedHelper.GetRandomSeed());

        RunManager.Instance.SetUpNewSingleplayer(runState, shouldSave: false);
        await PreloadManager.LoadRunAssets(new[] { character });
        RunManager.Instance.Launch();
        NGame.Instance!.RootSceneContainer.SetCurrentScene(NRun.Create(runState));
        await RunManager.Instance.SetActInternal(0);
        await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, encounter.ToMutable());
    }
}

internal sealed partial class AutoHarnessNode : Node
{
    private bool _started;

    public override void _Process(double delta)
    {
        // Wait until the main menu scene is up (ModelDb, SaveManager, and RunManager are all ready by then), then
        // fire exactly once.
        if (_started || NGame.Instance?.MainMenu == null || RunManager.Instance == null)
        {
            return;
        }

        _started = true;
        _ = TaskHelper.RunSafely(AutoHarness.RunAsync());
    }
}

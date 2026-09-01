// Developer-only two-peer harness - compiled in only under -p:Harness=true (see RdpsMeter.csproj). Never ships.
#if RDPS_HARNESS
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace RdpsMeter.Harness;

/// <summary>
/// Drives one side of a scripted two-peer co-op session, so a multiplayer bug can be reproduced from two launched
/// processes and two log files instead of two people and two machines.
///
/// It reuses the game's own <c>NMultiplayerTest</c> scene rather than rebuilding the lobby: that scene already hosts
/// and joins over ENet on 127.0.0.1:33771, already implements <c>IStartRunLobbyListener</c>, and already knows how to
/// turn "everyone is ready" into a started run. Everything below drives it the way a person would - press Host, press
/// Join, pick a character, press Ready - by calling those same methods. Reflection is only because they are private;
/// nothing here reimplements them.
///
/// The session is judged from the log, never from the screen, so every state change and every outcome carries a
/// grep-able sentinel. A peer that never connects or never reaches a fight quits on a timeout rather than hanging,
/// because the launcher waits on both processes.
/// </summary>
internal static class MpHarness
{
    public const string ReadySentinel = "[RdpsMeter] === MP READY ===";
    public const string CombatSentinel = "[RdpsMeter] === MP COMBAT ===";
    public const string CompleteSentinel = "[RdpsMeter] === MP COMPLETE ===";
    public const string FailedSentinel = "[RdpsMeter] === MP FAILED ===";

    public static void Install()
    {
        if (Engine.GetMainLoop() is SceneTree { Root: not null } tree)
        {
            // Mod init runs while the tree root is still building its own children and rejects a direct AddChild;
            // defer to the next idle frame, as the engine requires.
            tree.Root.CallDeferred(Node.MethodName.AddChild, new MpHarnessNode());
            Log($"armed as {MpConfig.Role}, meter={(MpConfig.MeterEnabled ? "on" : "off")}, scenario={MpConfig.Scenario}");
        }
        else
        {
            GD.PrintErr("[RdpsMeter] MP harness could not attach: no scene tree");
        }
    }

    public static void Log(string message)
    {
        GD.Print($"[RdpsMeter] MP({MpConfig.Role}): {message}");
    }
}

/// <summary>
/// The state machine, stepped once a frame. A frame-driven machine rather than one long async method because every
/// step waits on something the game only advances between frames - a peer appearing in the lobby, a combat becoming
/// live, a card becoming playable - and because a peer that dies mid-step should leave the last state it reached in
/// the log rather than an abandoned continuation.
/// </summary>
internal sealed partial class MpHarnessNode : Node
{
    private enum Stage
    {
        WaitingForMenu,
        OpeningScene,
        Connecting,
        WaitingForParty,
        Ready,
        WaitingForCombat,
        Playing,
        Done,
    }

    private Stage _stage = Stage.WaitingForMenu;
    private double _elapsed;
    private double _stageElapsed;
    private NMultiplayerTest? _scene;
    private MpFightDriver? _fight;
    private double _lingering = -1;

    // How long a finished peer keeps running before it quits. The two peers never finish on the same frame, and the
    // one still playing needs the other to keep servicing the network: a host whose only client has vanished stops
    // resolving turns and floods the log with "Peer not connected" instead of reaching its own verdict. Lingering
    // costs nothing and makes the session's outcome independent of which side got there first.
    private const double LingerSeconds = 25.0;

    // How long to wait between join attempts, and when the next one is due. The first is deliberately not immediate:
    // the host has to get its ENet socket up, and there is no way to ask whether it has.
    private const double JoinRetryInterval = 4.0;
    private double _nextJoinAttempt = 2.0;

    public override void _Process(double delta)
    {
        _elapsed += delta;
        _stageElapsed += delta;

        if (_lingering >= 0)
        {
            _lingering += delta;
            if (_lingering > LingerSeconds)
            {
                (Engine.GetMainLoop() as SceneTree)?.Quit();
            }

            return;
        }

        if (_stage != Stage.Done && _elapsed > MpConfig.TimeoutSeconds)
        {
            Finish($"{MpHarness.FailedSentinel} timed out in stage {_stage}", failed: true);
            return;
        }

        try
        {
            Step(delta);
        }
        catch (Exception ex)
        {
            Finish($"{MpHarness.FailedSentinel} {ex}", failed: true);
        }
    }

    private void Step(double delta)
    {
        switch (_stage)
        {
            case Stage.WaitingForMenu:
                // ModelDb, SaveManager and RunManager are all up by the time the main menu is.
                if (NGame.Instance?.MainMenu != null && RunManager.Instance != null)
                {
                    Advance(Stage.OpeningScene);
                }

                break;

            case Stage.OpeningScene:
                OpenMultiplayerScene();
                break;

            case Stage.Connecting:
                Connect();
                break;

            case Stage.WaitingForParty:
                if (LobbyOf(_scene) is { } lobby && lobby.Players.Count >= 2)
                {
                    MpHarness.Log($"party of {lobby.Players.Count}");
                    Advance(Stage.Ready);
                }

                break;

            case Stage.Ready:
                DeclareReady();
                break;

            case Stage.WaitingForCombat:
                if (CombatManager.Instance is { IsInProgress: true })
                {
                    GD.Print(MpHarness.CombatSentinel);
                    _fight = new MpFightDriver();
                    Advance(Stage.Playing);
                }

                break;

            case Stage.Playing:
                if (_fight!.Step(delta) is { } outcome)
                {
                    Finish(outcome, failed: false);
                }

                break;
        }
    }

    /// <summary>
    /// Swaps the main menu out for the game's multiplayer test scene, exactly as its dev-console command does.
    /// </summary>
    private void OpenMultiplayerScene()
    {
        _scene = SceneHelper.Instantiate<NMultiplayerTest>("debug/multiplayer_test");
        NGame.Instance!.RootSceneContainer.SetCurrentScene(_scene);
        TaskHelper.RunSafely(NGame.Instance.Transition.FadeIn());
        MpHarness.Log("opened multiplayer test scene");
        Advance(Stage.Connecting);
    }

    /// <summary>
    /// Hosts, or joins.
    /// </summary>
    private void Connect()
    {
        if (MpConfig.Role == MpRole.Host)
        {
            Invoke(_scene!, "StartHost", false);
            MpHarness.Log("hosting on :33771");
            Advance(Stage.WaitingForParty);
            return;
        }

        if (LobbyOf(_scene) != null)
        {
            MpHarness.Log("joined");
            Advance(Stage.WaitingForParty);
            return;
        }

        // Retry on a timer until the host answers. JoinToHost is safe to call again after a failure - it disconnects
        // whatever it was holding first - and a refused connection leaves the scene's lobby null rather than retrying
        // on its own, so this is what makes the launch order of the two processes not matter.
        if (_stageElapsed < _nextJoinAttempt)
        {
            return;
        }

        _nextJoinAttempt = _stageElapsed + JoinRetryInterval;
        MpHarness.Log($"joining {MpConfig.HostIp}:33771 as {MpConfig.NetId}");
        var initializer = new ENetClientConnectionInitializer(MpConfig.NetId, MpConfig.HostIp, 33771);
        _ = Invoke(_scene!, "JoinToHost", initializer);
    }

    /// <summary>
    /// Picks this peer's character and presses Ready. The character goes through the scene's own paginator so the
    /// choice is broadcast to the lobby the way a click would broadcast it.
    /// </summary>
    private void DeclareReady()
    {
        object? paginator = Field(_scene!, "_characterPaginator");
        if (paginator is NMultiplayerTestCharacterPaginator page)
        {
            page.SetIndex(MpConfig.CharacterIndex);
        }

        Invoke(_scene!, "ReadyButtonPressed");
        GD.Print(MpHarness.ReadySentinel);
        Advance(Stage.WaitingForCombat);
    }

    private void Advance(Stage next)
    {
        _stage = next;
        _stageElapsed = 0;
    }

    private void Finish(string message, bool failed)
    {
        _stage = Stage.Done;
        if (failed)
        {
            GD.PrintErr(message);
        }
        else
        {
            GD.Print(message);
        }

        _lingering = 0;
    }

    private static StartRunLobby? LobbyOf(NMultiplayerTest? scene)
    {
        return scene == null ? null : Field(scene, "_lobby") as StartRunLobby;
    }

    private static object? Field(object target, string name)
    {
        return AccessTools.Field(target.GetType(), name)?.GetValue(target);
    }

    private static object? Invoke(object target, string name, params object?[] args)
    {
        MethodInfo method = AccessTools.Method(target.GetType(), name)
            ?? throw new MissingMethodException(target.GetType().Name, name);
        return method.Invoke(target, args);
    }
}
#endif

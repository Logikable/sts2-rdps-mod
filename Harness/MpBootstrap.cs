// Developer-only two-peer harness - compiled in only under -p:Harness=true (see RdpsMeter.csproj). Never ships.
#if RDPS_HARNESS
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace RdpsMeter.Harness;

/// <summary>
/// Drops the scripted session straight into a co-op fight instead of leaving it on the act map waiting for clicks.
///
/// The game's own multiplayer test scene already knows how to do this: <c>NMultiplayerTest.BeginRunAsync</c> takes a
/// short path that sets the run up and calls <c>EnterRoomDebug</c> when an <see cref="IBootstrapSettings"/> exists,
/// and the long path through the map when one does not. A shipped build has none - <c>IBootstrapSettingsSubtypes</c>
/// is generated empty - so <see cref="BootstrapSettingsUtil.Get"/> returns null and the short path is dead. Handing it
/// this type turns the short path back on.
///
/// Every peer runs this identically, so it is not itself a source of divergence: the seed, the act and the encounter
/// are fixed, and <see cref="Setup"/> deals the scenario's cards to *every* player rather than to the local one. That
/// last part matters more than it looks. The game's own bootstrap only touches the local player, which is harmless for
/// a one-machine dev loop and fatal here: each peer would deal the extra cards to a different player's deck and the
/// two runs would disagree before a card was ever played, which is the very thing the session exists to detect.
/// </summary>
internal sealed class MpBootstrapSettings : IBootstrapSettings
{
    public CharacterModel Character => ModelDb.AllCharacters.First();

    public RoomType RoomType => RoomType.Monster;

    // Deterministic across peers: the same registry in the same order on both sides. Single-enemy on purpose, so a
    // scenario can name "the enemy" without ordering questions.
    public EncounterModel Encounter => ModelDb.AllEncounters.First(e => e.RoomType == RoomType.Monster);

    public EventModel Event => null!;

    public ActModel Act => ActModel.GetDefaultList().First();

    public int Ascension => 0;

    // Nothing here is a real run and the history page is not what is under test; keep it out of the save folder.
    public bool SaveRunHistory => false;

    public string? Seed => MpConfig.Seed;

    public bool DoPreloading => true;

    public bool BootstrapInMultiplayer => true;

    public List<ModifierModel> Modifiers => new();

    /// <summary>
    /// Deals the scenario's cards. Called once per peer for that peer's own player, so it deliberately ignores the
    /// argument and deals to the whole party instead - see the class remarks for why crediting only the local player
    /// would desync the session on its own.
    /// </summary>
    public Task Setup(Player localPlayer)
    {
        try
        {
            foreach (Player player in RunManager.Instance?.DebugOnlyGetState()?.Players ?? new List<Player>())
            {
                foreach (CardModel card in MpScenarios.ExtraCardsFor(MpConfig.Scenario))
                {
                    card.Owner = player;
                    player.Deck.AddInternal(card);
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] MP harness could not deal the scenario's cards: {ex}");
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Points <see cref="BootstrapSettingsUtil.Get"/> at <see cref="MpBootstrapSettings"/>. A prefix that skips the
/// original, because the original reads a source-generated table this mod cannot add to.
///
/// Applied on both peers, metered or not: it is test scaffolding, and scaffolding that ran on only one side would be
/// the asymmetry under test rather than the meter.
/// </summary>
[HarmonyPatch(typeof(BootstrapSettingsUtil), nameof(BootstrapSettingsUtil.Get))]
internal static class MpBootstrapPatch
{
    private static bool Prepare()
    {
        return MpConfig.Active;
    }

    [HarmonyPrefix]
    private static bool Prefix(ref Type? __result)
    {
        __result = typeof(MpBootstrapSettings);
        return false;
    }
}

/// <summary>The cards a named scenario needs in every deck, built fresh so each player owns their own copies.</summary>
internal static class MpScenarios
{
    /// <summary>
    /// The card the fight driver should reach for first when it is in hand.
    ///
    /// Without this a scenario is at the mercy of the draw and of which card happens to come first in hand: the first
    /// Echo Form session ran six turns and only the client ever played one, so the peer under test - the metered one -
    /// never exercised the card the bug report is about. Naming the card makes the scenario test what it says it does.
    /// </summary>
    public static string? PreferredCardFor(string scenario)
    {
        return scenario switch
        {
            "echoform" => "EchoForm",
            _ => null,
        };
    }

    public static IEnumerable<CardModel> ExtraCardsFor(string scenario)
    {
        switch (scenario)
        {
            case "echoform":
                // Echo Form alone changes nothing observable - what it modifies is the play count of whatever is
                // played after it, so the starting deck's own Strikes are what it doubles. Three copies so one
                // reaches an opening hand rather than the scenario waiting on a shuffle.
                yield return ModelDb.Card<EchoForm>().ToMutable();
                yield return ModelDb.Card<EchoForm>().ToMutable();
                yield return ModelDb.Card<EchoForm>().ToMutable();
                break;

            case "crossbuff":
                // The meter's counterfactual engine only runs when a modifier on the hit belongs to a player other
                // than the dealer - which is why a plain co-op fight of Strikes and Defends exercises none of it, and
                // why the whole path is effectively dead in single player. Bash puts Vulnerable on the enemy and
                // Debilitate doubles what Vulnerable is worth, so with both in every deck each player is buffing hits
                // the other one lands, and every such hit runs Recompute and the VulnerableBoosts prefixes.
                yield return ModelDb.Card<Bash>().ToMutable();
                yield return ModelDb.Card<Bash>().ToMutable();
                yield return ModelDb.Card<Debilitate>().ToMutable();
                break;

            default:
                yield break;
        }
    }
}
#endif

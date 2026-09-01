// Developer-only two-peer harness - compiled in only under -p:Harness=true (see RdpsMeter.csproj). Never ships.
#if RDPS_HARNESS
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace RdpsMeter.Harness;

/// <summary>
/// Plays this peer's own player, once a co-op fight is live: spend the hand on the enemy, end the turn, repeat.
///
/// Every action goes through the same entry points the UI uses - <c>CardModel.TryManualPlay</c> and an
/// <c>EndPlayerTurnAction</c> handed to the action-queue synchronizer - so the play travels the real networked path
/// and both peers resolve it the way they would in a real game. Driving the combat commands directly, the way the
/// single-player self-test does, would prove nothing here: the whole question is what the synchronized pipeline does.
///
/// Each peer drives only its own player. That is not a simplification but the rule the game itself enforces: an action
/// is owned by the player who issued it, and a peer that tried to play its teammate's cards would be issuing actions
/// the other side never agreed to.
/// </summary>
internal sealed class MpFightDriver
{
    // How long to leave the pipeline alone between actions. The queue is synchronized across peers, so an action
    // issued while the previous one is still resolving is queued rather than lost - but pacing keeps the log readable
    // and keeps a stuck fight from filling it.
    private const double ActionInterval = 0.6;

    // Enough turns, by default, for Echo Form to be drawn, played, and to then double an attack, without letting a
    // peer that is quietly doing nothing run forever.
    private static int TurnsToPlay => MpConfig.Turns;

    /// <summary>How many turns this peer has ended, so the session can act on a turn count - see the rejoin flow.</summary>
    public int TurnsEnded => _turnsEnded;

    private double _sinceAction;
    private int _cardsPlayed;
    private int _turnsEnded;
    private bool _playedPreferred;

    /// <summary>One frame of play. Returns null while the fight is still going, or the closing log line when done.</summary>
    public string? Step(double delta)
    {
        _sinceAction += delta;

        if (CombatManager.Instance is not { IsInProgress: true })
        {
            return $"{MpHarness.CompleteSentinel} combat ended after {_cardsPlayed} card(s) over {_turnsEnded} turn(s)";
        }

        if (_turnsEnded >= TurnsToPlay)
        {
            return $"{MpHarness.CompleteSentinel} played {TurnsToPlay} turns, {_cardsPlayed} card(s)";
        }

        if (_sinceAction < ActionInterval || RunManager.Instance.ActionExecutor.IsRunning)
        {
            return null;
        }

        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        Player? me = LocalContext.GetMe(state);
        if (me?.PlayerCombatState is not { Phase: PlayerTurnPhase.Play } combat)
        {
            return null;
        }

        _sinceAction = 0;

        Creature? enemy = state?.HittableEnemies.FirstOrDefault();
        if (enemy != null && PlayOneCard(combat, enemy))
        {
            return null;
        }

        MpHarness.Log($"ending turn {combat.TurnNumber}");
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, combat.TurnNumber));
        _turnsEnded++;
        return null;
    }

    /// <summary>
    /// Plays the first card in hand that can be played, preferring the enemy as its target and falling back to no
    /// target for the self-targeted ones (Echo Form among them). Returns false when the hand has nothing playable,
    /// which is what ends the turn.
    /// </summary>
    private bool PlayOneCard(PlayerCombatState combat, Creature enemy)
    {
        foreach (CardModel card in InPreferenceOrder(combat.Hand.Cards))
        {
            Creature? target = card.CanPlayTargeting(enemy) ? enemy : (card.CanPlayTargeting(null) ? null : enemy);
            if (!card.CanPlayTargeting(target))
            {
                continue;
            }

            if (card.TryManualPlay(target))
            {
                _cardsPlayed++;
                _playedPreferred |= IsPreferred(card);
                MpHarness.Log($"played {card.TitleLocString.GetFormattedText()} ({_cardsPlayed})");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The hand, with the scenario's own card first - but only until it has been played once, after which further
    /// copies go last.
    ///
    /// Both halves are load-bearing, and each was learned from a session that proved nothing. Without the preference,
    /// the first Echo Form run went six turns and only the peer that was *not* under test ever played one. With an
    /// unconditional preference, the peer under test spent every turn's three energy on another Echo Form and never
    /// attacked - and Echo Form's whole payload is that the cards played *after* it play twice, so a scenario that
    /// only ever plays Echo Form never observes an echo at all.
    /// </summary>
    private IEnumerable<CardModel> InPreferenceOrder(IReadOnlyList<CardModel> hand)
    {
        List<CardModel> cards = hand.ToList();
        if (MpScenarios.PreferredCardFor(MpConfig.Scenario) == null)
        {
            return cards;
        }

        return _playedPreferred
            ? cards.OrderBy(IsPreferred)
            : cards.OrderByDescending(IsPreferred);
    }

    private static bool IsPreferred(CardModel card)
    {
        return card.GetType().Name == MpScenarios.PreferredCardFor(MpConfig.Scenario);
    }
}
#endif

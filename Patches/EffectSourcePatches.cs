using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter.Patches;

/// <summary>
/// Names the power or relic behind a dealer-less-card hit so the breakdown shows "Thorns" or "Bronze Scales" instead
/// of "(none)". Thorns, retaliation relics, card-triggered powers and the like deal damage through CreatureCmd.Damage
/// with the player as dealer but no card source; while they run, the game keeps them on the choice context's
/// executing-model stack, readable as LastInvolvedModel.
///
/// This patches the one core Damage overload every other overload delegates to, whose body reaches ModifyDamage with
/// no await in between - so the name recorded here is still current when ModifyDamage settles the label. It is set on
/// every dealer-less-card player hit and cleared when no power/relic is on the stack (e.g. an end-of-turn AoE power
/// the game does not push), so nothing is mislabelled by a stale entry.
/// </summary>
[HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.Damage), new[]
{
    typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp),
    typeof(Creature), typeof(CardModel), typeof(CardPlay),
})]
internal static class EffectSourcePatches
{
    [HarmonyPrefix]
    private static void Prefix(PlayerChoiceContext choiceContext, Creature? dealer, CardModel? cardSource)
    {
        // A card hit names itself; only dealer-less-card player hits need a source recovered.
        if (cardSource != null || dealer?.Player is not { } player)
        {
            return;
        }

        // Prefer the game's own executing-model stack; fall back to our supplemental stack for the end-of-turn AoE
        // powers the game does not push (Hailstorm, The Bomb).
        string? name = choiceContext.LastInvolvedModel switch
        {
            PowerModel power => power.Title.GetFormattedText(),
            RelicModel relic => relic.Title.GetFormattedText(),
            _ => ExecutingEffect.Current(player.NetId),
        };
        EffectSource.Set(player.NetId, name);
    }
}

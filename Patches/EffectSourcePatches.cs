using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
[HarmonyPatch]
internal static class EffectSourcePatches
{
    // The core multi-target Damage overload every other overload funnels into. Newer game builds append a trailing
    // CardPlay? parameter, so the overload is matched on its stable leading parameters rather than an exact type list
    // - resolving whichever shape (six or seven parameters) the running game declares.
    private static readonly Type[] LeadingParams =
    {
        typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp),
        typeof(Creature), typeof(CardModel),
    };

    private static MethodBase TargetMethod()
    {
        return AccessTools.GetDeclaredMethods(typeof(CreatureCmd))
            .First(m => m.Name == nameof(CreatureCmd.Damage) && MatchesCoreOverload(m));
    }

    private static bool MatchesCoreOverload(MethodInfo method)
    {
        ParameterInfo[] ps = method.GetParameters();
        if (ps.Length != LeadingParams.Length && ps.Length != LeadingParams.Length + 1)
        {
            return false;
        }

        return !LeadingParams.Where((t, i) => ps[i].ParameterType != t).Any();
    }

    [HarmonyPrefix]
    private static void Prefix(PlayerChoiceContext choiceContext, Creature? dealer, CardModel? cardSource)
    {
        AbstractModel? running = choiceContext.LastInvolvedModel;

        // A hit with no dealer at all credits nobody unless something says whose it is. The game's own (Poison, Demise,
        // Doom) each have a path that does; a modded power's - the Engineer's Oil - is credited to whoever applied it.
        if (dealer == null)
        {
            AttributionPatches.CaptureDealerlessPower(
                running is PowerModel power && ModdedModels.IsModded(power.GetType()) ? power : null);
            return;
        }

        AttributionPatches.CaptureDealerlessPower(null);

        // A card hit names itself; only dealer-less-card player hits need a source recovered.
        if (cardSource != null || dealer.Player is not { } player)
        {
            return;
        }

        // Prefer the game's own executing-model stack when a power, relic or orb is on top of it. Otherwise fall back to
        // our supplemental stack: the end-of-turn AoE powers the game does not push (Hailstorm, The Bomb), an orb's own
        // end-of-turn passive, which the game does not push either (see OrbPassiveSourcePatches), and whatever modded
        // model is running (see ModdedSourcePatches). A Defect orb dealing damage - Lightning and Glass on passive and
        // evoke, Dark on evoke - is pushed by OrbCmd on every other route, so it is named the same way (Frost and Plasma
        // deal no damage, so never reach here).
        //
        // Last comes any other model the game has on top - a modded kind of its own, like a Runesmith rune its command
        // pushed, or the card being played when the damage came from something the card set off and nothing closer
        // claimed it ("Turret Push" over "(none)"). Below the supplemental stack on purpose: a turret fired by a card
        // has the card on the game's stack and the turret on ours, and the turret is the nearer of the two. A potion is
        // left to PotionSource, which already names it.
        string? name = running switch
        {
            PowerModel power => power.Title.GetFormattedText(),
            RelicModel relic => relic.Title.GetFormattedText(),
            OrbModel orb => orb.Title.GetFormattedText(),
            _ => null,
        };
        name ??= ExecutingEffect.Current(player.NetId);
        if (name == null && running is not (null or PotionModel))
        {
            name = running is CardModel card ? card.TitleLocString.GetFormattedText() : ModdedModels.NameOf(running);
        }

        EffectSource.Set(player.NetId, name);
    }
}

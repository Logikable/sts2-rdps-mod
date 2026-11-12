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
    // The core Damage overload every other overload funnels into. Its shape has moved twice: newer builds appended a
    // trailing CardPlay? (and later a ModifyDamageHookType), and 0.113.0 moved the core onto a new overload taking
    // IEnumerable<DamageTargetData> - per-target hit-VFX data - leaving the IEnumerable<Creature> one as a plain
    // wrapper. AttackCommand calls the DamageTargetData overload directly, so on 0.113.0 every attack skips the
    // wrapper: binding it would still resolve and still pass the verifier, and would quietly stop naming every
    // attack-routed effect. So the overload is matched on its stable leading parameters, and the DamageTargetData one
    // wins wherever it exists. DamageTargetData is reached by name only - it does not exist on older builds.
    private static readonly Type[] LeadingParams =
    {
        typeof(PlayerChoiceContext), typeof(IEnumerable<Creature>), typeof(decimal), typeof(ValueProp),
        typeof(Creature), typeof(CardModel),
    };

    private const string TargetDataTypeName = "DamageTargetData";

    private static FieldInfo? _targetField;

    private static MethodBase TargetMethod()
    {
        List<MethodInfo> candidates = AccessTools.GetDeclaredMethods(typeof(CreatureCmd))
            .Where(m => m.Name == nameof(CreatureCmd.Damage) && MatchesCoreOverload(m))
            .ToList();
        return candidates.FirstOrDefault(m => TargetDataElement(m.GetParameters()[1].ParameterType) != null)
            ?? candidates.First();
    }

    private static bool MatchesCoreOverload(MethodInfo method)
    {
        ParameterInfo[] ps = method.GetParameters();
        if (ps.Length < LeadingParams.Length || ps.Length > LeadingParams.Length + 2)
        {
            return false;
        }

        return !LeadingParams.Where((t, i) => ps[i].ParameterType != t
            && !(i == 1 && TargetDataElement(ps[i].ParameterType) != null)).Any();
    }

    /// <summary>The DamageTargetData element type if <paramref name="type"/> is IEnumerable of it, else null.</summary>
    private static Type? TargetDataElement(Type type)
    {
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(IEnumerable<>))
        {
            return null;
        }

        Type element = type.GetGenericArguments()[0];
        return element.Name == TargetDataTypeName ? element : null;
    }

    /// <summary>
    /// The hit's targets, whichever shape the bound overload takes them in. Read from the argument array rather than a
    /// named parameter because the name differs between the shapes (targets / targetData) and Harmony refuses to bind
    /// a prefix declaring one it can't find. Nothing is written back, so the array is safe to read.
    /// </summary>
    private static IEnumerable<Creature>? TargetsOf(object? arg)
    {
        switch (arg)
        {
            case null:
                return null;
            case IEnumerable<Creature> creatures:
                return creatures;
            case System.Collections.IEnumerable data:
                var targets = new List<Creature>();
                foreach (object? entry in data)
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    _targetField ??= AccessTools.Field(entry.GetType(), "target");
                    if (_targetField?.GetValue(entry) is Creature creature)
                    {
                        targets.Add(creature);
                    }
                }

                return targets;
            default:
                return null;
        }
    }

    [HarmonyPrefix]
    private static void Prefix(
        PlayerChoiceContext choiceContext, object[] __args, Creature? dealer, CardModel? cardSource)
    {
        AbstractModel? running = choiceContext.LastInvolvedModel;

        // A hit with no dealer at all credits nobody unless something says whose it is. The game's own (Poison, Demise,
        // Doom) each have a path that does; a modded power's - the Engineer's Oil - is credited to whoever applied it.
        // A modded power that dealt on a context of its own making (ARAM: Mayhem's Burn) left the game's stack empty,
        // so it is recovered from what is running instead - never when a game power is on top, which owns its hit.
        if (dealer == null)
        {
            AttributionPatches.CaptureDealerlessPower(running switch
            {
                PowerModel power when ModdedModels.IsModded(power.GetType()) => power,
                PowerModel => null,
                _ => RunningModdedPower.WornBy(TargetsOf(__args[1])),
            });
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

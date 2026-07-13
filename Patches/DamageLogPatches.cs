using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter.Patches;

/// <summary>
/// Phase-1 scaffolding: logs every resolved damage event and every real (non-preview) damage calculation so the
/// attribution surface can be validated in-game before the rDPS engine is built on top of it.
///
/// Two hooks, matching the two halves of the game's damage flow (both called from CreatureCmd.Damage, the funnel
/// every damage instance goes through):
///
/// - Hook.ModifyDamage is the pure base-to-final calculation. Its `out modifiers` list names every power/relic that
///   changed the number, and each PowerModel carries the Creature that applied it - together, everything rDPS
///   attribution needs. Card previews call this constantly with previewMode != None, so those are filtered out.
///
/// - Hook.AfterDamageGiven fires once per hit after block, with the settled DamageResult (UnblockedDamage is the HP
///   actually lost) plus dealer, target, and source card in one place.
/// </summary>
[HarmonyPatch(typeof(Hook))]
internal static class DamageLogPatches
{
    [HarmonyPatch(nameof(Hook.ModifyDamage))]
    [HarmonyPostfix]
    private static void ModifyDamagePostfix(
        Creature? target,
        Creature? dealer,
        decimal damage,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode,
        IEnumerable<AbstractModel> modifiers,
        decimal __result)
    {
        if (previewMode != CardPreviewMode.None || modifyDamageHookType != ModifyDamageHookType.All)
        {
            return;
        }

        string modifierList = string.Join(", ", modifiers.Select(Describe));
        GD.Print($"[RdpsMeter] calc: {LogName(dealer)} -> {LogName(target)} {damage} => {__result}"
            + (modifierList.Length > 0 ? $" [{modifierList}]" : ""));
    }

    [HarmonyPatch(nameof(Hook.AfterDamageGiven))]
    [HarmonyPrefix]
    private static void AfterDamageGivenPrefix(
        Creature? dealer,
        DamageResult results,
        Creature target,
        CardModel? cardSource)
    {
        GD.Print($"[RdpsMeter] hit: {LogName(dealer)} -> {LogName(target)}"
            + $" unblocked={results.UnblockedDamage} blocked={results.BlockedDamage}"
            + $" overkill={results.OverkillDamage} killed={results.WasTargetKilled}"
            + $" card={cardSource?.Id.ToString() ?? "none"} dealerNetId={DealerNetId(dealer, cardSource)}");
    }

    /// <summary>
    /// The stable multiplayer identity of the player behind a hit. The dealer creature is preferred; the card's
    /// owner covers hits where the dealing creature isn't the player itself (e.g. a pet). Monster hits have no
    /// player and report "-".
    /// </summary>
    private static string DealerNetId(Creature? dealer, CardModel? cardSource)
    {
        ulong? netId = dealer?.Player?.NetId ?? cardSource?.Owner?.NetId;
        return netId?.ToString() ?? "-";
    }

    private static string Describe(AbstractModel modifier)
    {
        if (modifier is PowerModel power)
        {
            string applier = power.Applier is null ? "?" : LogName(power.Applier);
            return $"{modifier.GetType().Name}(applier={applier})";
        }

        return modifier.GetType().Name;
    }

    private static string LogName(Creature? creature)
    {
        return creature?.LogName ?? "none";
    }
}

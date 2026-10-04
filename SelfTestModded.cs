// Developer-only, like the rest of SelfTest (see the note at the top of SelfTest.cs).
#if RDPS_HARNESS
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter;

/// <summary>
/// Scenarios against other mods' real models, for the generic paths that name and credit what the meter was never
/// written for (see <see cref="Patches.ModdedSourcePatches"/>). Each is reached by type name only - the meter has no
/// build dependency on any of these mods - and passes as skipped when its mod is not loaded, so the harness still runs
/// clean on a game with none of them installed.
///
/// The Engineer and Runesmith are the mods the generic paths were built against: between them they cover a modded orb
/// (the turret), a model kind a mod invented (the rune) and a modded power that deals damage with no dealer (Oil).
/// </summary>
internal static partial class SelfTest
{
    private static async Task<bool> ModdedScenarios(NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        bool all = true;
        all &= await ModdedTurretScenario(ctx, dealer, enemy);
        all &= await ModdedRuneScenario(ctx, dealer, enemy);
        all &= await ModdedOilScenario(ctx, dealer, enemy, applier2);
        return all;
    }

    /// <summary>
    /// A turret fired straight from a card, the way Turret Push fires every turret: the game has pushed the card, and the
    /// turret's Fire is a plain method of the mod's own that the game knows nothing about. The hit must read as the
    /// turret - the nearer of the two - not as the card, and not as "(none)".
    /// </summary>
    private static async Task<bool> ModdedTurretScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        const string scenario = "Modded orb (Engineer turret)";
        Type? type = AccessTools.TypeByName("TheEngineer.TheEngineerCode.Orbs.TurretOrb");
        MethodInfo? fire = type == null ? null : AccessTools.Method(type, "Fire");
        if (type == null || fire == null)
        {
            return Skipped(scenario, "The Engineer is not loaded");
        }

        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        var turret = (OrbModel)ModelDb.GetById<OrbModel>(ModelDb.GetId(type)).MutableClone();
        turret.Owner = dealer.Player!;
        string expected = turret.Title.GetFormattedText();
        decimal passive = Math.Truncate(turret.PassiveVal);
        int hpBefore = enemy.CurrentHp;

        CardModel card = ModelDb.AllCards.First();
        ctx.PushModel(card);
        try
        {
            await (Task)fire.Invoke(turret, new object?[] { ctx, enemy })!;
        }
        finally
        {
            ctx.PopModel(card);
        }

        GD.Print($"[RdpsMeter] Self-test: turret title = \"{expected}\", passive = {passive}, card on stack = \"{card.TitleLocString.GetFormattedText()}\"");
        CombatLedger l = CombatLedger.Current;
        return Report(scenario,
            Expect("the turret hit", hpBefore - enemy.CurrentHp, passive),
            Expect("named after the turret", l.DealtWith(you, expected), passive),
            Expect("not after the card", l.DealtWith(you, card.TitleLocString.GetFormattedText()), 0m),
            Expect("nothing left unnamed", l.DealtWith(you, NoCard), 0m));
    }

    /// <summary>
    /// A rune breaking - Runesmith's own kind of model, derived from AbstractModel directly rather than from any of the
    /// game's kinds. Driven by calling Break with nothing pushed, which is how the end-of-turn route reaches a rune's
    /// passive: the mod's rune queue calls it the way the game's orb queue calls an orb's, and pushes nothing either.
    /// </summary>
    private static async Task<bool> ModdedRuneScenario(NoOpChoiceContext ctx, Creature dealer, Creature enemy)
    {
        const string scenario = "Modded model kind (Runesmith rune)";
        Type? type = AccessTools.TypeByName("Runesmith2.Runesmith2Code.Models.Runes.FlammaRune");
        if (type == null)
        {
            return Skipped(scenario, "The Runesmith is not loaded");
        }

        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;

        AbstractModel canonical = ModelDb.GetById<AbstractModel>(ModelDb.GetId(type));
        var rune = (AbstractModel)AccessTools.Method(type, "ToMutable").Invoke(canonical, null)!;
        AccessTools.Method(type, "TransferOwner").Invoke(rune, new object[] { dealer.Player! });
        string expected = ModdedModels.NameOf(rune);

        await (Task)AccessTools.Method(type, "Break").Invoke(rune, new object[] { ctx })!;

        GD.Print($"[RdpsMeter] Self-test: rune name = \"{expected}\", dealt = {CombatLedger.Current.DealtWith(you, expected)}");
        CombatLedger l = CombatLedger.Current;
        return Report(scenario,
            Expect("named after the rune", l.DealtWith(you, expected) > 0m ? 1m : 0m, 1m),
            Expect("nothing left unnamed", l.DealtWith(you, NoCard), 0m));
    }

    /// <summary>
    /// A teammate's Oil on the enemy, set off by the dealer's attack. Oil burns the enemy with no dealer at all - nobody's
    /// damage, as the game books it - so it is credited to whoever applied it, the way the game's own Poison is.
    ///
    /// The burn lands inside the attack that sets it off, on the same enemy, before that attack has settled. That is the
    /// case that keeps the two hits' attributions apart: the dealer's own hit must still be booked to the dealer, in
    /// full, not swapped for the burn's.
    /// </summary>
    private static async Task<bool> ModdedOilScenario(
        NoOpChoiceContext ctx, Creature dealer, Creature enemy, Creature applier2)
    {
        const string scenario = "Modded dealer-less power (Engineer Oil)";
        Type? type = AccessTools.TypeByName("TheEngineer.TheEngineerCode.Powers.OilPower");
        if (type == null)
        {
            return Skipped(scenario, "The Engineer is not loaded");
        }

        await Prep(dealer, enemy);
        ulong you = dealer.Player!.NetId;
        ulong teammate = applier2.Player!.NetId;

        MethodInfo apply = typeof(PowerCmd).GetMethods()
            .First(m => m.Name == nameof(PowerCmd.Apply) && m.IsGenericMethodDefinition
                && m.GetParameters() is { Length: 6 } ps && ps[1].ParameterType == typeof(Creature))
            .MakeGenericMethod(type);
        await (Task)apply.Invoke(null, new object?[] { ctx, enemy, 3m, applier2, null, false })!;

        PowerModel? oil = enemy.Powers.FirstOrDefault(p => p.GetType() == type);
        LogShares("Oil", oil);
        string expected = oil != null ? ModdedModels.NameOf(oil) : "Oil";
        int hpBefore = enemy.CurrentHp;

        await HarnessDamage(ctx, new[] { enemy }, 6m, DamageProps.card, dealer, null, null);

        CombatLedger l = CombatLedger.Current;
        bool ok = Report(scenario,
            Expect("the enemy took both", hpBefore - enemy.CurrentHp, 9m),
            Expect("the burn is the teammate's", l.DealtWith(teammate, expected), 3m),
            Expect("the attack is still the dealer's", l.DealtWith(you, NoCard), 6m),
            Expect("the dealer took none of the burn", l.DealtWith(you, expected), 0m));

        // Oil leaves Residue behind; neither is a power the shared Prep knows to clear.
        foreach (PowerModel power in enemy.Powers.Where(p => ModdedModels.IsModded(p.GetType())).ToList())
        {
            await PowerCmd.Remove(power);
        }

        return ok;
    }

    private static bool Skipped(string scenario, string why)
    {
        GD.Print($"[RdpsMeter] Scenario '{scenario}': PASS (skipped - {why})");
        return true;
    }
}
#endif

using System.Diagnostics;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter.Patches;

/// <summary>
/// Books what a relic takes off its own owner's HP loss onto the Blocked meter, named after the relic: Tungsten Rod's
/// one point a hit, Hades Ancients' Hot Pot's three, Beating Remnant's per-turn cap.
///
/// It is mitigation for the reason block is - damage that was coming for you and did not arrive - and it is invisible
/// to everything else on that meter. Block is booked where it is spent, debuffs where the hit is modified; these act
/// one step later, in <c>Hook.ModifyHpLost</c>, on what is left after block. So a Tungsten Rod wearer's 1 per hit
/// simply never showed up anywhere.
///
/// The patch is a shape, not a list. Every override of the four <c>ModifyHpLost*</c> hooks declared on any relic, the
/// game's or another mod's, gets a postfix that compares what came in with what went out. That is the game's own
/// arithmetic read back rather than reproduced, so a relic that changes its number, or a mod's relic nobody here has
/// heard of, books exactly what it did. Only the owner's own HP counts: The Boot raises the floor on its owner's
/// <em>outgoing</em> hits, and Zeus's Air Quality does the same - both act on somebody else's HP and are skipped by the
/// target test alone, not by name.
///
/// Truncated, both sides, because the game truncates the HP it takes - the same rule the debuff mitigation uses, and the
/// same test <c>Hook.ModifyHpLost</c> applies to decide whether a model modified anything at all.
///
/// Damage a player deals themselves (Offering, Bloodletting and the rest of the self-harm cards) is left out: a relic
/// making your own card cheaper is not damage that was coming for you. A teammate's hit on you is left out on the
/// same grounds.
///
/// Applied with the late scan in <see cref="ModdedSourcePatches"/>, because a mod's relic types are not loaded yet
/// when the meter's initializer runs. Hades Ancients' own <c>IModifyHpLost…Final</c> interfaces (Defensive Posture,
/// Forge Armor) are a hook of that mod's invention and are not covered.
/// </summary>
internal static class HpLossReliefPatches
{
    private static readonly string[] HookNames =
    {
        nameof(AbstractModel.ModifyHpLostBeforeOsty),
        nameof(AbstractModel.ModifyHpLostBeforeOstyLate),
        nameof(AbstractModel.ModifyHpLostAfterOsty),
        nameof(AbstractModel.ModifyHpLostAfterOstyLate),
    };

    private static bool _applied;

    public static void Apply(Harmony harmony)
    {
        if (_applied)
        {
            return;
        }

        _applied = true;
        var timer = Stopwatch.StartNew();
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(HpLossReliefPatches), nameof(Postfix)));
        int patched = 0;
        foreach (MethodInfo method in Targets())
        {
            try
            {
                harmony.Patch(method, postfix: postfix);
                patched++;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[RdpsMeter] Could not watch {method.DeclaringType?.FullName}.{method.Name}: {ex.Message}");
            }
        }

        GD.Print($"[RdpsMeter] Watching {patched} relic HP-loss reductions in {timer.ElapsedMilliseconds} ms");
    }

    private static IEnumerable<MethodInfo> Targets()
    {
        const BindingFlags declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
        Assembly game = typeof(AbstractModel).Assembly;
        string gameName = game.GetName().Name!;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
            {
                continue;
            }

            if (assembly != game)
            {
                bool references;
                try
                {
                    references = assembly.GetReferencedAssemblies().Any(r => r.Name == gameName);
                }
                catch (Exception)
                {
                    references = false;
                }

                if (!references)
                {
                    continue;
                }
            }

            foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
            {
                if (!typeof(RelicModel).IsAssignableFrom(type) || type.ContainsGenericParameters)
                {
                    continue;
                }

                foreach (string name in HookNames)
                {
                    MethodInfo? method;
                    try
                    {
                        method = type.GetMethods(declared).FirstOrDefault(m => m.Name == name);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (method is { IsAbstract: false } && method.GetMethodBody() != null)
                    {
                        yield return method;
                    }
                }
            }
        }
    }

    private static void Postfix(RelicModel __instance, Creature target, decimal amount, Creature? dealer, decimal __result)
    {
        try
        {
            decimal relieved = decimal.Truncate(amount) - decimal.Truncate(__result);
            if (relieved <= 0m
                || CombatManager.Instance is not { IsInProgress: true }
                || __instance.Owner is not { } owner
                || !ReferenceEquals(owner.Creature, target)
                || dealer?.Player != null
                || dealer?.PetOwner != null)
            {
                return;
            }

            string name = __instance.Title.GetFormattedText();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = __instance.Id.Entry;
            }

            CombatLedger.Name(owner.NetId, PlayerIdentity.Name(owner));
            CombatLedger.RecordBlock(owner.NetId, new[] { new BlockStrand(owner.NetId, name, relieved) });
        }
        catch (Exception ex)
        {
            // A postfix that throws replaces the relic's answer with an exception mid-hit; a meter row is never worth it.
            GD.PrintErr($"[RdpsMeter] HP-loss relief on {__instance.GetType().Name}: {ex.Message}");
        }
    }
}

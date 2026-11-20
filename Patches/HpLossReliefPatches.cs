using System.Diagnostics;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.ValueProps;

namespace RdpsMeter.Patches;

/// <summary>
/// Books what a relic or power takes off its wearer's HP loss onto the Blocked meter: Tungsten Rod's one point a hit,
/// Buffer's whole hit, Beating Remnant's per-turn cap, and from Hades Ancients Hot Pot, Defensive Posture and Forge
/// Armor.
///
/// It is mitigation for the reason block is - damage that was coming for you and did not arrive - and it is invisible
/// to everything else on that meter. Block is booked where it is spent, debuffs where the hit is modified; these act
/// one step later, in <c>Hook.ModifyHpLost</c>, on what is left after block.
///
/// The patch is a shape, not a list. Every method named <c>ModifyHpLost…</c> with the hook's signature, declared on
/// any relic or power of the game's or another mod's, gets a postfix that compares what came in with what went out.
/// The name rather than the override is what is matched, because Hades Ancients runs its own
/// <c>IModifyHpLost…Final</c> hooks from a postfix on <c>Hook.ModifyHpLost</c> - a dispatcher this meter never sees -
/// and its methods follow the game's naming. That is the game's (or the mod's) arithmetic read back rather than
/// reproduced, so a model nobody here has heard of books exactly what it did.
///
/// Only the wearer's own HP counts: The Boot raises the floor on its owner's <em>outgoing</em> hits, Air Quality does
/// the same, and an enemy's Intangible or Slippery shrinks a player's hit on it - all skipped by the target test,
/// not by name. A relic is its owner's; a power is credited to whoever applied it, by the same shares the damage side
/// uses, falling back to the wearer.
///
/// Truncated, both sides, because the game truncates the HP it takes - the same test <c>Hook.ModifyHpLost</c> applies
/// to decide whether a model modified anything at all. Damage a player deals themselves (Offering, Bloodletting) and a
/// teammate's hit are left out: a relic making your own card cheaper is not damage that was coming for you.
///
/// Intangible also caps the hit itself, before block; that part is a damage modifier and is credited by
/// <see cref="DebuffMitigation"/>. What reaches the HP-loss arm afterwards is already 1, so the two never overlap.
///
/// Applied with the late scan in <see cref="ModdedSourcePatches"/>, because a mod's types are not loaded yet when the
/// meter's initializer runs.
/// </summary>
internal static class HpLossReliefPatches
{
    private const string HookPrefix = "ModifyHpLost";

    private static readonly Type[] HookSignature =
    {
        typeof(Creature), typeof(decimal), typeof(ValueProp), typeof(Creature), typeof(CardModel),
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

        GD.Print($"[RdpsMeter] Watching {patched} HP-loss reductions in {timer.ElapsedMilliseconds} ms");
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
                if (!(typeof(RelicModel).IsAssignableFrom(type) || typeof(PowerModel).IsAssignableFrom(type))
                    || type.ContainsGenericParameters)
                {
                    continue;
                }

                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(declared);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (MethodInfo method in methods)
                {
                    if (method.Name.StartsWith(HookPrefix, StringComparison.Ordinal)
                        && method.ReturnType == typeof(decimal)
                        && !method.IsAbstract
                        && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(HookSignature)
                        && method.GetMethodBody() != null)
                    {
                        yield return method;
                    }
                }
            }
        }
    }

    // Parameters bound by position (__0, __1, __3) rather than name: a mod's implementation may name them differently.
    private static void Postfix(AbstractModel __instance, Creature __0, decimal __1, Creature? __3, decimal __result)
    {
        Creature target = __0;
        Creature? dealer = __3;
        try
        {
            decimal relieved = decimal.Truncate(__1) - decimal.Truncate(__result);
            if (relieved <= 0m
                || CombatManager.Instance is not { IsInProgress: true }
                || target?.Player is not { } wearer
                || dealer?.Player != null
                || dealer?.PetOwner != null)
            {
                return;
            }

            IReadOnlyList<BlockStrand>? strands = __instance switch
            {
                RelicModel relic when ReferenceEquals(relic.Owner?.Creature, target)
                    => new[] { new BlockStrand(wearer.NetId, RelicName(relic), relieved) },
                PowerModel power when ReferenceEquals(power.Owner, target)
                    => PowerStrands(power, wearer.NetId, relieved),
                _ => null,
            };
            if (strands == null)
            {
                return;
            }

            CombatLedger.Name(wearer.NetId, PlayerIdentity.Name(wearer));
            foreach (BlockStrand strand in strands)
            {
                CombatLedger.Name(strand.OwnerNetId, PlayerIdentity.Name(strand.OwnerNetId));
            }

            CombatLedger.RecordBlock(wearer.NetId, strands);
        }
        catch (Exception ex)
        {
            // A postfix that throws replaces the model's answer with an exception mid-hit; a meter row is never worth it.
            GD.PrintErr($"[RdpsMeter] HP-loss relief on {__instance.GetType().Name}: {ex.Message}");
        }
    }

    private static string RelicName(RelicModel relic)
    {
        string name = relic.Title.GetFormattedText();
        return string.IsNullOrWhiteSpace(name) ? relic.Id.Entry : name;
    }

    /// <summary>
    /// A power's relief goes to whoever applied it, by the shares the damage side credits buffs with - a teammate's
    /// Buffer reads as given and received - and stored under the power's key, which the breakdown translates when it
    /// draws. With no player applier on record it is the wearer's.
    /// </summary>
    private static IReadOnlyList<BlockStrand> PowerStrands(PowerModel power, ulong wearerNetId, decimal relieved)
    {
        IReadOnlyList<(ulong NetId, string Effect, decimal Fraction)>? shares = AttributionEngine.NamedShares(power);
        if (shares == null || shares.Count == 0)
        {
            return new[] { new BlockStrand(wearerNetId, AttributionEngine.EffectName(power), relieved) };
        }

        return shares.Select(s => new BlockStrand(s.NetId, s.Effect, relieved * s.Fraction)).ToList();
    }
}

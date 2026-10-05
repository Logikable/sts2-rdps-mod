using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Modding;

namespace RdpsMeter.Patches;

/// <summary>
/// Names the damage other mods' models deal - a character mod's turrets, runes and powers - the way the hand-kept
/// lists in <see cref="UnpushedSourcePatches"/> name the game's own, without the meter knowing any of them.
///
/// Those lists work because the game's models are known in advance: a decompile says which hooks deal damage with
/// nothing pushed, and each is patched by name. A mod's models are not known in advance, and they hit the same two
/// gaps the game's do - a hook the game dispatches without pushing the model (an orb's end-of-turn passive, which the
/// Engineer's turrets fire on), or a model kind the mod invented whose own command pushes it but which no part of the
/// meter recognises (Runesmith's runes) - plus a third of their own: a helper the model calls directly from a card
/// (Turret Push firing every turret), where the game has pushed the card and nothing names the turret.
///
/// So the patch is broad rather than listed: every Task-returning instance method declared on a modded model pushes
/// that model onto <see cref="ExecutingEffect"/> for its span. ExecutingEffect is only ever a fallback - the game's own
/// stack outranks it whenever a power, relic or orb is on top - so a push around a hook that deals no damage, which is
/// nearly all of them, costs an entry on a stack and is never read. The name is resolved only if a hit asks.
///
/// Cards are left out. The game pushes a card for the whole of its play, a card's damage carries itself as the card
/// source and never reaches EffectSource, and the card is the one model kind a mod has hundreds of.
///
/// Applied after every mod has loaded rather than with the meter's other patches: mods load in order, and the meter's
/// initializer runs before the mods that come after it, whose types are not there to be found yet.
/// </summary>
internal static class ModdedSourcePatches
{
    private static bool _applied;

    /// <summary>
    /// Queues the scan for once mod loading has finished. The game's own loader is one synchronous loop, so the next
    /// frame is already past it - but Mod Launch Manager takes over and loads the rest one mod per frame, holding
    /// <c>ModManager.State</c> at None until the last, so the check is re-asked every frame until it clears.
    ///
    /// Each retry waits for the next <em>frame</em>, never a CallDeferred. Godot runs a call deferred from inside a
    /// deferred call in the same flush, so a CallDeferred retry spins without ever letting a frame pass - the state
    /// can't change, the message queue fills, and the game dies with an access violation on startup. That shipped in
    /// 0.1.31 and crashed every player using Mod Launch Manager.
    /// </summary>
    public static void ApplyWhenModsLoaded(Harmony harmony)
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            GD.PrintErr("[RdpsMeter] No scene tree to wait on; other mods' models will not be named");
            return;
        }

        tree.Connect(
            SceneTree.SignalName.ProcessFrame,
            Callable.From(() => ApplyOrRequeue(harmony)),
            (uint)GodotObject.ConnectFlags.OneShot);
    }

    private static void ApplyOrRequeue(Harmony harmony)
    {
        if (ModManager.State == ModManagerState.None)
        {
            ApplyWhenModsLoaded(harmony);
            return;
        }

        if (_applied)
        {
            return;
        }

        _applied = true;
        var timer = Stopwatch.StartNew();
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(ModdedSourcePatches), nameof(Prefix)));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(ModdedSourcePatches), nameof(Postfix)));
        var finalizer = new HarmonyMethod(AccessTools.Method(typeof(ModdedSourcePatches), nameof(Finalizer)));
        int patched = 0;
        int failed = 0;
        foreach (MethodInfo method in Targets())
        {
            try
            {
                harmony.Patch(method, prefix, postfix, finalizer: finalizer);
                patched++;
            }
            catch (Exception ex)
            {
                // One method Harmony cannot patch costs that method's name and nothing else.
                failed++;
                GD.PrintErr($"[RdpsMeter] Could not name {method.DeclaringType?.FullName}.{method.Name}: {ex.Message}");
            }
        }

        GD.Print($"[RdpsMeter] Named {patched} modded model methods ({failed} skipped) in {timer.ElapsedMilliseconds} ms");
    }

    private static IEnumerable<MethodInfo> Targets()
    {
        const BindingFlags declared =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic
                || !ModdedModels.IsModded(assembly)
                || !References(assembly, typeof(AbstractModel).Assembly))
            {
                continue;
            }

            foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
            {
                if (!typeof(AbstractModel).IsAssignableFrom(type)
                    || typeof(CardModel).IsAssignableFrom(type)
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
                    if (method.ReturnType == typeof(Task)
                        && !method.IsAbstract
                        && !method.ContainsGenericParameters
                        && method.GetMethodBody() != null)
                    {
                        yield return method;
                    }
                }
            }
        }
    }

    private static bool References(Assembly assembly, Assembly target)
    {
        string name = target.GetName().Name!;
        try
        {
            return assembly.GetReferencedAssemblies().Any(r => r.Name == name);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Prefix(AbstractModel __instance, out ulong? __state)
    {
        __state = null;
        if (ModdedModels.OwnerOf(__instance)?.NetId is ulong netId)
        {
            __state = netId;
            ExecutingEffect.Push(netId, __instance);
        }
    }

    private static void Postfix(ulong? __state, ref Task __result)
    {
        if (__state is not ulong netId)
        {
            return;
        }

        if (__result != null)
        {
            __result = UnpushedSourcePatches.PopAfter(__result, netId);
        }
        else
        {
            ExecutingEffect.Pop(netId);
        }
    }

    /// <summary>
    /// The pop for a method that threw before handing back a Task - a plain (non-async) one, since an async method's
    /// exceptions travel in its Task. The postfix never runs then, and the push would otherwise stand until the
    /// fight ends, naming every later unnamed hit of that player's after a model that is no longer running.
    /// </summary>
    private static Exception? Finalizer(Exception? __exception, ulong? __state)
    {
        if (__exception != null && __state is ulong netId)
        {
            ExecutingEffect.Pop(netId);
        }

        return __exception;
    }
}

using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace RdpsMeter;

[ModInitializer("Initialize")]
public static class Mod
{
    public static void Initialize()
    {
        ApplyPatches();
        GD.Print("[RdpsMeter] Initialized");

        if (DevMode.Enabled)
        {
            AutoHarness.Install();
        }
        else
        {
            RdpsOverlay.Install();
        }
    }

    /// <summary>
    /// Patches each [HarmonyPatch]-annotated class individually rather than calling Harmony.PatchAll, which applies
    /// every patch class in one pass and aborts entirely - taking every feature down - the moment any single one
    /// fails (e.g. a game update changes a target method's signature). Isolating each class means one broken hook
    /// only disables that feature, and the log clearly identifies which patch broke.
    /// </summary>
    private static void ApplyPatches()
    {
        var harmony = new Harmony("com.rdpsmeter.sts2");

        foreach (Type type in LoadableTypes(typeof(Mod).Assembly))
        {
            if (!Attribute.IsDefined(type, typeof(HarmonyPatch)))
            {
                continue;
            }

            // Diagnostic-only patches (per-hit logging, drift checks) must never hook a shipped build; skip them
            // entirely so release carries no per-hit cost.
            if (Attribute.IsDefined(type, typeof(DevOnlyPatchAttribute)) && !DevMode.Enabled)
            {
                continue;
            }

            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[RdpsMeter] Failed to apply patch '{type.Name}' - that feature will be disabled, but the rest of the mod will continue working: {ex}");
            }
        }
    }

    /// <summary>
    /// The assembly's types, minus any that fail to load. A dev-only helper built against a newer game version (its
    /// base class changed abstract members, say) makes the plain GetTypes() throw ReflectionTypeLoadException, which
    /// would take the whole mod down at startup before a single patch is applied. Dropping the unloadable types lets
    /// the mod keep running on an older game build - the same isolate-failures stance ApplyPatches takes per patch.
    /// </summary>
    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
    }
}

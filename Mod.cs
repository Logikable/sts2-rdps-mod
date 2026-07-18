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

        if (AutoHarness.Armed())
        {
            AutoHarness.Install();
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

        foreach (Type type in typeof(Mod).Assembly.GetTypes())
        {
            if (!Attribute.IsDefined(type, typeof(HarmonyPatch)))
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
}

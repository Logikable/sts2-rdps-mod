// Offline check that every Harmony patch in the shipped mod still binds against a given game assembly, without
// launching the game. Harmony resolves patch targets and their parameters by name at load time, so a game update that
// renames a method, changes an overload, adds a parameter, or removes a type silently breaks a patch - a failure the
// self-test harness only catches on the one game version it can run on. This applies the real patches against any
// sts2.dll, reproducing exactly the binding the game's mod loader performs, so both the current and older game
// versions can be checked from the two reference assemblies alone.
//
// It mirrors Mod.ApplyPatches: load the assembly, drop any type that will not load (the game's loader would otherwise
// throw and take the whole mod down), then apply each [HarmonyPatch] class in isolation - skipping the dev-only ones a
// shipped build never applies. A patch that Prepare()-gates itself out on a version is reported as skipped, not
// failed; only a thrown exception is a real break.

using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: BindingVerifier <RdpsMeter.dll> <sts2.dll> <lib-dir>");
    return 64;
}

string modPath = Path.GetFullPath(args[0]);
string sts2Path = Path.GetFullPath(args[1]);
string libDir = Path.GetFullPath(args[2]);

// The mod references sts2, GodotSharp and 0Harmony; resolve them to the assemblies under test rather than whatever the
// verifier was built against, so the mod is bound against this specific game version.
Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase)
{
    ["sts2"] = sts2Path,
    ["GodotSharp"] = Path.Combine(libDir, "GodotSharp.dll"),
    ["0Harmony"] = Path.Combine(libDir, "0Harmony.dll"),
};

AssemblyLoadContext.Default.Resolving += (context, name) =>
    name.Name is { } simpleName && byName.TryGetValue(simpleName, out string? path) && File.Exists(path)
        ? context.LoadFromAssemblyPath(path)
        : null;

Console.WriteLine($"Verifying {Path.GetFileName(modPath)} against {Path.GetFileName(sts2Path)}");

Assembly mod = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);

Type[] types;
try
{
    types = mod.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
    // The game's loader reflects over the whole assembly to find the entry point; a single type that cannot load on
    // this version makes that throw before any patch runs, so the mod fails to load entirely.
    Console.WriteLine("FAIL  a type in the assembly will not load on this game version:");
    foreach (string message in ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct())
    {
        Console.WriteLine($"        {message}");
    }

    return 1;
}

Type harmonyPatch = typeof(HarmonyPatch);
Type? devOnly = mod.GetType("RdpsMeter.DevOnlyPatchAttribute");

Harmony harmony = new("rdpsmeter.binding-verifier");
int bound = 0;
int skipped = 0;
int failed = 0;

foreach (Type type in types)
{
    if (!Attribute.IsDefined(type, harmonyPatch))
    {
        continue;
    }

    // Dev-only patches are gated behind a marker file the shipped build never carries, so it never applies them; a
    // verifier that did would flag failures that can never reach a player.
    if (devOnly != null && Attribute.IsDefined(type, devOnly))
    {
        continue;
    }

    try
    {
        List<MethodInfo> patched = harmony.CreateClassProcessor(type).Patch();
        if (patched.Count == 0)
        {
            Console.WriteLine($"  skip  {type.Name} - no target on this version (Prepare-gated)");
            skipped++;
        }
        else
        {
            Console.WriteLine($"  ok    {type.Name} - {patched.Count} method(s)");
            bound++;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL  {type.Name} - {Innermost(ex).Message}");
        failed++;
    }
}

Console.WriteLine($"bound={bound} skipped={skipped} failed={failed}");
return failed == 0 ? 0 : 1;

static Exception Innermost(Exception exception)
{
    while (exception.InnerException != null)
    {
        exception = exception.InnerException;
    }

    return exception;
}

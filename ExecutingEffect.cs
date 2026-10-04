using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter;

/// <summary>
/// A supplemental per-player stack of the damaging powers a player is resolving whose hook the game does not push onto
/// its own model stack - the end-of-turn AoE buffs (Hailstorm, The Bomb) that deal to every enemy with the player as
/// dealer but no card source. <see cref="EffectSource"/> reads this when PlayerChoiceContext.LastInvolvedModel is
/// empty, so those hits are named too. Pushed when such a power's hook begins and popped when it completes, so it
/// stays balanced and never goes stale. Other mods' models are pushed the same way, around every method they run
/// (see <see cref="Patches.ModdedSourcePatches"/>).
/// </summary>
internal static class ExecutingEffect
{
    // A name, or a model whose name is read only if a hit actually asks for it: the modded-model patches push around
    // every hook a mod's model runs, nearly all of which deal no damage, so resolving a LocString on each would be
    // paying for names nobody reads.
    private static readonly Dictionary<ulong, Stack<object>> ByPlayer = new();
    private static readonly object Lock = new();

    public static void Push(ulong playerNetId, string name)
    {
        PushEntry(playerNetId, name);
    }

    public static void Push(ulong playerNetId, AbstractModel model)
    {
        PushEntry(playerNetId, model);
    }

    private static void PushEntry(ulong playerNetId, object entry)
    {
        lock (Lock)
        {
            if (!ByPlayer.TryGetValue(playerNetId, out Stack<object>? stack))
            {
                stack = new Stack<object>();
                ByPlayer[playerNetId] = stack;
            }

            stack.Push(entry);
        }
    }

    public static void Pop(ulong playerNetId)
    {
        lock (Lock)
        {
            if (ByPlayer.TryGetValue(playerNetId, out Stack<object>? stack) && stack.Count > 0)
            {
                stack.Pop();
                if (stack.Count == 0)
                {
                    ByPlayer.Remove(playerNetId);
                }
            }
        }
    }

    public static string? Current(ulong playerNetId)
    {
        object? entry;
        lock (Lock)
        {
            entry = ByPlayer.TryGetValue(playerNetId, out Stack<object>? stack) && stack.TryPeek(out object? top)
                ? top
                : null;
        }

        return entry switch
        {
            string name => name,
            AbstractModel model => ModdedModels.NameOf(model),
            _ => null,
        };
    }

    public static void Clear()
    {
        lock (Lock)
        {
            ByPlayer.Clear();
        }
    }
}

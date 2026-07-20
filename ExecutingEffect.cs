namespace RdpsMeter;

/// <summary>
/// A supplemental per-player stack of the damaging powers a player is resolving whose hook the game does not push onto
/// its own model stack - the end-of-turn AoE buffs (Hailstorm, The Bomb) that deal to every enemy with the player as
/// dealer but no card source. <see cref="EffectSource"/> reads this when PlayerChoiceContext.LastInvolvedModel is
/// empty, so those hits are named too. Pushed when such a power's hook begins and popped when it completes, so it
/// stays balanced and never goes stale.
/// </summary>
internal static class ExecutingEffect
{
    private static readonly Dictionary<ulong, Stack<string>> ByPlayer = new();
    private static readonly object Lock = new();

    public static void Push(ulong playerNetId, string name)
    {
        lock (Lock)
        {
            if (!ByPlayer.TryGetValue(playerNetId, out Stack<string>? stack))
            {
                stack = new Stack<string>();
                ByPlayer[playerNetId] = stack;
            }

            stack.Push(name);
        }
    }

    public static void Pop(ulong playerNetId)
    {
        lock (Lock)
        {
            if (ByPlayer.TryGetValue(playerNetId, out Stack<string>? stack) && stack.Count > 0)
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
        lock (Lock)
        {
            return ByPlayer.TryGetValue(playerNetId, out Stack<string>? stack) && stack.TryPeek(out string? name)
                ? name
                : null;
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            ByPlayer.Clear();
        }
    }
}

namespace RdpsMeter;

/// <summary>
/// Tracks which potion each player is currently resolving. A potion deals its damage through CreatureCmd.Damage with a
/// real player dealer but no card source, so the attribution engine credits the right player yet has nothing to name
/// the row and falls back to "(none)". This recovers the potion's own title for that window: it is set when a potion's
/// use begins and cleared when that player's card/potion effect ends, so a later dealer-less hit is never mislabelled
/// with a stale potion name.
/// </summary>
internal static class PotionSource
{
    private static readonly Dictionary<ulong, string> ByPlayer = new();
    private static readonly object Lock = new();

    public static void Begin(ulong playerNetId, string title)
    {
        lock (Lock)
        {
            ByPlayer[playerNetId] = title;
        }
    }

    public static void End(ulong playerNetId)
    {
        lock (Lock)
        {
            ByPlayer.Remove(playerNetId);
        }
    }

    public static string? Current(ulong playerNetId)
    {
        lock (Lock)
        {
            return ByPlayer.GetValueOrDefault(playerNetId);
        }
    }

    /// <summary>
    /// The one potion being resolved right now and who is resolving it, or null when that is not exactly one. Block
    /// gained from a potion has no other way to name its thrower: unlike damage, which arrives with a dealer, a thrown
    /// Block Potion reaches the block funnel knowing only who is receiving it - and in co-op that is not necessarily the
    /// player who threw it. Two potions resolving at once would make the answer a guess, so it declines to give one.
    /// </summary>
    public static (ulong NetId, string Title)? Sole()
    {
        lock (Lock)
        {
            if (ByPlayer.Count != 1)
            {
                return null;
            }

            foreach ((ulong netId, string title) in ByPlayer)
            {
                return (netId, title);
            }

            return null;
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

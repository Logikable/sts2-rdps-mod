namespace RdpsMeter;

/// <summary>
/// The power or relic a player is currently resolving when it deals damage. Like a potion, a power/relic hit reaches
/// the damage funnel with a real player dealer but no card source, so the meter credits the right player yet has
/// nothing to name the row and falls back to "(none)". This recovers the effect's name from the game's own
/// executing-model stack (PlayerChoiceContext.LastInvolvedModel), captured at the damage call. It is refreshed on
/// every dealer-less-card player hit - and cleared when no power/relic is behind it - so a later hit is never
/// mislabelled with a stale name.
/// </summary>
internal static class EffectSource
{
    private static readonly Dictionary<ulong, string> ByPlayer = new();
    private static readonly object Lock = new();

    // A null or empty name clears the entry: this hit has no identifiable power/relic behind it.
    public static void Set(ulong playerNetId, string? name)
    {
        lock (Lock)
        {
            if (string.IsNullOrEmpty(name))
            {
                ByPlayer.Remove(playerNetId);
            }
            else
            {
                ByPlayer[playerNetId] = name;
            }
        }
    }

    public static string? Current(ulong playerNetId)
    {
        lock (Lock)
        {
            return ByPlayer.GetValueOrDefault(playerNetId);
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

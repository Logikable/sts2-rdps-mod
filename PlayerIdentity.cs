using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Platform;

namespace RdpsMeter;

/// <summary>
/// Resolves a player's stable identity and display name. NetId is the game's own multiplayer key; for a networked
/// player it is the platform id (on Steam, the SteamID64) and resolves straight to a persona name. A singleplayer
/// local player, though, carries a small synthetic NetId the platform can't resolve, so it would otherwise show as a
/// bare number - for that case we fall back to the local persona, which is always available.
/// </summary>
internal static class PlayerIdentity
{
    // Real platform ids (SteamID64s) are far larger than the handful of synthetic ids a local/solo player gets. A
    // NetId below this is treated as synthetic, so an unresolved one falls back to the local persona rather than a
    // remote player whose name simply hasn't arrived yet.
    private const ulong SyntheticIdCeiling = 1UL << 53;

    public static string Name(ulong netId)
    {
        PlatformType platform = PlatformUtil.PrimaryPlatform;
        string name = PlatformUtil.GetPlayerNameRaw(platform, netId);
        if (IsResolved(name) || netId >= SyntheticIdCeiling)
        {
            return name;
        }

        string local = PlatformUtil.GetPlayerNameRaw(platform, PlatformUtil.GetLocalPlayerId(platform));
        return IsResolved(local) ? local : name;
    }

    public static string Name(Player player)
    {
        return Name(player.NetId);
    }

    // The platform degrades to the numeric id when it can't resolve a name; a real persona name is non-empty and not
    // just that number.
    private static bool IsResolved(string name)
    {
        return !string.IsNullOrWhiteSpace(name) && !ulong.TryParse(name, out _);
    }
}

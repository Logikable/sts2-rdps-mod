using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Platform;

namespace RdpsMeter;

/// <summary>
/// Resolves a player's stable identity and display name. NetId is the game's own multiplayer key (on Steam it is
/// literally the SteamID64); PlatformUtil.GetPlayerNameRaw branches local-vs-remote internally and degrades to a
/// numeric placeholder rather than throwing when Steam is unavailable, so one call is safe for every player.
/// </summary>
internal static class PlayerIdentity
{
    public static string Name(ulong netId)
    {
        return PlatformUtil.GetPlayerNameRaw(PlatformUtil.PrimaryPlatform, netId);
    }

    public static string Name(Player player)
    {
        return Name(player.NetId);
    }
}

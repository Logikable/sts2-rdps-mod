// Developer-only two-peer harness - compiled in only under -p:Harness=true (see RdpsMeter.csproj). Never ships.
#if RDPS_HARNESS
using MegaCrit.Sts2.Core.Helpers;

namespace RdpsMeter.Harness;

/// <summary>What role this process plays in a scripted two-peer session, and how much of the mod it runs.</summary>
internal enum MpRole
{
    /// <summary>Not a harness peer: the mod behaves exactly as it does in normal play.</summary>
    Off,
    Host,
    Client,
}

/// <summary>
/// The two-peer harness is configured from the command line rather than the environment, because these processes are
/// launched from WSL and a plain exported variable does not survive the crossing into a Windows process - the game's
/// own <see cref="CommandLineHelper"/> reads arguments Godot passes straight through, so an argument does.
///
/// Both peers run the same DLL. What makes one of them *effectively unmodded* is <c>--rdps-meter=off</c>, which makes
/// <see cref="Mod"/> skip every attribution patch and the overlay, leaving only this harness. That matters because the
/// game refuses to let a mods list differ between peers only for mods that declare themselves gameplay-affecting; the
/// meter declares it does not, so the configuration players actually hit is one side metered and the other not, and it
/// is the only configuration in which a difference the meter causes can show up as a checksum divergence. Keeping one
/// build and switching the patches off - rather than shipping a second driver-only mod - also keeps the handshake's
/// mod list identical on both sides, so a divergence cannot be blamed on the lists.
/// </summary>
internal static class MpConfig
{
    public static MpRole Role { get; } = ParseRole();

    /// <summary>Whether this peer applies the meter's patches at all. False makes it behave as an unmodded client.</summary>
    public static bool MeterEnabled { get; } = Arg("rdps-meter") != "off";

    /// <summary>Which scripted scenario to drive once both peers are in combat.</summary>
    public static string Scenario { get; } = Arg("rdps-scenario") ?? "idle";

    public static string HostIp { get; } = Arg("rdps-mp-ip") ?? "127.0.0.1";

    /// <summary>The net id this peer announces to the host. ENet hands out no identity of its own, so we assign one.</summary>
    public static ulong NetId { get; } = ulong.TryParse(Arg("rdps-mp-netid"), out ulong id) ? id : 1000uL;

    /// <summary>
    /// Which character this peer picks, as an index into the multiplayer test scene's own paginator
    /// (0 Ironclad, 1 Silent, 2 Regent, 3 Necrobinder, 4 Defect). Echo Form is a Defect card.
    /// </summary>
    public static int CharacterIndex { get; } = int.TryParse(Arg("rdps-character"), out int i) ? i : 0;

    /// <summary>Both peers must agree on the seed or the run they build diverges before a card is ever played.</summary>
    public static string Seed { get; } = Arg("rdps-seed") ?? "RDPSMPTEST";

    /// <summary>
    /// What shape of session this is. "fresh" starts a co-op run and fights; "rejoin" does the same and then has the
    /// client drop out mid-fight and try to come back, which is the second bug report's own words.
    /// </summary>
    public static string Flow { get; } = Arg("rdps-mp-flow") ?? "fresh";

    /// <summary>How many turns each peer plays before it calls the fight done.</summary>
    public static int Turns { get; } = int.TryParse(Arg("rdps-mp-turns"), out int t) ? t : 6;

    /// <summary>
    /// Which turn the client drops the connection on, in the rejoin flow. Mid-fight rather than between fights,
    /// because the interesting question is what the meter does while it is holding a live combat's tally and the
    /// overlay is drawing from it - the run is torn down underneath both.
    /// </summary>
    public static int DisconnectAfterTurns { get; } =
        int.TryParse(Arg("rdps-mp-drop-turn"), out int d) ? d : 2;

    /// <summary>Give up and quit rather than hang forever when a peer never connects or a fight never starts.</summary>
    public static double TimeoutSeconds { get; } =
        double.TryParse(Arg("rdps-mp-timeout"), out double t) ? t : 300.0;

    public static bool Active => Role != MpRole.Off;

    private static MpRole ParseRole()
    {
        return Arg("rdps-mp") switch
        {
            "host" => MpRole.Host,
            "client" => MpRole.Client,
            _ => MpRole.Off,
        };
    }

    // Always read the "--key=value" form. CommandLineHelper also accepts a bare "--key value" pair, which silently
    // swallows the following argument as the value; spelling every one with an "=" keeps the argument list order-free.
    private static string? Arg(string key)
    {
        return CommandLineHelper.TryGetValue(key, out string? value) ? value : null;
    }
}
#endif

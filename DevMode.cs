namespace RdpsMeter;

/// <summary>
/// Whether the mod's developer tools are enabled. Two of them - the headless auto-harness and the F9 in-combat
/// self-test - drive live combat purely to validate attribution: they spawn fake players, apply effects, deal damage,
/// even kill the enemy. That is exactly what a shipped build must never let a real player trigger. Both arm only when
/// an "autotest.marker" file sits beside the mod DLL, which a released build does not ship; the check is done once at
/// load and cached, since the marker cannot appear or vanish mid-run.
/// </summary>
internal static class DevMode
{
    private static readonly bool EnabledValue = Detect();

    public static bool Enabled => EnabledValue;

    private static bool Detect()
    {
        string? dir = Path.GetDirectoryName(typeof(DevMode).Assembly.Location);
        return dir != null && File.Exists(Path.Combine(dir, "autotest.marker"));
    }
}

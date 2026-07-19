using Godot;

namespace RdpsMeter;

/// <summary>
/// Remembers where the player last put the rDPS window, so it reappears there next session. Stored in the game's user
/// data (not beside the read-only mod dll) as a tiny config file; a missing or unreadable file just means "no saved
/// spot, use the default corner".
/// </summary>
internal static class OverlayLayout
{
    private const string Path = "user://rdps_meter.cfg";
    private const string Section = "overlay";

    public static void SavePosition(Vector2 position)
    {
        var config = new ConfigFile();
        config.SetValue(Section, "x", position.X);
        config.SetValue(Section, "y", position.Y);
        config.Save(Path);
    }

    public static Vector2? LoadPosition()
    {
        var config = new ConfigFile();
        if (config.Load(Path) != Error.Ok
            || !config.HasSectionKey(Section, "x")
            || !config.HasSectionKey(Section, "y"))
        {
            return null;
        }

        return new Vector2(config.GetValue(Section, "x").AsSingle(), config.GetValue(Section, "y").AsSingle());
    }
}

using Godot;

namespace RdpsMeter;

/// <summary>Which meter the window is showing: rDPS, which credits teammates, or the raw damage the player dealt.</summary>
internal enum MeterMode
{
    Rdps,
    ADps,
}

/// <summary>
/// Remembers how the player left the rDPS window - where they put it, and which meter they were reading - so it comes
/// back the same way next session. Stored in the game's user data (not beside the read-only mod dll) as a tiny config
/// file; a missing or unreadable file just means "no saved spot, use the default corner" and the default meter.
/// </summary>
internal static class OverlayLayout
{
    private const string Path = "user://rdps_meter.cfg";
    private const string Section = "overlay";

    /// <summary>Both settings live in one file, so each save has to carry the other through rather than drop it.</summary>
    public static void SavePosition(Vector2 position)
    {
        var config = Read();
        config.SetValue(Section, "x", position.X);
        config.SetValue(Section, "y", position.Y);
        config.Save(Path);
    }

    public static void SaveMode(MeterMode mode)
    {
        var config = Read();
        config.SetValue(Section, "mode", mode == MeterMode.ADps ? "adps" : "rdps");
        config.Save(Path);
    }

    /// <summary>The meter last read, defaulting to rDPS - the one the mod exists for.</summary>
    public static MeterMode LoadMode()
    {
        var config = new ConfigFile();
        if (config.Load(Path) != Error.Ok || !config.HasSectionKey(Section, "mode"))
        {
            return MeterMode.Rdps;
        }

        return config.GetValue(Section, "mode").AsString() == "adps" ? MeterMode.ADps : MeterMode.Rdps;
    }

    private static ConfigFile Read()
    {
        var config = new ConfigFile();
        config.Load(Path);
        return config;
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

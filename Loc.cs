using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Models;
using FileAccess = Godot.FileAccess;

namespace RdpsMeter;

/// <summary>
/// The meter's own text, in the language the player has the game set to. The translations are JSON tables compiled
/// into the dll (see localization/), because a release ships nothing but the dll and the manifest - the game's own
/// mod loc-table hook would need us to ship a .pck. A player's language is read from the game's LocManager on every
/// lookup, so switching language in settings takes effect without a restart; <see cref="Revision"/> tells the overlay
/// when that happened so it can redraw the text it has already placed.
///
/// Only the meter's own chrome lives here. Everything the meter borrows from the game - card, potion, relic, power and
/// encounter names - is already localized by the game itself, with two exceptions this class also covers: the effect
/// names the ledger stores (kept English so saved tallies stay readable across languages, translated on the way to the
/// screen by <see cref="PowerName"/>) and the placeholder for an unattributable damage source.
/// </summary>
internal static class Loc
{
    // The language whose table backs every key, and the fallback for any key a translation is missing.
    private const string Fallback = "eng";

    // A loose table here wins over the compiled-in one, so a translator can iterate on their language - and a player
    // can fix a wording they dislike - without rebuilding the mod. One file per language, e.g. zhs.json.
    private const string OverrideFolder = "user://rdps_meter/localization";

    private static readonly object Lock = new();
    private static readonly Dictionary<string, string> English = Load(Fallback);

    private static Dictionary<string, string> _strings = English;
    private static Dictionary<string, string>? _powerNames;
    private static string _language = Fallback;
    private static int _revision;
    private static bool _isEnglishText = true;

    /// <summary>
    /// Bumped whenever the tables are reloaded for a new language. The overlay watches it the same way it watches the
    /// run generation, and rebuilds the labels it has already drawn - text and locale font alike - when it changes.
    /// </summary>
    public static int Revision
    {
        get
        {
            Sync();
            return _revision;
        }
    }

    /// <summary>
    /// True when the text on screen is the English set - either the game is in English, or the player's language has
    /// no translation here and fell back to it. Rules that only hold for English (pluralizing by appending "s") apply
    /// exactly then.
    /// </summary>
    public static bool IsEnglishText
    {
        get
        {
            Sync();
            return _isEnglishText;
        }
    }

    public static string T(string key)
    {
        Sync();
        lock (Lock)
        {
            // The key itself is the last resort: a missing string should look wrong in the corner of the screen, not
            // take the overlay down.
            return _strings.GetValueOrDefault(key) ?? English.GetValueOrDefault(key) ?? key;
        }
    }

    public static string T(string key, params object?[] args)
    {
        try
        {
            // Invariant formatting: every argument is already a formatted number or a name, and the game may be
            // running in .NET globalization-invariant mode anyway.
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, T(key), args);
        }
        catch (FormatException)
        {
            // A translation with a broken placeholder ("{0" or "{2}" where there is one argument) must not throw in
            // the middle of a draw; show the unformatted text instead.
            return T(key);
        }
    }

    /// <summary>
    /// The display name for an effect the ledger has stored (see AttributionEngine's EffectName, which keeps the
    /// English "Vulnerable" so a tally saved in one language still reads in another). Resolved through the game's own
    /// power titles, so it matches the wording the player sees on the power's icon; an effect with no matching power
    /// - a mod's, or one whose class was renamed by a game update - keeps the stored name.
    /// </summary>
    public static string PowerName(string effect)
    {
        Sync();
        lock (Lock)
        {
            _powerNames ??= LoadPowerNames();
            return _powerNames.GetValueOrDefault(effect) ?? effect;
        }
    }

    /// <summary>
    /// The display name for a damage source the ledger has stored: card, potion, relic, power and orb names arrive
    /// already localized from the game, so only the placeholder for a source we could not identify needs translating.
    /// </summary>
    public static string SourceName(string source)
    {
        return source == AttributionEngine.UnknownSource ? T("source.unknown") : source;
    }

    /// <summary>
    /// Gives a control the font the player's language needs. Chinese, Japanese, Korean, Thai, Russian and Polish all
    /// need a font the default theme's does not cover; without this every borrowed name (cards, enemies, powers) draws
    /// as empty boxes, so this matters even before a word of the meter's own text is translated.
    /// </summary>
    public static void ApplyFont(Control control, StringName themeItem)
    {
        Apply(control.AddThemeFontOverride, themeItem);
    }

    /// <summary>
    /// The same, for a popup - a Window rather than a Control, but themed the same way.
    /// </summary>
    public static void ApplyFont(Window window, StringName themeItem)
    {
        Apply(window.AddThemeFontOverride, themeItem);
    }

    private static void Apply(Action<StringName, Font> addOverride, StringName themeItem)
    {
        Sync();
        try
        {
            Substitute(addOverride, themeItem);
        }
        catch (Exception ex)
        {
            // Isolated the way patches are: a game build without this API, or a font that fails to load, costs the
            // overlay its glyphs for that language - never its existence.
            GD.PrintErr($"[RdpsMeter] Could not apply the locale font - non-latin text may not render: {ex.Message}");
        }
    }

    // The one place that touches the game's font API. Kept out of line so the JIT resolves it when it is called, not
    // when its caller is compiled: a game build that dropped the API then throws inside Apply's try, rather than out
    // of whatever was drawing at the time.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Substitute(Action<StringName, Font> addOverride, StringName themeItem)
    {
        Font? font = FontManager.GetSubstituteFont(_language, FontType.Regular);
        if (font != null)
        {
            addOverride(themeItem, font);
        }
    }

    // Reloads the tables when the player's language differs from the loaded one - at startup, and again whenever they
    // change it in settings.
    private static void Sync()
    {
        string language = CurrentLanguage();
        lock (Lock)
        {
            if (language == _language)
            {
                return;
            }

            _language = language;
            _strings = language == Fallback ? English : Load(language);
            _isEnglishText = language == Fallback || _strings.Count == 0;
            _powerNames = null;
            _revision++;
        }
    }

    // The game's current language, or English until the game has one. LocManager is initialized after mods are, so a
    // lookup during mod init - and any future build that moves or renames it - lands on English rather than throwing.
    private static string CurrentLanguage()
    {
        try
        {
            string? language = LocManager.Instance?.Language;
            return string.IsNullOrEmpty(language) ? Fallback : language;
        }
        catch (Exception)
        {
            return Fallback;
        }
    }

    // A language's table: the compiled-in one, with any loose override file merged over the top of it.
    private static Dictionary<string, string> Load(string language)
    {
        Dictionary<string, string> table = Embedded(language);
        foreach (KeyValuePair<string, string> entry in Override(language))
        {
            table[entry.Key] = entry.Value;
        }

        return table;
    }

    private static Dictionary<string, string> Embedded(string language)
    {
        try
        {
            using Stream? stream = typeof(Loc).Assembly
                .GetManifestResourceStream($"RdpsMeter.localization.{language}.json");
            if (stream == null)
            {
                return new Dictionary<string, string>();
            }

            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Could not read the built-in '{language}' translation: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    private static Dictionary<string, string> Override(string language)
    {
        try
        {
            string path = $"{OverrideFolder}/{language}.json";
            if (!FileAccess.FileExists(path))
            {
                return new Dictionary<string, string>();
            }

            using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            Dictionary<string, string>? table = file == null
                ? null
                : JsonSerializer.Deserialize<Dictionary<string, string>>(file.GetAsText());
            if (table != null)
            {
                GD.Print($"[RdpsMeter] Using the '{language}' translation override at {path}");
            }

            return table ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Ignoring the '{language}' translation override - it could not be read: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    // Every power the game knows, keyed the way the ledger stores it (class name without the "Power" suffix, matching
    // AttributionEngine's EffectName) and valued with the power's own localized title. Built once per language: the
    // model list is cached by the game, and reading a title formats a loc string, so this is not work to repeat on
    // every frame the breakdown is drawn.
    private static Dictionary<string, string> LoadPowerNames()
    {
        var names = new Dictionary<string, string>();
        try
        {
            foreach (PowerModel power in ModelDb.AllPowers)
            {
                try
                {
                    string type = power.GetType().Name;
                    string key = type.EndsWith("Power", StringComparison.Ordinal)
                        ? type[..^"Power".Length]
                        : type;
                    names[key] = power.Title.GetFormattedText();
                }
                catch (Exception)
                {
                    // One power without a title (a mod's, or one mid-rename in a game update) leaves that effect
                    // showing its stored name; the rest still translate.
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[RdpsMeter] Could not read the game's power names - effects will show untranslated: {ex.Message}");
        }

        return names;
    }
}

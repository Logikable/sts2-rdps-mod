using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter;

/// <summary>
/// What the meter can learn about a model it was never written for - a character mod's turret, rune or debuff - from
/// conventions alone rather than from its type.
///
/// Mods build on the game's own model classes, and the game names and owns every model the same two ways: a
/// <c>Title</c> that is a LocString, and an <c>Owner</c> that is either the Player or that player's Creature. A mod that
/// invents a model kind of its own - Runesmith's runes derive from AbstractModel directly, not from any of the game's
/// kinds - still follows both, because it copies the game's patterns. So both are read by name, by reflection, and
/// cached per type; a model that follows neither falls back to its class name made readable.
/// </summary>
internal static class ModdedModels
{
    private static readonly Assembly GameAssembly = typeof(AbstractModel).Assembly;
    private static readonly Assembly OwnAssembly = typeof(ModdedModels).Assembly;

    private static readonly ConcurrentDictionary<Type, PropertyInfo?> TitleProperties = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> OwnerProperties = new();

    /// <summary>Whether a type comes from another mod rather than from the game or from the meter itself.</summary>
    public static bool IsModded(Type type)
    {
        return IsModded(type.Assembly);
    }

    public static bool IsModded(Assembly assembly)
    {
        return assembly != GameAssembly && assembly != OwnAssembly;
    }

    /// <summary>
    /// The model's display name: its Title where it has one ("Turret", "Flamma"), otherwise its class name with the
    /// kind suffix dropped and the words spaced out ("FlammaRune" -> "Flamma Rune", "OilPower" -> "Oil"). Never throws:
    /// a name is never worth breaking a hit over.
    /// </summary>
    public static string NameOf(AbstractModel model)
    {
        try
        {
            PropertyInfo? title = TitleProperties.GetOrAdd(model.GetType(), static type =>
                AccessTools.Property(type, "Title") is { PropertyType: var t } p && t == typeof(LocString) ? p : null);
            if (title?.GetValue(model) is LocString loc && loc.GetFormattedText() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (Exception)
        {
            // A missing localisation key throws on some builds; the class name below is still a real name.
        }

        return Readable(model.GetType().Name);
    }

    /// <summary>
    /// The player a model belongs to, or null when it belongs to none - an enemy's power, or a model with no Owner
    /// the meter can read.
    /// </summary>
    public static Player? OwnerOf(AbstractModel model)
    {
        try
        {
            PropertyInfo? owner = OwnerProperties.GetOrAdd(model.GetType(), static type =>
                AccessTools.Property(type, "Owner") is { PropertyType: var t } p
                && (typeof(Player).IsAssignableFrom(t) || typeof(Creature).IsAssignableFrom(t))
                    ? p
                    : null);
            return owner?.GetValue(model) switch
            {
                Player player => player,
                Creature creature => creature.Player,
                _ => null,
            };
        }
        catch (Exception)
        {
            // An Owner getter that throws before the model is attached (some assert a set owner) has no owner yet.
            return null;
        }
    }

    private static string Readable(string typeName)
    {
        foreach (string suffix in new[] { "Power", "Relic", "Orb", "Model" })
        {
            if (typeName.Length > suffix.Length && typeName.EndsWith(suffix, StringComparison.Ordinal))
            {
                typeName = typeName[..^suffix.Length];
                break;
            }
        }

        return Regex.Replace(typeName, "(?<=[a-z0-9])(?=[A-Z])", " ");
    }
}

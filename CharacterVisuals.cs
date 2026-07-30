using Godot;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter;

/// <summary>
/// Turns a saved character model id back into the two things a row is drawn with: the class colour and the class icon.
///
/// This is what makes a restored breakdown look like the run it came from. While combat is running the overlay reads
/// both straight off the live <c>Player.Character</c>; once the run is over - or the game has been restarted - there is
/// no live player left, and the only thing on disk is the model id the roster saved. Both properties belong to the
/// character *prototype* rather than to a run's copy of it, so the one in <c>ModelDb</c> answers exactly as the live
/// model would.
///
/// Everything here is best-effort. A model id from a mod that is no longer installed, or one the game has since
/// renamed, simply does not resolve, and the caller falls back to the neutral tint it used before any of this existed -
/// a grey row is a far better outcome than a meter that throws while drawing.
/// </summary>
internal static class CharacterVisuals
{
    // Resolving walks the model database and, for the icon, hits the texture cache. Rows are rebuilt whenever the run
    // or the language changes, so the same handful of ids would be resolved repeatedly; a run has at most a few players
    // in it, so the cache never grows.
    private static readonly Dictionary<string, (Color Color, Texture2D? Icon)?> Cache = new();
    private static readonly object Lock = new();

    /// <summary>The class colour and icon for a saved model id, or null when it names nothing we can draw.</summary>
    public static (Color Color, Texture2D? Icon)? For(string? characterId)
    {
        if (string.IsNullOrEmpty(characterId))
        {
            return null;
        }

        lock (Lock)
        {
            if (Cache.TryGetValue(characterId, out (Color Color, Texture2D? Icon)? cached))
            {
                return cached;
            }

            (Color Color, Texture2D? Icon)? resolved = Resolve(characterId);
            Cache[characterId] = resolved;
            return resolved;
        }
    }

    /// <summary>Drops the resolved visuals. For the self-test, which needs a cold lookup to be measuring anything.</summary>
    public static void ClearCache()
    {
        lock (Lock)
        {
            Cache.Clear();
        }
    }

    private static (Color Color, Texture2D? Icon)? Resolve(string characterId)
    {
        try
        {
            // Asked for as the base model and matched afterwards: GetByIdOrNull casts to its type argument without
            // checking, so asking it for a CharacterModel would throw on an id that turned out to be something else
            // rather than simply not matching. Same reason the fight labels ask for AbstractModel.
            if (ModelDb.GetByIdOrNull<AbstractModel>(ModelId.Deserialize(characterId)) is not CharacterModel character)
            {
                return null;
            }

            // The colour is a constant on the class and cannot fail. The icon goes through the preload cache, which can
            // come up empty for art that is not loaded at this point in the game's life, so it is fetched separately -
            // losing the icon must not cost the colour as well.
            return (character.NameColor, Icon(character));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Texture2D? Icon(CharacterModel character)
    {
        try
        {
            return character.IconTexture;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

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
    // What is cached is the *prototype*, never the texture it hands out.
    //
    // The id lookup walks the model database and is worth doing once; the icon is not, because it is a live Godot
    // resource with a shorter life than this process. IconTexture reads the game's preload cache, which is torn down
    // and rebuilt as the game moves between the menu and a run - so a texture resolved at the main menu is freed the
    // moment a run loads, and a cache holding it goes on handing out a disposed object for the rest of the session.
    // That is not theoretical: it is what made the overlay throw ObjectDisposedException every frame after loading a
    // saved co-op run, 4,613 times and 9MB of log in one short session, because the row that failed to build was
    // retried on the next frame forever.
    //
    // The prototype itself lives in ModelDb for the life of the process, so caching it is safe and re-reading NameColor
    // and IconTexture off it each time costs a property read. This is the same rule the saved file already follows -
    // keep the model id, resolve the asset - applied to memory as well as to disk.
    private static readonly Dictionary<string, CharacterModel?> Cache = new();
    private static readonly object Lock = new();

    /// <summary>The class colour and icon for a saved model id, or null when it names nothing we can draw.</summary>
    public static (Color Color, Texture2D? Icon)? For(string? characterId)
    {
        if (string.IsNullOrEmpty(characterId))
        {
            return null;
        }

        CharacterModel? character;
        lock (Lock)
        {
            if (!Cache.TryGetValue(characterId, out character))
            {
                character = Resolve(characterId);
                Cache[characterId] = character;
            }
        }

        // Read outside the lock and fresh every time: the colour is a constant on the class, and the icon has to come
        // from whatever the game has loaded now rather than from whatever it had loaded when this id was first seen.
        return character == null ? null : (character.NameColor, Icon(character));
    }

    /// <summary>Drops the resolved prototypes. For the self-test, which needs a cold lookup to be measuring anything.</summary>
    public static void ClearCache()
    {
        lock (Lock)
        {
            Cache.Clear();
        }
    }

    private static CharacterModel? Resolve(string characterId)
    {
        try
        {
            // Asked for as the base model and matched afterwards: GetByIdOrNull casts to its type argument without
            // checking, so asking it for a CharacterModel would throw on an id that turned out to be something else
            // rather than simply not matching. Same reason the fight labels ask for AbstractModel.
            return ModelDb.GetByIdOrNull<AbstractModel>(ModelId.Deserialize(characterId)) as CharacterModel;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // The icon goes through the game's preload cache, which can come up empty - or throw - for art that is not loaded
    // at this point in the game's life, so it is fetched separately: losing the icon must not cost the colour as well.
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

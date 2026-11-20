using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter;

/// <summary>
/// The power or relic a player is currently resolving when it deals damage. Like a potion, a power/relic hit reaches
/// the damage funnel with a real player dealer but no card source, so the meter credits the right player yet has
/// nothing to name the row and falls back to "(none)". This recovers the effect's name from the game's own
/// executing-model stack (PlayerChoiceContext.LastInvolvedModel), captured at the damage call. It is refreshed on
/// every dealer-less-card player hit - and cleared when no power/relic is behind it - so a later hit is never
/// mislabelled with a stale name.
/// </summary>
internal static class EffectSource
{
    private static readonly Dictionary<ulong, string> ByPlayer = new();
    private static readonly object Lock = new();

    // A null or empty name clears the entry: this hit has no identifiable power/relic behind it.
    public static void Set(ulong playerNetId, string? name)
    {
        lock (Lock)
        {
            if (string.IsNullOrEmpty(name))
            {
                ByPlayer.Remove(playerNetId);
            }
            else
            {
                ByPlayer[playerNetId] = name;
            }
        }
    }

    /// <summary>
    /// What is running right now on a context, for a player: the name to give an effect that came with no card. Read
    /// from the context the caller was handed, never from <see cref="Current"/>, which is a *hit's* name and stays
    /// behind after its hit until that player's next card-less hit replaces it.
    ///
    /// Prefer the game's own executing-model stack when a power, relic or orb is on top of it. Otherwise fall back to
    /// our supplemental stack: the end-of-turn AoE powers the game does not push (Hailstorm, The Bomb), an orb's own
    /// end-of-turn passive, which the game does not push either (see OrbPassiveSourcePatches), and whatever modded
    /// model is running (see ModdedSourcePatches). A Defect orb dealing damage - Lightning and Glass on passive and
    /// evoke, Dark on evoke - is pushed by OrbCmd on every other route, so it is named the same way.
    ///
    /// Last comes any other model the game has on top - a modded kind of its own, like a Runesmith rune its command
    /// pushed, or the card being played when the effect came from something the card set off and nothing closer
    /// claimed it ("Turret Push" over "(none)"). Below the supplemental stack on purpose: a turret fired by a card has
    /// the card on the game's stack and the turret on ours, and the turret is the nearer of the two. A potion is left to
    /// PotionSource, which already names it.
    /// </summary>
    public static string? Resolve(AbstractModel? running, ulong playerNetId)
    {
        string? name = running switch
        {
            PowerModel power => power.Title.GetFormattedText(),
            RelicModel relic => relic.Title.GetFormattedText(),
            OrbModel orb => orb.Title.GetFormattedText(),
            _ => null,
        };
        name ??= ExecutingEffect.Current(playerNetId);
        if (name == null && running is not (null or PotionModel))
        {
            name = running is CardModel card ? CardNames.Of(card) : ModdedModels.NameOf(running);
        }

        return name;
    }

    public static string? Current(ulong playerNetId)
    {
        lock (Lock)
        {
            return ByPlayer.GetValueOrDefault(playerNetId);
        }
    }

    public static void Clear()
    {
        lock (Lock)
        {
            ByPlayer.Clear();
        }
    }
}

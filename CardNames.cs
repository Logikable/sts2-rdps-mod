using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Models;

namespace RdpsMeter;

/// <summary>
/// The one place a card's row name comes from.
///
/// Rows want the bare title - "Anger", not "Anger+" - so they read <c>TitleLocString</c>, the <c>&lt;ID&gt;.title</c>
/// key, rather than <c>Title</c>, which appends the upgrade marker. That key is a convention the game does not hold
/// itself to. Dingy Dagger (0.113.0) overrides <c>Title</c> to pick one of three keys by upgrade tier
/// (<c>.title.dingy</c>, <c>.title.fine</c>, <c>.title.pristine</c>) and has no plain <c>.title</c> in any language,
/// and a missing key does not come back empty: <c>LocTable.GetRawText</c> throws <c>LocException</c>.
///
/// That throw is not a cosmetic failure. The card name is read in the prefix of every hit, and a prefix that throws
/// stops the game's own method - so the dagger dealt nothing, the play hung, and in co-op the peer whose prefix threw
/// fell out of step with the one that did not. So: ask whether the key exists, fall back to the game's own
/// <c>Title</c> with the upgrade marker stripped, and never let a name escape as an exception.
/// </summary>
internal static class CardNames
{
    private static readonly Regex UpgradeSuffix = new(@"\+\d*$", RegexOptions.Compiled);

    public static string Of(CardModel card)
    {
        try
        {
            if (card.TitleLocString.Exists())
            {
                return card.TitleLocString.GetFormattedText();
            }
        }
        catch (Exception)
        {
            // Fall through to the computed title.
        }

        try
        {
            string title = UpgradeSuffix.Replace(card.Title, "");
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }
        catch (Exception)
        {
            // Fall through to the id.
        }

        return card.Id.Entry;
    }

    public static string? OrNull(CardModel? card)
    {
        return card == null ? null : Of(card);
    }
}

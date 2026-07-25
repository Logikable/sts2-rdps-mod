namespace RdpsMeter;

/// <summary>
/// Names a fight after the enemies in it, for the rare combat the game itself does not name (see
/// <see cref="Patches.CombatLifecyclePatches"/>, which prefers the encounter's own title). A single enemy type keeps
/// its full name (pluralized when there are several of it - "Slimes"); a mix is shortened to about the length of one
/// name by keeping just the creature nouns, so the dropdown stays scannable. Input is expected toughest-first, so a mix
/// is named after its most notable enemy (an elite reads as "Elite +2", not after whichever minion sat in slot one).
/// </summary>
internal static class FightLabel
{
    public static string From(IReadOnlyList<string> enemyNames)
    {
        if (enemyNames == null || enemyNames.Count == 0)
        {
            return Loc.T("combat");
        }

        var distinct = new List<string>();
        foreach (string name in enemyNames)
        {
            if (!string.IsNullOrWhiteSpace(name) && !distinct.Contains(name))
            {
                distinct.Add(name);
            }
        }

        if (distinct.Count == 0)
        {
            return Loc.T("combat");
        }

        if (distinct.Count == 1)
        {
            return enemyNames.Count > 1 ? Pluralize(distinct[0]) : distinct[0];
        }

        if (distinct.Count == 2)
        {
            return Loc.T("enemies.pair", LastWord(distinct[0]), LastWord(distinct[1]));
        }

        return Loc.T("enemies.more", LastWord(distinct[0]), distinct.Count - 1);
    }

    // Shortening a name to its creature noun only means anything for a language that writes names as words with
    // spaces; one that does not (Chinese, Japanese) has no last word to take, and keeps the whole name.
    private static string LastWord(string name)
    {
        string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? name : parts[^1];
    }

    // Pluralize just the last word, so "Acid Slime" -> "Acid Slimes". Deliberately naive (append "s"): enemy names
    // almost always pluralize that way, and an odd plural on a stat label is harmless. English-only - every other
    // language either does not mark plurals this way or does not mark them at all, so their names are left alone.
    private static string Pluralize(string name)
    {
        if (!Loc.IsEnglishText)
        {
            return name;
        }

        int space = name.LastIndexOf(' ');
        string head = space >= 0 ? name[..(space + 1)] : string.Empty;
        string tail = space >= 0 ? name[(space + 1)..] : name;
        if (tail.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return head + tail + "s";
    }
}

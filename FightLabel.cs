namespace RdpsMeter;

/// <summary>
/// Turns the enemies a combat started with into a short, readable name for the fight picker. A single enemy type keeps
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
            return "Combat";
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
            return "Combat";
        }

        if (distinct.Count == 1)
        {
            return enemyNames.Count > 1 ? Pluralize(distinct[0]) : distinct[0];
        }

        if (distinct.Count == 2)
        {
            return $"{LastWord(distinct[0])} & {LastWord(distinct[1])}";
        }

        return $"{LastWord(distinct[0])} +{distinct.Count - 1}";
    }

    private static string LastWord(string name)
    {
        string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? name : parts[^1];
    }

    // Pluralize just the last word, so "Acid Slime" -> "Acid Slimes". Deliberately naive (append "s"): enemy names
    // almost always pluralize that way, and an odd plural on a stat label is harmless.
    private static string Pluralize(string name)
    {
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

namespace Tanjelo.Core;

/// <summary>
/// How the menu's filter box finds entries. A match in an entry's name or key beats a match in its description,
/// so typing "move" picks "Move to…" rather than Mute, whose description says "Remove the sound".
/// </summary>
public static class MenuSearch
{
    public const int NoMatch = 0, DescriptionMatch = 1, NameMatch = 2;

    /// <param name="extra">Other text that counts like the description, such as the group heading.</param>
    public static int Score(Target target, string query, string? extra = null)
    {
        query = query.Trim();
        if (query.Length == 0) return NameMatch;
        if (Has(target.Label, query) || Has(target.Key, query)) return NameMatch;
        if (Has(target.Description, query) || (extra is not null && Has(extra, query))) return DescriptionMatch;
        return NoMatch;
    }

    /// <summary>
    /// The tab to show while filtering: the current tab if it has the best kind of match there is, otherwise the
    /// first tab, in <paramref name="tabs"/> order, that has it. Null when nothing matches anywhere.
    /// </summary>
    public static TargetKind? BestTab(IEnumerable<(TargetKind Kind, int Score)> scores, TargetKind current, IReadOnlyList<TargetKind> tabs)
    {
        var best = scores.Where(s => s.Score > NoMatch).ToList();
        if (best.Count == 0) return null;
        var top = best.Max(s => s.Score);
        if (best.Any(s => s.Kind == current && s.Score == top)) return current;
        return tabs.First(kind => best.Any(s => s.Kind == kind && s.Score == top));
    }

    static bool Has(string text, string query) => text.Contains(query, StringComparison.OrdinalIgnoreCase);
}

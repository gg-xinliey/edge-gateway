using System.Text.RegularExpressions;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Name matching for a tag the point map doesn't know: which MES column is it most
// likely? The tag's words (GOOD_QTY, goodQty -> good, qty), with synonyms replaced
// (qty -> quantity), are compared with each column of Config/mes-schema.json by edit
// similarity: 1 = the same name, 0 = nothing alike.
//
// A match only counts for an automatic mapping when nothing makes it ambiguous
// (Conflict = null): the column must exist in one data group only, and no tag of
// this mapping may fill it already. Otherwise it is a suggestion for a person.
public static class NameMatcher
{
    public sealed record Match(string Group, string Field, double Score, string? Conflict);

    // "qty=quantity, sn=serial number" -> { qty: "quantity", sn: "serial number" }. Invalid items are skipped
    // (ConfigValidator refuses them on save).
    public static Dictionary<string, string> ParseSynonyms(string? text)
    {
        var synonyms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                synonyms[parts[0]] = parts[1];
        }
        return synonyms;
    }

    // null when there are no MES columns to compare with.
    public static Match? Best(string tag, IReadOnlyDictionary<string, IReadOnlyList<string>> mesColumns,
        IReadOnlyDictionary<string, string> synonyms, IReadOnlyList<PointMapEntry> pointMap)
    {
        var name = Words(tag, synonyms);
        var scored = mesColumns
            .SelectMany(g => g.Value.Select(column => (Group: g.Key, Field: column, Score: Similarity(name, Words(column, synonyms)))))
            .ToList();
        if (scored.Count == 0)
            return null;

        var top = scored.Max(s => s.Score);
        var best = scored.Where(s => s.Score == top).ToList();
        var first = best[0];

        string? conflict = null;
        var fields = best.Select(b => b.Field).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (fields.Count > 1)
            conflict = $"fits {string.Join(", ", fields)} equally";
        else if (best.Count > 1)
            conflict = $"{first.Field} is in several data groups: {string.Join(", ", best.Select(b => b.Group))}";
        else if (pointMap.FirstOrDefault(p => p.DataGroup.Equals(first.Group, StringComparison.OrdinalIgnoreCase)
                                              && p.FieldName.Equals(first.Field, StringComparison.OrdinalIgnoreCase)) is { } filled)
            conflict = $"{first.Group} / {first.Field} already has tag {filled.Tag}";

        return new Match(first.Group, first.Field, Math.Round(top, 2), conflict);
    }

    // GOOD_QTY / goodQty / good-qty -> "good_quantity" (with qty=quantity).
    private static string Words(string name, IReadOnlyDictionary<string, string> synonyms)
    {
        var spaced = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2");
        var words = Regex.Split(spaced, "[^A-Za-z0-9]+")
            .Where(w => w.Length > 0)
            .SelectMany(w => (synonyms.TryGetValue(w, out var full) ? full : w).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.ToLowerInvariant());
        return string.Join('_', words);
    }

    // 1 - edit distance / longer length.
    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        var current = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
}

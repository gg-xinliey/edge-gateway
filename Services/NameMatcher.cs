using System.Text.RegularExpressions;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Name matching for a tag the point map doesn't know: which MES column is it most
// likely? The tag's words (GOOD_QTY, goodQty -> good, qty), with synonyms replaced
// (qty -> quantity), are compared with each column of Config/mes-schema.json: the better
// of edit similarity (letters) and shared whole words, where a word few columns use
// (serial) counts more than a common one (code, status) (#27). 1 = the same name, 0 = nothing alike.
//
// A match only counts for an automatic mapping when nothing makes it ambiguous
// (Conflict = null): the column must exist in one data group only, and no tag of
// this mapping may fill it already. Otherwise it is a suggestion for a person.
public static class NameMatcher
{
    // SeenOn: tabs where people mapped the tag this way (Seen); 0 = a name match.
    public sealed record Match(string Group, string Field, double Score, string? Conflict, int SeenOn = 0);

    // #25: how people mapped this tag (or set it to ignore) on other tabs; entries still marked
    // auto don't count. Conflict when the tabs disagree, or the field is filled here already.
    // null = no other tab has it.
    public static Match? Seen(string tag, IEnumerable<IReadOnlyList<PointMapEntry>> otherTabs, IReadOnlyList<PointMapEntry> pointMap)
    {
        static bool Ignored(PointMapEntry p) => p.DataGroup.Equals(Normalizer.IgnoredGroup, StringComparison.OrdinalIgnoreCase);
        var mappings = otherTabs
            .Select(map => map.FirstOrDefault(p => p.Auto is null && p.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
            .OfType<PointMapEntry>()
            .GroupBy(p => Ignored(p) ? Normalizer.IgnoredGroup : $"{p.DataGroup} / {p.FieldName}", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (mappings.Count == 0)
            return null;

        var top = mappings[0].First();
        string? conflict = null;
        if (mappings.Count > 1)
            conflict = $"mapped differently on other tabs: {string.Join(", ", mappings.Select(g => $"{g.Key} ({g.Count()})"))}";
        else if (!Ignored(top) && pointMap.FirstOrDefault(p => p.DataGroup.Equals(top.DataGroup, StringComparison.OrdinalIgnoreCase)
                                                             && p.FieldName.Equals(top.FieldName, StringComparison.OrdinalIgnoreCase)) is { } filled)
            conflict = $"{top.DataGroup} / {top.FieldName} already has tag {filled.Tag}";
        return new Match(top.DataGroup, top.FieldName, 1, conflict, mappings[0].Count());
    }

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

    // #26: word swaps people's mappings agree on. GOOD_QTY -> good_quantity and NG_QTY -> ng_quantity
    // both leave qty -> quantity once the shared words are taken out: learned when at least
    // MinAgreeing different tags say so and none says otherwise. Entries still marked auto don't count.
    public const int MinAgreeing = 2;
    public static Dictionary<string, string> LearnSynonyms(IEnumerable<PointMapEntry> pointMaps)
    {
        var none = new Dictionary<string, string>();
        return pointMaps
            .Where(p => p.Auto is null && !p.DataGroup.Equals(Normalizer.IgnoredGroup, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(p => (p.Tag.ToLowerInvariant(), p.FieldName.ToLowerInvariant())) // one tag on many tabs is one example
            .Select(p => (Tag: Words(p.Tag, none), Field: Words(p.FieldName, none)))
            .Select(w => (From: w.Tag.Except(w.Field).ToList(), To: w.Field.Except(w.Tag).ToList()))
            .Where(d => d.From.Count == 1 && d.To.Count == 1)
            .GroupBy(d => d.From[0])
            .Where(g => g.Select(d => d.To[0]).Distinct().Count() == 1 && g.Count() >= MinAgreeing)
            .ToDictionary(g => g.Key, g => g.First().To[0], StringComparer.OrdinalIgnoreCase);
    }

    // Learned synonyms, with the configured ones taking precedence.
    public static Dictionary<string, string> WithLearned(IReadOnlyDictionary<string, string> configured, IEnumerable<PointMapEntry> pointMaps)
    {
        var all = LearnSynonyms(pointMaps);
        foreach (var (word, full) in configured)
            all[word] = full;
        return all;
    }

    // #28: what the value says about the column. Only reorders: Score stays the name's, so a value
    // alone never maps a tag automatically.
    private const double HintBonus = 0.3;
    private static readonly HashSet<string> ResultValues = new(StringComparer.OrdinalIgnoreCase) { "P", "F", "PASS", "FAIL", "OK", "NG", "NOK" };

    private static bool FitsValue(string[] column, string? value) => value?.Trim() is { Length: > 0 } v &&
        (ResultValues.Contains(v) ? column.Contains("result")
         : v.Length >= 8 && (v.Contains(':') || v.Contains('-'))
           && DateTimeOffset.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)
           && column.Length > 0 && column[^1] is "time" or "timestamp" or "date");

    // null when there are no MES columns to compare with. value: the tag's last value, for #28's hints.
    public static Match? Best(string tag, IReadOnlyDictionary<string, IReadOnlyList<string>> mesColumns,
        IReadOnlyDictionary<string, string> synonyms, IReadOnlyList<PointMapEntry> pointMap, string? value = null)
    {
        var name = Words(tag, synonyms);
        var columnWords = mesColumns.SelectMany(g => g.Value).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(c => c, c => Words(c, synonyms), StringComparer.OrdinalIgnoreCase);
        var used = columnWords.Values.SelectMany(w => w.Distinct()).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        double Weight(string word) => Math.Log(1 + (double)columnWords.Count / used.GetValueOrDefault(word, 1)); // a word no column uses counts as rare

        var scored = mesColumns
            .SelectMany(g => g.Value.Select(column =>
            {
                var score = Score(name, columnWords[column], Weight);
                return (Group: g.Key, Field: column, Score: score, Rank: score + (FitsValue(columnWords[column], value) ? HintBonus : 0));
            }))
            .ToList();
        if (scored.Count == 0)
            return null;

        var topRank = scored.Max(s => s.Rank);
        var best = scored.Where(s => s.Rank == topRank).ToList();
        var first = best[0];
        var top = first.Score;

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

    // GOOD_QTY / goodQty / good-qty -> [good, quantity] (with qty=quantity).
    private static string[] Words(string name, IReadOnlyDictionary<string, string> synonyms)
    {
        var spaced = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2");
        return Regex.Split(spaced, "[^A-Za-z0-9]+")
            .Where(w => w.Length > 0)
            .SelectMany(w => (synonyms.TryGetValue(w, out var full) ? full : w).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(w => w.ToLowerInvariant())
            .ToArray();
    }

    // Letters, or whole words weighted by how rare they are (2 x shared / both), whichever is higher.
    private static double Score(string[] tag, string[] column, Func<string, double> weight)
    {
        var letters = Similarity(string.Join('_', tag), string.Join('_', column));
        var total = tag.Distinct().Sum(weight) + column.Distinct().Sum(weight);
        var words = total == 0 ? 0 : 2 * tag.Intersect(column).Sum(weight) / total;
        return Math.Max(letters, words);
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

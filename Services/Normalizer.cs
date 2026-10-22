using EdgeGateway.Models;
using System.Linq;
using System.Text.Json;

namespace EdgeGateway.Services;

// Sorts raw tag/value pairs into one dictionary per MES data group,
// translating tag names into exact Data Dictionary field names.
public class Normalizer
{
    private readonly Dictionary<string, PointMapEntry> _byTag;
    private readonly UnidentifiedTagTracker? _unidentified;

    // Tags mapped to this group are known but never sent (PointMap: "ignore").
    public const string IgnoredGroup = "IGNORED";
    // Never sent to MES (MES has no such table); a tag mapped here counts as not mapped.
    public const string UnidentifiedGroup = "UNIDENTIFIED";

    // A tag "line_no+process_id" is a joined field: the values of those tags,
    // joined with "_" (001 + xxxx = "001_xxxx"). Made only when every part is
    // in the message, so the field never gets half a value.
    public const char JoinMark = '+';
    public const string JoinSeparator = "_";
    private readonly List<(PointMapEntry Point, string[] Parts)> _joins;
    private readonly HashSet<string> _joinParts; // tags used in a joined field: known, not unmapped

    public Normalizer(List<PointMapEntry> pointMap, UnidentifiedTagTracker? unidentified = null)
    {
        _byTag = pointMap.ToDictionary(p => p.Tag, StringComparer.OrdinalIgnoreCase);
        _unidentified = unidentified;
        _joins = pointMap
            .Where(p => p.Tag.Contains(JoinMark) && !p.DataGroup.Equals(UnidentifiedGroup, StringComparison.OrdinalIgnoreCase))
            .Select(p => (p, p.Tag.Split(JoinMark, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
            .ToList();
        _joinParts = new HashSet<string>(_joins.SelectMany(j => j.Parts), StringComparer.OrdinalIgnoreCase);
        _fieldsByGroup = pointMap
            .GroupBy(p => p.DataGroup, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.Select(p => p.FieldName).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, IReadOnlySet<string>> _fieldsByGroup;
    private static readonly IReadOnlySet<string> NoFields = new HashSet<string>();

    // The field names this mapping can put into a data group. A remembered value of
    // any other field is stale (its tag was remapped or set to ignore).
    public IReadOnlySet<string> FieldsOf(string dataGroup) => _fieldsByGroup.GetValueOrDefault(dataGroup) ?? NoFields;

    // Mapped to a real group, set to ignore, or part of a joined field.
    public bool IsMapped(string tag) =>
        (_byTag.TryGetValue(tag, out var point) && !point.DataGroup.Equals(UnidentifiedGroup, StringComparison.OrdinalIgnoreCase))
        || _joinParts.Contains(tag);

    // The tags of a message that the mapping doesn't know (topic_* helpers left
    // out). Each goes on the Unidentified list with its value (logged the first time).
    public List<string> Unmapped(Dictionary<string, object?> rawTags)
    {
        var unmapped = new List<string>();
        foreach (var (tag, value) in rawTags)
        {
            if (tag.StartsWith("topic_", StringComparison.Ordinal) || IsMapped(tag))
                continue;
            unmapped.Add(tag);
            if (_unidentified?.Record(tag, value) ?? true)
                Console.WriteLine($"[normaliser] unmapped tag '{tag}' — map it or set it to ignore on the config page (PointMap > Unidentified)");
        }
        return unmapped;
    }

    public Dictionary<string, Dictionary<string, object?>> Sort(Dictionary<string, object?> rawTags)
    {
        var buckets = new Dictionary<string, Dictionary<string, object?>>();

        // MOTION/IO: the topic already states the one group — no per-tag inference.
        if (rawTags.TryGetValue("topic_data_group", out var g) && g is string dataGroup)
        {
            var fields = new Dictionary<string, object?>();
            foreach (var (tag, value) in rawTags)
            {
                if (tag is "topic_data_group" or "topic_machine_id" or "topic_qualifier")
                    continue;

                fields[_byTag.TryGetValue(tag, out var point) ? point.FieldName : tag] = value;
            }
            buckets[dataGroup.ToUpperInvariant()] = fields;
            return buckets;
        }

        // Everything else (flat snapshots): infer each tag's own group from
        // point-map.json, same as before — one message can fan out into several.
        foreach (var (tag, value) in rawTags)
        {
            string bucketKey, fieldName;

            if (_byTag.TryGetValue(tag, out var point))
            {
                bucketKey = point.DataGroup;
                fieldName = point.FieldName;
            }
            else if (_joinParts.Contains(tag))
            {
                continue; // only used in a joined field (below)
            }
            else
            {
                // Logged the first time only; the config page keeps the count.
                if (_unidentified?.Record(tag, value) ?? true)
                    Console.WriteLine($"[normaliser] unmapped tag '{tag}' — map it on the config page (PointMap > Unidentified)");
                bucketKey = "UNIDENTIFIED";
                fieldName = tag;
            }

            if (!buckets.TryGetValue(bucketKey, out var bucket))
                buckets[bucketKey] = bucket = new Dictionary<string, object?>();

            bucket[fieldName] = value;
        }

        foreach (var (point, parts) in _joins)
        {
            var values = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                var found = rawTags.FirstOrDefault(t => t.Key.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (found.Key is null || found.Value is null)
                    break;
                values.Add(found.Value switch
                {
                    string s => s,
                    double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    bool b => b ? "true" : "false",
                    var other => JsonSerializer.Serialize(other)
                });
            }
            if (values.Count < parts.Length)
                continue; // a part is missing: the field keeps its last value

            if (!buckets.TryGetValue(point.DataGroup, out var bucket))
                buckets[point.DataGroup] = bucket = new Dictionary<string, object?>();
            bucket[point.FieldName] = string.Join(JoinSeparator, values);
        }

        return buckets;
    }
    public static void AppendToJsonStream<T>(string filePath, T newItem)
    {
        // 1. If file is missing or empty, write it as a new array
        if (!File.Exists(filePath) || new FileInfo(filePath).Length <= 2)
        {
            File.WriteAllText(filePath, JsonSerializer.Serialize(new[] { newItem }));
            return;
        }

        // 2. Open file for reading and writing without loading it all into memory
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);

        // Seek to 1 byte before the end of the file to step over the closing bracket ']'
        stream.Seek(-1, SeekOrigin.End);

        // 3. Write a comma separator and serialize the new item directly into the stream
        using var writer = new StreamWriter(stream,new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(","); // Add comma separator
        writer.Write(JsonSerializer.Serialize(newItem));
        writer.Write("]"); // Re-close the array
    }
}

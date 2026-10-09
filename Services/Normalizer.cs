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

    public Normalizer(List<PointMapEntry> pointMap, UnidentifiedTagTracker? unidentified = null)
    {
        _byTag = pointMap.ToDictionary(p => p.Tag, StringComparer.OrdinalIgnoreCase);
        _unidentified = unidentified;
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
            else
            {
                Console.WriteLine($"[normaliser] unmapped tag '{tag}' — map it on the config page (PointMap > Unidentified)");
                _unidentified?.Record(tag, value);
                bucketKey = "UNIDENTIFIED";
                fieldName = tag;
            }

            if (!buckets.TryGetValue(bucketKey, out var bucket))
                buckets[bucketKey] = bucket = new Dictionary<string, object?>();

            bucket[fieldName] = value;
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

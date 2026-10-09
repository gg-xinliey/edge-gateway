using System.Linq;
using System.Text.Json;

namespace EdgeGateway.Services;


//   JSON line -> a full snapshot: {"MACHINE_STATUS":"RUN","GOOD_QTY":125}
//   CSV line  -> one tag per line: "TAG_NAME,value"
public static class PayloadParser
{
    public static Dictionary<string, object?> Parse(string raw)
    {
        raw = raw.Trim();
        return raw.StartsWith('{') ? ParseJson(raw) : ParseCsv(raw);
    }

    private static Dictionary<string, object?> ParseJson(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var result = new Dictionary<string, object?>();

        if (root.TryGetProperty("topic", out var topicEl) && topicEl.ValueKind == JsonValueKind.String
            && root.TryGetProperty("payload", out var payloadEl) && payloadEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in payloadEl.EnumerateObject())
                result[prop.Name] = ParseValue(prop.Value);

            var segments = (topicEl.GetString() ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0) result["topic_machine_id"] = segments[0];
            if (segments.Length > 1) result["topic_data_group"] = segments[1];
            if (segments.Length > 2) result["topic_qualifier"] = segments[2];
        }
        else
        {
            foreach (var prop in root.EnumerateObject())
                result[prop.Name] = ParseValue(prop.Value);
        }

        return result;
    }

    // Recurses into objects/arrays instead of calling GetString() on them —
    // the original version threw on any nested value, which is exactly the
    // shape of the MOTION payload on slide 10 ("world": {"X":..., "Y":...}).
    private static object? ParseValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ParseValue(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ParseValue).ToList(),
        _ => element.GetString()
    };

    private static Dictionary<string, object?> ParseCsv(string raw)
    {
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new FormatException($"malformed CSV line: '{raw}'");

        var tag = parts[0];
        var rawValue = parts[1];
        object? value = double.TryParse(rawValue, out var num) ? num : rawValue;

        return new Dictionary<string, object?> { [tag] = value };
    }
}

using System.Linq;
using System.Text.Json;

namespace EdgeGateway.Services;


//   JSON line -> the message details at the top, the data tags in "data":
//                {"message_id":"…","machine_id":"NXT102","timestamp":"…","data_source":"MCSTATECHANGE",
//                 "data":{"event_name":"MCSTATECHANGE","current_status_code":"5",…}}
//                (the older flat form, tags next to the details, still works:
//                 {"message_id":"…","machine_id":"M1","MACHINE_STATUS":"RUN","GOOD_QTY":125})
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
        return Parse(doc.RootElement);
    }

    // For a line that is already parsed (the gateway parses each line once).
    public static Dictionary<string, object?> Parse(JsonElement root)
    {
        var result = new Dictionary<string, object?>();

        if (root.TryGetProperty("topic", out var topicEl) && topicEl.ValueKind == JsonValueKind.String
            && root.TryGetProperty("payload", out var payloadEl) && TryPayloadObject(payloadEl, out var payloadDoc, out var payload))
        {
            using (payloadDoc)
                foreach (var prop in payload.EnumerateObject())
                    result[prop.Name] = ParseValue(prop.Value);

            var segments = (topicEl.GetString() ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0) result["topic_machine_id"] = segments[0];
            if (segments.Length > 1) result["topic_data_group"] = segments[1];
            if (segments.Length > 2) result["topic_qualifier"] = segments[2];
        }
        else
        {
            // The message details (message_id, machine_id, timestamp, data_source)
            // and, in the flat form, the tags themselves.
            JsonElement? data = null;
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("data") && prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.String)
                    data = prop.Value;
                else
                    result[prop.Name] = ParseValue(prop.Value);
            }

            // "data": the tags (an object, or JSON text holding one). A tag never
            // replaces a message detail of the same name.
            if (data is { } dataEl && TryPayloadObject(dataEl, out var dataDoc, out var tags))
            {
                using (dataDoc)
                    foreach (var prop in tags.EnumerateObject())
                        result.TryAdd(prop.Name, ParseValue(prop.Value));
            }
            else if (data is { } plain)
            {
                result["data"] = ParseValue(plain); // text that isn't an object: just a tag called "data"
            }
        }

        return result;
    }

    // "payload" as an object, or as JSON text holding an object (how the
    // machines send it). document is the parsed text, to dispose; null otherwise.
    private static bool TryPayloadObject(JsonElement payload, out JsonDocument? document, out JsonElement obj)
    {
        document = null;
        obj = payload;
        if (payload.ValueKind == JsonValueKind.Object)
            return true;
        if (payload.ValueKind != JsonValueKind.String)
            return false;

        try
        {
            document = JsonDocument.Parse(payload.GetString() ?? "");
            obj = document.RootElement;
            if (obj.ValueKind == JsonValueKind.Object)
                return true;
        }
        catch (JsonException)
        {
        }

        document?.Dispose();
        document = null;
        return false;
    }

    // Recurses into objects/arrays instead of calling GetString() on them —
    // the original version threw on any nested value, which is exactly the
    // shape of the MOTION payload on slide 10 ("world": {"X":..., "Y":...}).
    // Also used to read saved snapshots back (IntervalSender), so they compare equal.
    internal static object? ParseValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => Number(element),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ParseValue(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ParseValue).ToList(),
        _ => element.GetString()
    };

    // A whole number past 2^53 (serial numbers, counters) does not fit a double exactly:
    // it is kept as a decimal, so MES gets the same digits and a change of 1 is a change (SVI-100).
    // A number past the double range (1e400) is refused: as Infinity it could never be saved
    // or sent, and the machine would get no answer.
    private static object Number(JsonElement element)
    {
        if (!element.TryGetDouble(out var value) || !double.IsFinite(value))
            throw new FormatException("number out of range");
        return Math.Abs(value) >= 9007199254740992 && element.TryGetDecimal(out var exact) && exact == decimal.Truncate(exact)
            ? exact
            : value;
    }

    public static Dictionary<string, object?> ParseCsv(string raw)
    {
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            throw new FormatException($"malformed CSV line: '{raw}'");

        var tag = parts[0];
        var rawValue = parts[1];
        object? value = double.TryParse(rawValue, out var num) && double.IsFinite(num) ? num : rawValue; // "NaN", "1e400": kept as text

        return new Dictionary<string, object?> { [tag] = value };
    }
}

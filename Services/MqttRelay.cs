using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// The Digital Twin path in one object.
//
// Everything from the machine arrives on ONE TCP port (9000). For each line
// you call TryRelay(line):
//
//   * twin line     -> published on its own topic (QoS 0) and TryRelay
//                      returns true. Your MES code must then skip the line.
//   * anything else -> TryRelay returns false. Carry on with the normal
//                      MES pipeline (parser -> normaliser -> envelope -> outbox).
//
// A twin line carries its topic and a payload:
//   {"topic":"svi/TEST/ASSY-L01-FCT-01/IO/input","payload":"{\"timestamp\":\"2026-10-09T11:39:20.133\",...}"}
//
// The front of the topic is free, but it must END in one of four fixed pairs:
//   IO/input   IO/output   motion/position   motion/status
// The ending is matched ignoring case and published in the case above.
// A topic ending in IO/<other> or motion/<other> is dropped with a warning,
// so twin data never leaks into MES.
//
// What is published is the inner payload alone, SpotWelding style:
//   {"timestamp":"2026-10-09 11:39:20.133","totalbits":96,"value":"E77B..."}
public sealed class MqttRelay : IAsyncDisposable
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const int MaxWarnedTopics = 1000;

    private static readonly (string Group, string Name)[] Endings =
    {
        ("IO", "input"),
        ("IO", "output"),
        ("motion", "position"),
        ("motion", "status")
    };

    private readonly MqttPublisher _publisher;

    // Topics already warned about, so a misconfigured machine at 50 msg/s
    // writes one line instead of flooding the console.
    private readonly ConcurrentDictionary<string, byte> _warnedTopics = new();

    public MqttRelay(MqttConnectionConfig config)
    {
        _publisher = new MqttPublisher(config);
    }

    // true  = it was a twin line; it has been handed to MQTT (or dropped), MES must skip it.
    // false = not ours; give it to the MES pipeline.
    // Never throws and never waits for the broker.
    public bool TryRelay(string line)
    {
        // CSV lines and garbage never start with '{': skip the JSON work.
        var text = line.AsSpan().TrimStart();
        if (text.Length == 0 || text[0] != '{')
            return false;

        string topic;
        string? payload;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("topic", out var topicEl) || topicEl.ValueKind != JsonValueKind.String)
                return false;

            var rawTopic = topicEl.GetString() ?? "";
            var normalised = NormaliseTopic(rawTopic, out var isTwin);
            if (!isTwin)
                return false;

            if (normalised is null)
            {
                WarnOnce(rawTopic, "topic must end in IO/input, IO/output, motion/position or motion/status");
                return true;
            }

            topic = normalised;
            payload = ExtractPayload(root);
        }
        catch (JsonException)
        {
            // Not valid JSON: not ours. The MES pipeline will report it.
            return false;
        }

        if (payload is null)
        {
            WarnOnce(topic, "\"payload\" is missing or not a JSON object");
            return true;
        }

        // Fire and forget: the TCP reader must never wait on the broker.
        _ = _publisher.PublishRawAsync(topic, payload);
        return true;
    }

    // ".../IO/input" (any case) -> the same topic with the ending as "IO/input", isTwin = true.
    // ".../IO/<other>" or ".../motion/<other>" -> null, isTwin = true (to be dropped).
    // Anything else -> null, isTwin = false (not twin data).
    private static string? NormaliseTopic(string topic, out bool isTwin)
    {
        isTwin = false;

        var segments = topic.Split('/');
        if (segments.Length < 2)
            return null;

        var group = segments[^2];
        var name = segments[^1];

        foreach (var ending in Endings)
        {
            if (!group.Equals(ending.Group, StringComparison.OrdinalIgnoreCase))
                continue;

            isTwin = true;
            if (!name.Equals(ending.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            segments[^2] = ending.Group;
            segments[^1] = ending.Name;
            return string.Join('/', segments);
        }

        return null;
    }

    // "payload" may be the object itself or the object as a JSON string.
    // Returns null when it is neither.
    private static string? ExtractPayload(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
            return null;

        if (payload.ValueKind == JsonValueKind.Object)
            return Rewrite(payload);

        if (payload.ValueKind != JsonValueKind.String)
            return null;

        try
        {
            using var inner = JsonDocument.Parse(payload.GetString() ?? "");
            return inner.RootElement.ValueKind == JsonValueKind.Object ? Rewrite(inner.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Compact JSON, same property order, "timestamp" as yyyy-MM-dd HH:mm:ss.fff.
    private static string Rewrite(JsonElement obj)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var prop in obj.EnumerateObject())
            {
                if (prop.NameEquals("timestamp") && prop.Value.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(prop.Value.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var ts))
                {
                    if (ts.Kind == DateTimeKind.Utc)
                        ts = ts.ToLocalTime(); // SpotWelding stamps local time
                    writer.WriteString(prop.Name, ts.ToString(TimestampFormat, CultureInfo.InvariantCulture));
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private void WarnOnce(string topic, string reason)
    {
        if (_warnedTopics.Count < MaxWarnedTopics && _warnedTopics.TryAdd(topic, 0))
            Console.WriteLine($"[mqtt] dropped '{topic}': {reason}");
    }

    public ValueTask DisposeAsync() => _publisher.DisposeAsync();
}

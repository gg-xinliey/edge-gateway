using System.Collections.Concurrent;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Stamps each category's bucket according to its own rule:
//   SinglePoint -> one field, set to now
//   Span        -> a start/end pair, only closed out when the trigger
//                  field's value actually changes
public class TimeStamper
{
    private readonly Dictionary<string, TimestampRule> _rules;
    private readonly ConcurrentDictionary<string, (object? value, DateTimeOffset since)> _lastState = new();

    public TimeStamper(Dictionary<string, TimestampRule> rules)
    {
        _rules = rules;
    }

    // at = when it happened on the machine (its "timestamp"); arrival time when it sent none.
    // dataSource keeps spans of different sources apart (same group, separate spans).
    public void Stamp(string machineId, string dataGroup, Dictionary<string, object?> fields, DateTimeOffset? at = null, string dataSource = "")
    {
        var now = at ?? DateTimeOffset.Now;

        if (!_rules.TryGetValue(dataGroup, out var rule))
        {
            fields["record_timestamp"] = FormatIso(now);
            return;
        }

        if (rule.Mode == TimestampMode.SinglePoint)
        {
            fields[rule.TimestampField ?? "record_timestamp"] = FormatIso(now);
            return;
        }

        // Span category: keep the last value we saw and when we first saw
        // it. A change closes the previous span and opens a new one.
        var key = $"{machineId}:{dataSource}:{dataGroup}";
        var newValue = rule.TriggerField is not null ? fields.GetValueOrDefault(rule.TriggerField) : null;

        var state = _lastState.AddOrUpdate(
            key,
            _ => (newValue, now),
            (_, prev) =>
            {
                if (Equals(prev.value, newValue))
                    return prev; // still inside the same span — keep its original start time

                fields["previous_" + rule.TriggerField] = prev.value;
                fields[rule.EndField!] = FormatIso(now); // closes the span that just ended
                return (newValue, now);
            });

        fields[rule.StartField!] = FormatIso(state.since);
    }

    // Every MES time (record/start/end times, publish_timestamp) in UTC with Z,
    // e.g. 2026-10-16T08:25:28.086Z: one zone for all machines and sources.
    // (MQTT keeps SpotWelding's local "yyyy-MM-dd HH:mm:ss.fff"; see MqttRelay.)
    public static string FormatIso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

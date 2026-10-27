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

        if (dataGroup.Equals("ALARM", StringComparison.OrdinalIgnoreCase))
        {
            StampAlarm(key, rule, fields, now, newValue);
            return;
        }

        var state = _lastState.AddOrUpdate(
            key,
            _ => (newValue, now),
            (_, prev) =>
            {
                if (Equals(prev.value, newValue))
                    return prev; // still inside the same span — keep its original start time

                fields[string.IsNullOrWhiteSpace(rule.PreviousField) ? "previous_" + rule.TriggerField : rule.PreviousField] = prev.value;
                fields[rule.EndField!] = FormatIso(now); // closes the span that just ended
                return (newValue, now);
            });

        fields[rule.StartField!] = FormatIso(state.since);
    }

    // MES's ALARM table: start and clear time belong to the alarm itself, not to
    // "the time between changes". Alarm on: start = when it went on (kept while it
    // stays on). Alarm off: the same start + clear time = now. An off with no known
    // on before it (e.g. the first message) gets neither.
    private void StampAlarm(string key, TimestampRule rule, Dictionary<string, object?> fields, DateTimeOffset now, object? value)
    {
        var on = IsOn(value);
        var known = _lastState.TryGetValue(key, out var before);
        var wasOn = known && IsOn(before.value);

        if (on)
        {
            var since = wasOn ? before.since : now;
            _lastState[key] = (value, since);
            fields[rule.StartField!] = FormatIso(since);
            return;
        }

        if (wasOn)
        {
            fields[rule.StartField!] = FormatIso(before.since);
            fields[rule.EndField!] = FormatIso(now);
        }
        _lastState[key] = (value, now);
    }

    private static bool IsOn(object? value) => value switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        decimal m => m != 0,
        string s => !(s.Trim() is "" or "0" or "false" or "False" or "FALSE" or "off" or "OFF" or "Off"),
        _ => true
    };

    // After a restart: the span that was open (its trigger value and start), read
    // back from the outbox, so the next change closes it with the right start
    // time and previous value. Does nothing if the span is already known.
    public void Restore(string machineId, string dataGroup, string dataSource, object? value, DateTimeOffset since) =>
        _lastState.TryAdd($"{machineId}:{dataSource}:{dataGroup}", (value, since));

    public TimestampRule? RuleFor(string dataGroup) => _rules.GetValueOrDefault(dataGroup);

    // Every MES time (record/start/end times, publish_timestamp) in UTC with Z,
    // e.g. 2026-10-16T08:25:28.086Z: one zone for all machines and sources.
    // (MQTT keeps SpotWelding's local "yyyy-MM-dd HH:mm:ss.fff"; see MqttRelay.)
    public static string FormatIso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}

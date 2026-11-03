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
    // sent = the fields this message carried (fields can be the group's snapshot, which still
    // holds an earlier message's times); null = everything in fields was sent now.
    public void Stamp(string machineId, string dataGroup, Dictionary<string, object?> fields, DateTimeOffset? at = null, string dataSource = "",
        IEnumerable<string>? sent = null)
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

        // A start / end time the machine sent itself: UTC with Z too (no zone = UTC).
        foreach (var field in new[] { rule.StartField, rule.EndField })
            if (field is not null && fields.TryGetValue(field, out var value))
                fields[field] = ToUtc(value);

        // ... and it wins: the gateway fills only the times (and previous value) this message didn't bring.
        var mine = sent?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sentStart = Sent(fields, mine, rule.StartField) ? Time(fields[rule.StartField!]) : null;
        var sentEnd = Sent(fields, mine, rule.EndField) ? Time(fields[rule.EndField!]) : null;

        // Span category: keep the last value we saw and when we first saw
        // it. A change closes the previous span and opens a new one.
        var key = $"{machineId}:{dataSource}:{dataGroup}";
        var newValue = rule.TriggerField is not null ? fields.GetValueOrDefault(rule.TriggerField) : null;

        if (dataGroup.Equals("ALARM", StringComparison.OrdinalIgnoreCase))
        {
            StampAlarm(key, rule, fields, now, newValue, mine, sentStart);
            return;
        }

        var state = _lastState.AddOrUpdate(
            key,
            _ => (newValue, sentStart ?? now),
            (_, prev) =>
            {
                if (Equals(prev.value, newValue))
                    return prev; // still inside the same span — keep its original start time

                Fill(fields, mine, string.IsNullOrWhiteSpace(rule.PreviousField) ? "previous_" + rule.TriggerField : rule.PreviousField, prev.value);
                var end = sentEnd ?? now;
                Fill(fields, mine, rule.EndField, FormatIso(end)); // closes the span that just ended
                // A PRODUCTION cycle just ended (its trigger, e.g. cycle_active, was on): its length is the
                // cycle time, unless the machine sends its own cycle_time.
                if (dataGroup.Equals("PRODUCTION", StringComparison.OrdinalIgnoreCase) && IsOn(prev.value) && !fields.ContainsKey("cycle_time") && end >= prev.since)
                    fields["cycle_time"] = Math.Round((end - prev.since).TotalSeconds, 3);
                return (newValue, sentStart ?? now);
            });

        Fill(fields, mine, rule.StartField, FormatIso(state.since));
    }

    // MES's ALARM table: start and clear time belong to the alarm itself, not to
    // "the time between changes". Alarm on: start = when it went on (kept while it
    // stays on). Alarm off: the same start + clear time = now. An off with no known
    // on before it (e.g. the first message) gets neither. Times the message brought itself are kept,
    // and an on's own start time is the start its off gets.
    private void StampAlarm(string key, TimestampRule rule, Dictionary<string, object?> fields, DateTimeOffset now, object? value,
        HashSet<string>? mine, DateTimeOffset? sentStart)
    {
        var on = IsOn(value);
        var known = _lastState.TryGetValue(key, out var before);
        var wasOn = known && IsOn(before.value);

        if (on)
        {
            var since = sentStart ?? (wasOn ? before.since : now);
            _lastState[key] = (value, since);
            Fill(fields, mine, rule.StartField, FormatIso(since));
            return;
        }

        if (wasOn)
        {
            Fill(fields, mine, rule.StartField, FormatIso(before.since));
            Fill(fields, mine, rule.EndField, FormatIso(now));
        }
        _lastState[key] = (value, now);
    }

    // A value this message brought itself (not empty, and not one an earlier message left in the snapshot).
    private static bool Sent(Dictionary<string, object?> fields, HashSet<string>? mine, string? field) =>
        field is not null && (mine is null || mine.Contains(field))
        && fields.GetValueOrDefault(field) is { } v && !(v is string s && string.IsNullOrWhiteSpace(s));

    private static void Fill(Dictionary<string, object?> fields, HashSet<string>? mine, string? field, object? value)
    {
        if (field is not null && !Sent(fields, mine, field))
            fields[field] = value;
    }

    private static DateTimeOffset? Time(object? value) =>
        value is string s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private static bool IsOn(object? value) => value switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        decimal m => m != 0,
        string s => !(s.Trim() is "" or "0" or "false" or "False" or "FALSE" or "off" or "OFF" or "Off"),
        _ => true
    };

    // A time the machine sent as text: to UTC with Z like every MES time (no zone = UTC); anything else stays as sent.
    private static object? ToUtc(object? value) =>
        value is string s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? FormatIso(t) : value;

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

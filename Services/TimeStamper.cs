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
    public void Stamp(string machineId, string dataGroup, Dictionary<string, object?> fields, DateTimeOffset? at = null)
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
        var key = $"{machineId}:{dataGroup}";
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

    public static string FormatIso(DateTimeOffset t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffK");
}

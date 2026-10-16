using System.Collections.Concurrent;
using System.Text.Json;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Sends MES data per data group on an interval instead of per message.
//
// For each machine + data group it keeps the latest values (the snapshot).
//   * A message that changes nothing is ignored.
//   * A message that changes something becomes one record: the full group
//     snapshot after the change, time-stamped (TimeStamper) with the machine's
//     own time. Records wait for the group's next tick.
//   * Each tick (sendEveryMs from timestamp-rules.json, read live) every
//     waiting record is written to the outbox, oldest first. So every change
//     reaches MES, at most one interval late.
//   * Nothing changes, nothing is sent: no periodic repeats. (A 60 s
//     "full snapshot" used to repeat unchanged data with a new event_id and
//     a new record_timestamp, which MES could count as new events.)
//   * When the gateway stops or restarts, waiting records are written first.
public sealed class IntervalSender
{
    public const int DefaultSendEveryMs = 1000;
    private static readonly TimeSpan TickResolution = TimeSpan.FromMilliseconds(20);

    private readonly Outbox _outbox;
    private readonly EnvelopeBuilder _envelopes;
    private readonly Func<ConfigSnapshot> _config;
    private readonly ConcurrentDictionary<(string Machine, string Group), GroupState> _states = new();

    public IntervalSender(Outbox outbox, EnvelopeBuilder envelopes, Func<ConfigSnapshot> config)
    {
        _outbox = outbox;
        _envelopes = envelopes;
        _config = config;
    }

    // Called by the ingest workers for every MES-bound bucket of a message.
    public void Accept(string machineId, string dataGroup, Dictionary<string, object?> fields, DateTimeOffset? eventTime)
    {
        var config = _config();
        var state = _states.GetOrAdd((machineId, dataGroup), _ => new GroupState());
        Dictionary<string, object?> record;

        lock (state)
        {
            if (!ApplyChanges(state.Snapshot, fields))
                return; // same values as last time: nothing new for MES

            record = new Dictionary<string, object?>(state.Snapshot);
            config.TimeStamper.Stamp(machineId, dataGroup, record, eventTime);

            if (SendEveryMs(config, dataGroup) > 0)
            {
                state.Pending.Add(record);
                return;
            }
        }

        _outbox.Write(_envelopes.Build(machineId, dataGroup, record)); // sendEveryMs = 0: right away
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Flush(force: false);
                await Task.Delay(TickResolution, ct);
            }
        }
        finally
        {
            FlushAll(); // stopping: nothing waiting may be lost
        }
    }

    // Writes every waiting record now (gateway stopping / restarting).
    public void FlushAll() => Flush(force: true);

    private void Flush(bool force)
    {
        var config = _config();
        var now = DateTimeOffset.UtcNow;

        foreach (var ((machineId, dataGroup), state) in _states)
        {
            List<Dictionary<string, object?>>? toSend = null;

            lock (state)
            {
                if (!force && now < state.NextDue)
                    continue;
                state.NextDue = now + TimeSpan.FromMilliseconds(Math.Max(SendEveryMs(config, dataGroup), TickResolution.TotalMilliseconds));

                if (state.Pending.Count > 0)
                {
                    toSend = state.Pending;
                    state.Pending = new List<Dictionary<string, object?>>();
                }
            }

            if (toSend is null)
                continue;

            foreach (var record in toSend)
                _outbox.Write(_envelopes.Build(machineId, dataGroup, record));
        }
    }

    private static int SendEveryMs(ConfigSnapshot config, string dataGroup) =>
        config.TimestampRules.TryGetValue(dataGroup, out var rule) && rule.SendEveryMs is { } ms
            ? ms
            : DefaultSendEveryMs;

    // Copies incoming values into the snapshot; true when at least one differs.
    private static bool ApplyChanges(Dictionary<string, object?> snapshot, Dictionary<string, object?> incoming)
    {
        var changed = false;
        foreach (var (name, value) in incoming)
        {
            if (snapshot.TryGetValue(name, out var old) && SameValue(old, value))
                continue;
            snapshot[name] = value;
            changed = true;
        }

        return changed;
    }

    // Values come from PayloadParser: double, bool, string, null, or nested
    // dictionaries / lists. Nested ones are compared by their JSON text.
    private static bool SameValue(object? a, object? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a is Dictionary<string, object?> || a is List<object?> || b is Dictionary<string, object?> || b is List<object?>)
            return JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
        return a.Equals(b);
    }

    private sealed class GroupState
    {
        public Dictionary<string, object?> Snapshot = new();
        public List<Dictionary<string, object?>> Pending = new();
        public DateTimeOffset NextDue = DateTimeOffset.MinValue;    }
}

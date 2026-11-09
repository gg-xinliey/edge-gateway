using System.Collections.Concurrent;

namespace EdgeGateway.Services;

// Every MES message held right now (not processed, not answered: the adapter keeps it and sends it
// again), with why, for the config page's Log > Held. One entry per message (machine + data_source +
// message_id): a resend of the same message counts up instead of adding a row. An entry goes by
// itself once that message goes through (ack) or is rejected (nack). Kept in memory for the life of
// the process: after a restart, messages still held come back as their adapters send them again.
// At most MaxEntries (port 9000 has no login), the oldest dropped first.
public sealed class HeldMessages
{
    public const int MaxEntries = 5_000;

    private readonly ConcurrentDictionary<string, Entry> _held = new(StringComparer.Ordinal);
    private readonly ThrottledLog _full = new(TimeSpan.FromSeconds(60));

    private sealed class Entry
    {
        public required string MachineId, MessageId, Reason, Connection;
        public string? DataSource;
        public DateTimeOffset FirstHeld, LastHeld;
        public long Times;
    }

    public int Count => _held.Count;

    // key: the message's AckTracker key (GatewayHost.RepeatKey).
    public void Hold(string key, string machineId, string? dataSource, string messageId, string reason, string connection)
    {
        var now = DateTimeOffset.Now;
        var entry = _held.GetOrAdd(key, _ => new Entry { MachineId = machineId, MessageId = messageId, Reason = reason, Connection = connection, FirstHeld = now });
        lock (entry)
        {
            entry.DataSource = dataSource;
            entry.Reason = reason; // the latest: a tag mapped meanwhile leaves the others
            entry.Connection = connection;
            entry.LastHeld = now;
            entry.Times++;
        }

        if (_held.Count > MaxEntries)
        {
            var dropped = 0;
            foreach (var oldest in _held.OrderBy(e => e.Value.FirstHeld).Take(_held.Count - MaxEntries).ToList())
                if (_held.TryRemove(oldest.Key, out _))
                    dropped++;
            _full.Write("full", $"[held] more than {MaxEntries:N0} messages held: the oldest {dropped} are no longer listed on Log > Held (they are still held)");
        }
    }

    // Went through (ack) or was rejected (nack): no longer held.
    public void Clear(string key) => _held.TryRemove(key, out _);

    // Newest first.
    public object Snapshot() =>
        _held.Values.Select(e =>
        {
            lock (e)
                return new { machineId = e.MachineId, dataSource = e.DataSource, messageId = e.MessageId, reason = e.Reason,
                             connection = e.Connection, firstHeld = e.FirstHeld, lastHeld = e.LastHeld, times = e.Times };
        }).OrderByDescending(e => e.lastHeld).ToList();
}

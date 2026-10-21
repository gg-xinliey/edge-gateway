using System.Collections.Concurrent;

namespace EdgeGateway.Services;

// Machines whose MES messages are held because they contain tags the mapping
// doesn't know. Nothing goes to MES under UNIDENTIFIED (MES has no such
// group): the whole message waits, not answered, so the adapter keeps it.
//
// Once every one of those tags is mapped or set to ignore (PointMap save), the
// machine is released: the gateway closes its connection, the adapter sends
// the held messages again and they go through complete.
public sealed class UnmappedHolds
{
    private readonly ConcurrentDictionary<string, Entry> _machines = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public Entry(string id, DateTimeOffset now) { Id = id; FirstSeen = now; }
        public string Id { get; }
        public string? Mapping;                  // the machine_id of the tab whose mapping applies; null = Template / shared
        public string Connection = "";
        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen;
        public long Lines;
        public readonly HashSet<string> Tags = new(StringComparer.OrdinalIgnoreCase); // still unmapped
    }

    public int Count => _machines.Count;

    public void Hold(string machineId, string? mapping, string connection, IEnumerable<string> unmappedTags)
    {
        var now = DateTimeOffset.Now;
        var entry = _machines.GetOrAdd(machineId, id => new Entry(id, now));
        lock (entry)
        {
            entry.Mapping = mapping;
            entry.Connection = connection;
            entry.LastSeen = now;
            entry.Lines++;
            foreach (var tag in unmappedTags)
                entry.Tags.Add(tag);
        }
    }

    // After a mapping was saved: drops the tags it now maps. Machines with none
    // left are released; returns their connections (to close, so they resend).
    public List<string> Resolve(string? mapping, Func<string, bool> isMapped)
    {
        var connections = new List<string>();
        foreach (var entry in _machines.Values)
        {
            lock (entry)
            {
                if (!string.Equals(entry.Mapping, mapping, StringComparison.OrdinalIgnoreCase))
                    continue;
                entry.Tags.RemoveWhere(tag => isMapped(tag));
                if (entry.Tags.Count > 0)
                    continue;
            }
            if (_machines.TryRemove(new KeyValuePair<string, Entry>(entry.Id, entry)))
                connections.Add(entry.Connection);
        }
        return connections.Distinct().ToList();
    }

    public object Snapshot() =>
        _machines.Values.Select(e =>
        {
            lock (e)
                return new
                {
                    machineId = e.Id, mapping = e.Mapping, connection = e.Connection, firstSeen = e.FirstSeen,
                    lastSeen = e.LastSeen, lines = e.Lines, tags = e.Tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList()
                };
        }).OrderBy(e => e.machineId, StringComparer.OrdinalIgnoreCase).ToList();
}

using System.Collections.Concurrent;

namespace EdgeGateway.Services;

// Machines that send MES data but have no tab (no own mapping) while
// GatewayConfig unregisteredMachines = "hold". Their lines are not processed
// and not answered, so the adapter keeps them and sends them again once the
// machine is registered. This list is what the config page shows (and pops up
// about), with the tags each one sent, so mapping it is quick.
//
// Lives as long as the process. Registering a machine removes it from here and
// its tags go to the new tab's Unidentified list.
public sealed class HeldMachines
{
    private const int MaxMachines = 200;
    private const int MaxTagsPerMachine = 300;

    private readonly ConcurrentDictionary<string, Entry> _machines = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public Entry(string id, DateTimeOffset now) { Id = id; FirstSeen = now; }
        public string Id { get; }
        public DateTimeOffset FirstSeen { get; }
        public DateTimeOffset LastSeen;
        public long Lines;
        public string Connection = "";
        public readonly HashSet<string> DataSources = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, object?> Tags = new(StringComparer.OrdinalIgnoreCase); // tag -> last value
    }

    public int Count => _machines.Count;

    // The connection (ip:port) a held machine last sent on, or null.
    public string? ConnectionOf(string machineId)
    {
        if (!_machines.TryGetValue(machineId, out var entry))
            return null;
        lock (entry)
            return entry.Connection;
    }

    // One held line. true = the first one from this machine (worth a notice).
    public bool Hold(string machineId, string connection, string? dataSource, IReadOnlyDictionary<string, object?> tags)
    {
        var now = DateTimeOffset.Now;
        if (_machines.Count >= MaxMachines && !_machines.ContainsKey(machineId))
            return false;

        var isNew = false;
        var entry = _machines.GetOrAdd(machineId, id => { isNew = true; return new Entry(id, now); });
        lock (entry)
        {
            entry.LastSeen = now;
            entry.Lines++;
            entry.Connection = connection;
            if (!string.IsNullOrWhiteSpace(dataSource))
                entry.DataSources.Add(dataSource);
            foreach (var (tag, value) in tags)
                if (entry.Tags.ContainsKey(tag) || entry.Tags.Count < MaxTagsPerMachine)
                    entry.Tags[tag] = value;
        }
        return isNew;
    }

    // Registered: no longer held. Returns the tags it sent (tag -> last value).
    public IReadOnlyDictionary<string, object?> Release(string machineId)
    {
        if (!_machines.TryRemove(machineId, out var entry))
            return new Dictionary<string, object?>();
        lock (entry)
            return new Dictionary<string, object?>(entry.Tags);
    }

    public object Snapshot() =>
        _machines.Values.Select(e =>
        {
            lock (e)
                return new
                {
                    machineId = e.Id, firstSeen = e.FirstSeen, lastSeen = e.LastSeen, lines = e.Lines,
                    connection = e.Connection, dataSources = e.DataSources.OrderBy(s => s).ToList(),
                    tags = e.Tags.Keys.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList()
                };
        }).OrderBy(e => e.machineId, StringComparer.OrdinalIgnoreCase).ToList();
}

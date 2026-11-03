using System.Collections.Concurrent;

namespace EdgeGateway.Services;

// Who is sending, and how much: per TCP connection and per machine_id.
// Feeds the config page's Machines view (/api/machines). Lives as long as the
// gateway process, so a restart from the page keeps the counts; closing the
// exe resets them.
//
//   connection: ip:port, open/closed, since, lines received, lines rejected
//               (no machine_id / no message_id), duplicates (message_id seen
//               in the last 10 min), lines that could not be read, last line
//   machine:    which connection it last used (so: connected or not), first
//               and last line (repeats too: it is still sending), messages total /
//               to MES / to MQTT, repeats and time warnings of the last 10 min
//               (only issues still going on), messages per second (last 10 s), data_sources seen
public sealed class TrafficRegistry
{
    private const int MaxClosedConnections = 200;
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, Machine> _machines = new(StringComparer.OrdinalIgnoreCase);

    // ---- called by TcpIngestServer ----

    public void Opened(string endpoint) => _connections[endpoint] = new Connection(endpoint);

    public void Closed(string endpoint)
    {
        if (_connections.TryGetValue(endpoint, out var c))
            lock (c) { c.Open = false; c.ClosedAt = DateTimeOffset.Now; }
        PruneClosed();
    }

    public void LineReceived(string endpoint)
    {
        if (_connections.TryGetValue(endpoint, out var c))
            lock (c) { c.Lines++; c.LastLine = DateTimeOffset.Now; }
    }

    public void Unreadable(string endpoint)
    {
        if (_connections.TryGetValue(endpoint, out var c))
            lock (c) c.Unreadable++;
    }

    // ---- called by GatewayHost ----

    public void Rejected(string endpoint)
    {
        if (_connections.TryGetValue(endpoint, out var c))
            lock (c) c.Rejected++;
    }

    // A line whose message_id was seen in the last 10 min (ignored). The machine is still
    // sending, on this connection: it counts as connected (also when it only resends after a
    // reconnect or a gateway restart).
    public void Duplicate(string endpoint, string? machineId)
    {
        _connections.TryGetValue(endpoint, out var c);
        if (c is not null)
            lock (c) c.Duplicates++;
        if (string.IsNullOrWhiteSpace(machineId))
            return;

        var now = DateTimeOffset.Now;
        var m = _machines.GetOrAdd(machineId, id => new Machine(id, now));
        lock (m)
        {
            m.LastEndpoint = endpoint;
            m.LastMessage = now;
            m.Duplicates++;
            m.Repeats.Add(now);
        }
        if (c is not null)
            lock (c) c.Machines.Add(machineId);
    }

    // A new message, accepted. kind: "MES" or "MQTT". Counted once: repeats
    // (Duplicate) and held tries (Held) are not; a message that was held is
    // counted here when it finally goes through.
    public void Message(string machineId, string endpoint, string kind, string? dataSource, string? messageId = null)
    {
        var now = DateTimeOffset.Now;
        var m = _machines.GetOrAdd(machineId, id => new Machine(id, now));
        lock (m)
        {
            m.LastEndpoint = endpoint;
            m.LastMessage = now;
            m.Total++;
            if (kind == "MQTT") m.Mqtt++; else m.Mes++;
            if (messageId is not null)
                m.HeldIds.Remove(messageId); // it waited, now it is through
            if (!string.IsNullOrWhiteSpace(dataSource))
                m.DataSources[dataSource] = m.DataSources.GetValueOrDefault(dataSource) + 1;
            m.CountSecond(now);
        }

        if (_connections.TryGetValue(endpoint, out var c))
            lock (c) c.Machines.Add(machineId);
    }

    // A message held (no tab yet, or tags not mapped): not counted as a message.
    // "held" shows how many different messages are waiting right now.
    public void Held(string machineId, string endpoint, string messageId)
    {
        var now = DateTimeOffset.Now;
        var m = _machines.GetOrAdd(machineId, id => new Machine(id, now));
        lock (m)
        {
            m.LastEndpoint = endpoint;
            m.LastMessage = now;
            if (m.HeldIds.Count < MaxHeldIds)
                m.HeldIds.Add(messageId);
        }

        if (_connections.TryGetValue(endpoint, out var c))
            lock (c) c.Machines.Add(machineId);
    }

    private const int MaxHeldIds = 10_000;

    // A message whose "timestamp" can't be read, or is over an hour from its arrival.
    public void BadTime(string machineId)
    {
        var now = DateTimeOffset.Now;
        var m = _machines.GetOrAdd(machineId, id => new Machine(id, now));
        lock (m) { m.BadTimes++; m.TimeWarnings.Add(now); }
    }

    // ---- read by the config page ----

    public object Snapshot()
    {
        var now = DateTimeOffset.Now;
        var connections = _connections.Values.Select(c =>
        {
            lock (c)
                return new
                {
                    endpoint = c.Endpoint, open = c.Open, connectedAt = c.ConnectedAt, closedAt = c.ClosedAt,
                    lines = c.Lines, rejected = c.Rejected, duplicates = c.Duplicates, unreadable = c.Unreadable, lastLine = c.LastLine,
                    machines = c.Machines.OrderBy(x => x).ToList()
                };
        }).OrderByDescending(c => c.open).ThenBy(c => c.endpoint).ToList();

        var machines = _machines.Values.Select(m =>
        {
            lock (m)
            {
                _connections.TryGetValue(m.LastEndpoint, out var c);
                bool open; DateTimeOffset? closedAt;
                if (c is null) { open = false; closedAt = null; }
                else lock (c) { open = c.Open; closedAt = c.ClosedAt; }

                // Sending (a line, new or repeat, in the last 60 s; also an adapter that connects per
                // message and closed already) / connected but quiet / gone.
                var status = now - m.LastMessage <= IdleAfter ? "connected" : open ? "idle" : "disconnected";
                return new
                {
                    machineId = m.Id, status, connection = m.LastEndpoint, disconnectedAt = open ? null : closedAt,
                    firstSeen = m.FirstSeen, lastMessage = m.LastMessage,
                    total = m.Total, mes = m.Mes, mqtt = m.Mqtt, held = m.HeldIds.Count, duplicates = m.Duplicates, badTimes = m.BadTimes, perSecond = m.PerSecond(now),
                    repeats = m.Repeats.Count(now), timeWarnings = m.TimeWarnings.Count(now), // last 10 min: issues still going on
                    dataSources = m.DataSources.OrderByDescending(d => d.Value).Select(d => new { name = d.Key, count = d.Value }).ToList()
                };
            }
        }).OrderBy(m => m.status == "connected" ? 0 : m.status == "idle" ? 1 : 2).ThenBy(m => m.machineId).ToList();

        return new { machines, connections };
    }

    // How many in the last 10 minutes, in one-minute buckets: an issue that stopped clears by itself.
    public sealed class Recent
    {
        private readonly long[] _minutes = new long[10];
        private long _current; // the minute (Unix) of the newest bucket

        public void Add(DateTimeOffset now)
        {
            Advance(now);
            _minutes[_current % _minutes.Length]++;
        }

        public long Count(DateTimeOffset now)
        {
            Advance(now);
            return _minutes.Sum();
        }

        private void Advance(DateTimeOffset now)
        {
            var minute = now.ToUnixTimeSeconds() / 60;
            if (minute <= _current) return;
            if (minute - _current >= _minutes.Length) Array.Clear(_minutes);
            else for (var m = _current + 1; m <= minute; m++) _minutes[m % _minutes.Length] = 0;
            _current = minute;
        }
    }

    private void PruneClosed()
    {
        var closed = _connections.Values.Where(c => !c.Open).OrderBy(c => c.ClosedAt).ToList();
        foreach (var c in closed.Take(Math.Max(0, closed.Count - MaxClosedConnections)))
            _connections.TryRemove(c.Endpoint, out _);
    }

    private sealed class Connection
    {
        public Connection(string endpoint) { Endpoint = endpoint; }
        public string Endpoint { get; }
        public bool Open = true;
        public DateTimeOffset ConnectedAt = DateTimeOffset.Now;
        public DateTimeOffset? ClosedAt;
        public DateTimeOffset? LastLine;
        public long Lines, Rejected, Duplicates, Unreadable;
        public HashSet<string> Machines { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Machine
    {
        private readonly long[] _seconds = new long[10]; // messages per second, last 10 s
        private long _currentSecond;

        public Machine(string id, DateTimeOffset now) { Id = id; FirstSeen = now; }
        public string Id { get; }
        public string LastEndpoint = "";
        public DateTimeOffset FirstSeen;
        public DateTimeOffset LastMessage;
        public long Total, Mes, Mqtt, Duplicates, BadTimes;                        // Total = new messages accepted (each once)
        public HashSet<string> HeldIds { get; } = new(StringComparer.Ordinal);    // message_ids waiting (held) right now
        public Recent Repeats { get; } = new();
        public Recent TimeWarnings { get; } = new();
        public Dictionary<string, long> DataSources { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void CountSecond(DateTimeOffset now)
        {
            Advance(now.ToUnixTimeSeconds());
            _seconds[_currentSecond % _seconds.Length]++;
        }

        public double PerSecond(DateTimeOffset now)
        {
            Advance(now.ToUnixTimeSeconds());
            return _seconds.Sum() / (double)_seconds.Length;
        }

        // Clears the buckets of seconds that passed without messages.
        private void Advance(long second)
        {
            if (_currentSecond == 0) { _currentSecond = second; return; }
            for (var s = _currentSecond + 1; s <= second && s - _currentSecond <= _seconds.Length; s++)
                _seconds[s % _seconds.Length] = 0;
            if (second - _currentSecond > _seconds.Length)
                Array.Clear(_seconds);
            if (second > _currentSecond)
                _currentSecond = second;
        }
    }
}

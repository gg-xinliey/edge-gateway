using System.Collections.Concurrent;
using System.Globalization;

namespace EdgeGateway.Services;

// Duplicate check on "message_id": every line carries one, and a message_id
// seen in the last 10 minutes means the same line came again (e.g. a source
// resending after a reconnect). That line is ignored before anything else.
//
// Lives as long as the gateway process, so a restart from the config page
// keeps it. On a clean stop (Ctrl+C) the ids are saved to a file and loaded on
// the next start, so a source resending right after a gateway restart is
// still caught. A hard kill loses them.
//
// Memory: about 170 bytes per id; 1,000 new ids/s for 10 min is ~100 MB.
public sealed class MessageIdFilter
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, long> _seen = new(StringComparer.Ordinal); // id -> first seen (UTC ticks)
    private readonly ConcurrentQueue<(string Id, long At)> _order = new();                    // oldest first, for expiry
    private readonly object _sweep = new();
    private long _lastSweep;

    public int Count => _seen.Count;

    // true = first time in the window: go on with the line.
    // false = seen within the window: a duplicate, ignore the line.
    public bool TryAccept(string id)
    {
        var now = DateTime.UtcNow.Ticks;
        SweepIfDue(now);

        if (_seen.TryAdd(id, now))
        {
            _order.Enqueue((id, now));
            return true;
        }

        if (_seen.TryGetValue(id, out var at) && now - at < Window.Ticks)
            return false;

        // Seen longer ago than the window but not swept yet: new again.
        _seen[id] = now;
        _order.Enqueue((id, now));
        return true;
    }

    // The line was rejected after the check (e.g. no machine_id): forget its id,
    // so the corrected line sent again with the same id is not taken as a duplicate.
    public void Forget(string id) => _seen.TryRemove(id, out _);

    // Drops ids older than the window, at most once a second, one thread at a time.
    private void SweepIfDue(long now)
    {
        if (now - Interlocked.Read(ref _lastSweep) < TimeSpan.TicksPerSecond || !Monitor.TryEnter(_sweep))
            return;
        try
        {
            _lastSweep = now;
            var cutoff = now - Window.Ticks;
            while (_order.TryPeek(out var head) && head.At < cutoff)
            {
                _order.TryDequeue(out head);
                // Only when it was not seen again since (the newer entry is further back).
                _seen.TryRemove(new KeyValuePair<string, long>(head.Id, head.At));
            }
        }
        finally
        {
            Monitor.Exit(_sweep);
        }
    }

    // One line per id: "<UTC ticks>\t<id>". Only ids still inside the window.
    // Returns how many were saved.
    public int Save(string path)
    {
        var cutoff = DateTime.UtcNow.Ticks - Window.Ticks;
        var lines = _seen.Where(e => e.Value >= cutoff)
            .OrderBy(e => e.Value)
            .Select(e => e.Value.ToString(CultureInfo.InvariantCulture) + "\t" + e.Key)
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return lines.Count;
    }

    // Ids older than the window (e.g. the gateway was off for a while) are skipped.
    public static MessageIdFilter Load(string path)
    {
        var filter = new MessageIdFilter();
        if (!File.Exists(path))
            return filter;

        var cutoff = DateTime.UtcNow.Ticks - Window.Ticks;
        foreach (var line in File.ReadLines(path))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !long.TryParse(line.AsSpan(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out var at) || at < cutoff)
                continue;
            var id = line[(tab + 1)..];
            if (filter._seen.TryAdd(id, at))
                filter._order.Enqueue((id, at));
        }
        return filter;
    }
}

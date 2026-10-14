using System.Collections.Concurrent;

namespace EdgeGateway.Services;

// Logs a problem at most once per `every` for each key (e.g. one sender);
// repeats in between are only counted and reported with the next line:
//   "[ingest] ... (+1200 more like this in the last 60 s)"
// With 50 machines at thousands of lines/s, logging every bad line would
// flood the console, the log file and the config page.
public sealed class ThrottledLog
{
    private const int MaxKeys = 10_000;

    private readonly TimeSpan _every;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public ThrottledLog(TimeSpan every)
    {
        _every = every;
    }

    public void Write(string key, string message)
    {
        if (_entries.Count >= MaxKeys && !_entries.ContainsKey(key))
            key = "(other)"; // many distinct keys: share one entry instead of growing forever

        var entry = _entries.GetOrAdd(key, _ => new Entry());
        long suppressed;
        lock (entry)
        {
            var now = DateTime.UtcNow;
            if (now - entry.Last < _every)
            {
                entry.Suppressed++;
                return;
            }

            suppressed = entry.Suppressed;
            entry.Suppressed = 0;
            entry.Last = now;
        }

        Console.WriteLine(suppressed > 0
            ? $"{message} (+{suppressed} more like this in the last {_every.TotalSeconds:0} s)"
            : message);
    }

    private sealed class Entry
    {
        public DateTime Last = DateTime.MinValue;
        public long Suppressed;
    }
}

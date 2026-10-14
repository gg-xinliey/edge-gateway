using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace EdgeGateway.Services;

// Remembers tags that arrived but are not in point-map.json, so the config
// page can list them for mapping. Lives for the whole process (survives
// config reloads and gateway restarts). Capped so a sender spraying random
// keys cannot grow it forever.
public sealed class UnidentifiedTagTracker
{
    private const int MaxTags = 500;

    private readonly ConcurrentDictionary<string, UnidentifiedTag> _tags = new(StringComparer.OrdinalIgnoreCase);

    // Returns true the first time a tag is seen (so the caller logs it once, not per message).
    public bool Record(string tag, object? value)
    {
        if (_tags.Count >= MaxTags && !_tags.ContainsKey(tag))
            return false;

        var isNew = !_tags.ContainsKey(tag);

        var now = DateTimeOffset.Now;
        var text = value switch
        {
            null => "null",
            string s => s,
            double d => d.ToString(CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(value)
        };

        _tags.AddOrUpdate(
            tag,
            _ => new UnidentifiedTag(tag, text, 1, now, now),
            (_, prev) => prev with { LastValue = text, Count = prev.Count + 1, LastSeen = now });
        return isNew;
    }

    // Newest first.
    public IReadOnlyList<UnidentifiedTag> Snapshot() =>
        _tags.Values.OrderByDescending(t => t.LastSeen).ToList();

    // Called after point-map.json is reloaded: mapped tags are no longer unidentified.
    public void Forget(IEnumerable<string> mappedTags)
    {
        foreach (var tag in mappedTags)
            _tags.TryRemove(tag, out _);
    }
}

public sealed record UnidentifiedTag(string Tag, string LastValue, long Count, DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

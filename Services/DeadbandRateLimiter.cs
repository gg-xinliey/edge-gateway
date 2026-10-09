using System.Collections.Concurrent;
using System.Text.Json;

namespace EdgeGateway.Services;

// Decides whether a twin-bound record is worth publishing. Two modes,
// because "on change" means something different for the two payload shapes
// on slide 10:
//   - Motion (world/pulse/speed/force): continuous numeric values, so only
//     publish once a tracked number has moved by >= deadband.
//   - IO / status: discrete values, so publish whenever the serialized
//     record differs from what was last sent.
// Both are capped at minInterval regardless of how much changed — the
// "maximum rate" half of slide 10's rule. Per-key state (recommend keying
// by machine_id + topic), so one instance tracks every point the gateway
// publishes.
public class DeadbandRateLimiter
{
    private readonly ConcurrentDictionary<string, (Dictionary<string, double> numeric, DateTimeOffset at)> _lastNumeric = new();
    private readonly ConcurrentDictionary<string, (string serialized, DateTimeOffset at)> _lastSerialized = new();

    public bool ShouldPublishMotion(string key, Dictionary<string, object?> fields, double deadband, TimeSpan minInterval)
    {
        var now = DateTimeOffset.UtcNow;
        var numeric = FlattenNumeric(fields);

        if (_lastNumeric.TryGetValue(key, out var prev))
        {
            if (now - prev.at < minInterval)
                return false;

            var changed = numeric.Any(kv => !prev.numeric.TryGetValue(kv.Key, out var old) || Math.Abs(kv.Value - old) >= deadband);
            if (!changed)
                return false;
        }

        _lastNumeric[key] = (numeric, now);
        return true;
    }

    public bool ShouldPublishOnChange(string key, Dictionary<string, object?> fields, TimeSpan minInterval)
    {
        var now = DateTimeOffset.UtcNow;
        var serialized = JsonSerializer.Serialize(fields);

        if (_lastSerialized.TryGetValue(key, out var prev))
        {
            if (now - prev.at < minInterval)
                return false;

            if (serialized == prev.serialized)
                return false;
        }

        _lastSerialized[key] = (serialized, now);
        return true;
    }

    // Walks one level of nesting (world.X, pulse.Y, ...) — enough for the
    // motion payload shape on slide 10.
    private static Dictionary<string, double> FlattenNumeric(Dictionary<string, object?> fields)
    {
        var result = new Dictionary<string, double>();
        foreach (var (k, v) in fields)
        {
            if (v is double d)
                result[k] = d;
            else if (v is Dictionary<string, object?> nested)
                foreach (var (nk, nv) in nested)
                    if (nv is double nd)
                        result[$"{k}.{nk}"] = nd;
        }
        return result;
    }
}

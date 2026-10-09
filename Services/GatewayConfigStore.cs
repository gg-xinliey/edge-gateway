using System.Text.Json;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

/// <summary>
/// Owns the two runtime configuration files already used by Program.cs and provides
/// an atomic-ish snapshot for message processing while the UI reloads configuration.
/// </summary>
public sealed class GatewayConfigStore
{
    private readonly string _configRoot;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly UnidentifiedTagTracker? _unidentified;
    private readonly object _sync = new();
    private ConfigSnapshot _current;

    public GatewayConfigStore(string configRoot, JsonSerializerOptions jsonOptions, UnidentifiedTagTracker? unidentified = null)
    {
        _configRoot = Path.IsPathRooted(configRoot)
            ? Path.GetFullPath(configRoot)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configRoot));
        _jsonOptions = jsonOptions;
        _unidentified = unidentified;
        _current = LoadSnapshot();
    }

    // The one Config folder everything reads and the config page writes,
    // next to the exe (not the working directory).
    public string ConfigRoot => _configRoot;

    public ConfigSnapshot Current
    {
        get
        {
            lock (_sync)
                return _current;
        }
    }

    public void Reload()
    {
        var next = LoadSnapshot();
        lock (_sync)
            _current = next;

        _unidentified?.Forget(next.PointMap.Select(p => p.Tag));
        Console.WriteLine("[config] runtime configuration reloaded");
    }

    private ConfigSnapshot LoadSnapshot()
    {
        var pointMapPath = Path.Combine(_configRoot, "point-map.json");
        var timestampRulesPath = Path.Combine(_configRoot, "timestamp-rules.json");

        var pointMap = JsonSerializer.Deserialize<List<PointMapEntry>>(
            File.ReadAllText(pointMapPath), _jsonOptions)
            ?? throw new InvalidOperationException("Config/point-map.json did not contain a valid list.");

        var timestampRules = JsonSerializer.Deserialize<Dictionary<string, TimestampRule>>(
            File.ReadAllText(timestampRulesPath), _jsonOptions)
            ?? throw new InvalidOperationException("Config/timestamp-rules.json did not contain a valid object.");

        return new ConfigSnapshot(
            pointMap,
            timestampRules,
            new Normalizer(pointMap, _unidentified),
            new TimeStamper(timestampRules));
    }
}

public sealed record ConfigSnapshot(
    IReadOnlyList<PointMapEntry> PointMap,
    IReadOnlyDictionary<string, TimestampRule> TimestampRules,
    Normalizer Normalizer,
    TimeStamper TimeStamper);

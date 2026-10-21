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
    private readonly string _mesSchemaRoot;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly UnidentifiedTagTracker? _unidentified;
    private readonly object _sync = new();
    private ConfigSnapshot _current;

    // configRoot: the shared Config folder, or one machine's own Config/machines/<id>
    // (point-map.json + timestamp-rules.json). mesSchemaRoot: where mes-schema.json
    // lives (always the shared Config folder). label: for log lines, e.g. "NXT101".
    public GatewayConfigStore(string configRoot, JsonSerializerOptions jsonOptions, UnidentifiedTagTracker? unidentified = null,
        string? mesSchemaRoot = null, string? label = null)
    {
        _configRoot = Path.IsPathRooted(configRoot)
            ? Path.GetFullPath(configRoot)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configRoot));
        _mesSchemaRoot = mesSchemaRoot is null ? _configRoot : Path.GetFullPath(mesSchemaRoot);
        _jsonOptions = jsonOptions;
        _unidentified = unidentified;
        Label = label;
        _current = LoadSnapshot();
    }

    // The Config folder this store reads and the config page writes, next to
    // the exe (not the working directory).
    public string ConfigRoot => _configRoot;

    // null = the shared config; else the machine_id this mapping belongs to.
    public string? Label { get; }

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
        Console.WriteLine(Label is null ? "[config] runtime configuration reloaded" : $"[config] {Label}: own mapping reloaded");
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
            new TimeStamper(timestampRules),
            LoadMesColumns());
    }

    // Config/mes-schema.json: per data group, the columns of its MES table (from
    // the MES data dictionary). Every one of them goes into the payload, null
    // when not sent. Missing file = payloads go out as they are.
    private IReadOnlyDictionary<string, IReadOnlyList<string>> LoadMesColumns()
    {
        var path = Path.Combine(_mesSchemaRoot, "mes-schema.json");
        if (!File.Exists(path))
        {
            Console.WriteLine("[config] Config/mes-schema.json not found — MES payloads are not completed with null columns");
            return new Dictionary<string, IReadOnlyList<string>>();
        }

        var groups = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path), _jsonOptions)
            ?? throw new InvalidOperationException("Config/mes-schema.json did not contain a valid object.");
        return groups.ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Value, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record ConfigSnapshot(
    IReadOnlyList<PointMapEntry> PointMap,
    IReadOnlyDictionary<string, TimestampRule> TimestampRules,
    Normalizer Normalizer,
    TimeStamper TimeStamper,
    IReadOnlyDictionary<string, IReadOnlyList<string>> MesColumns);

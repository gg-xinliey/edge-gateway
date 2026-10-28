using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EdgeGateway.Services;

// Which machines have their own mapping, and their config.
//
// A machine with a folder Config/machines/<machine_id>/ (point-map.json +
// timestamp-rules.json) gets its own pipeline: own normalizing, own interval
// state and spans, own outbox (outbox/<machine_id>/) and own MES sender. So two
// machines can use the same field name with different meanings.
// Every other machine uses the shared config in Config/ (as before).
// mes-schema.json, machine-config.json and mqtt-config.json stay shared.
//
// Lives as long as the process; the config page adds and removes machines.
public sealed class MachineRegistry
{
    public const string FolderName = "machines";
    private static readonly Regex ValidId = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly string[] MappingFiles = { "point-map.json", "timestamp-rules.json" };

    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ConcurrentDictionary<string, Entry> _machines = new(StringComparer.OrdinalIgnoreCase);

    // Store null = the machine's files could not be read (Error says why):
    // its lines are refused rather than handled with the wrong (shared) mapping.
    private sealed record Entry(string Id, string Folder, GatewayConfigStore? Store, UnidentifiedTagTracker Unidentified, string? Error);

    public MachineRegistry(GatewayConfigStore shared, UnidentifiedTagTracker sharedUnidentified, JsonSerializerOptions jsonOptions)
    {
        Shared = shared;
        SharedUnidentified = sharedUnidentified;
        _jsonOptions = jsonOptions;

        if (Directory.Exists(MachinesFolder))
            foreach (var folder in Directory.GetDirectories(MachinesFolder))
            {
                var id = Path.GetFileName(folder);
                if (ValidId.IsMatch(id))
                    Load(id, folder);
            }
    }

    public GatewayConfigStore Shared { get; }
    public UnidentifiedTagTracker SharedUnidentified { get; }

    // Machines sending without a tab while unregisteredMachines = "hold".
    public HeldMachines Held { get; } = new();

    // Machines whose messages are held for tags their mapping doesn't know yet.
    public UnmappedHolds Unmapped { get; } = new();
    public string MachinesFolder => Path.Combine(Shared.ConfigRoot, FolderName);

    public IReadOnlyList<string> Ids => _machines.Values.Select(e => e.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool IsValidId(string? id) => id is not null && ValidId.IsMatch(id);

    // true = the machine has its own mapping. store = null (with error) when its files can't be read.
    public bool TryGetOwn(string machineId, out GatewayConfigStore? store, out string? error)
    {
        if (_machines.TryGetValue(machineId, out var entry))
        {
            store = entry.Store;
            error = entry.Error;
            return true;
        }
        store = null;
        error = null;
        return false;
    }

    // The machine's own store (null = it has none, or it is unreadable).
    public GatewayConfigStore? StoreFor(string machineId) =>
        _machines.TryGetValue(machineId, out var entry) ? entry.Store : null;

    public UnidentifiedTagTracker? UnidentifiedFor(string machineId) =>
        _machines.TryGetValue(machineId, out var entry) ? entry.Unidentified : null;

    // The machine's own Config folder (also when its files are unreadable), or null.
    public string? FolderFor(string machineId) =>
        _machines.TryGetValue(machineId, out var entry) ? entry.Folder : null;

    // (id, readable, why not) for the config page.
    public IReadOnlyList<(string Id, bool Readable, string? Error)> List() =>
        _machines.Values.OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(e => (e.Id, e.Store is not null, e.Error)).ToList();

    // After the page saved one of the machine's files. Null = fine, else why not.
    public string? Reload(string machineId)
    {
        if (!_machines.TryGetValue(machineId, out var entry))
            return $"{machineId} has no own mapping.";
        if (entry.Store is null)
            return Load(entry.Id, entry.Folder).Error;
        return ReloadOrHold(entry);
    }

    // A file broken by hand must only hold that machine's lines, not stop the
    // gateway (issue #4): the machine becomes unreadable, like at a cold start.
    // Null = reloaded, else why not.
    private string? ReloadOrHold(Entry entry)
    {
        try
        {
            entry.Store!.Reload();
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] {entry.Id}: own mapping can't be read, its lines are held until it is fixed: {ex.Message}");
            _machines[entry.Id] = new Entry(entry.Id, entry.Folder, null, entry.Unidentified, ex.Message);
            return ex.Message;
        }
    }

    // Machines of one kind: the same name before the first "-" (NEXIM-...), or, without a
    // "-", the same name without its trailing digits (NXT101 ~ NXT105). The config page
    // uses the same rule (familyOf in app.js).
    public static string Family(string machineId)
    {
        var dash = machineId.IndexOf('-');
        return (dash >= 0 ? machineId[..dash] : Regex.Replace(machineId, @"\d+$", "")).ToLowerInvariant();
    }

    // A readable tab of the same kind to copy for a new machine: one somebody confirmed
    // first, an automatic one only if there is no other. null = none (copy the template).
    public string? SimilarTab(string machineId)
    {
        var family = Family(machineId);
        if (family.Length == 0)
            return null;
        return _machines.Values
            .Where(e => e.Store is not null && !e.Id.Equals(machineId, StringComparison.OrdinalIgnoreCase) && Family(e.Id) == family)
            .OrderBy(e => IsAuto(e.Id) ? 1 : 0).ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Id).FirstOrDefault();
    }

    // A tab the gateway created by itself (no person has checked it yet): this file in its folder.
    public const string AutoMarker = "auto-created.json";
    public bool IsAuto(string machineId) => FolderFor(machineId) is { } folder && File.Exists(Path.Combine(folder, AutoMarker));

    // Where an automatic tab was copied from ("" = the template); null = not automatic.
    public string? AutoCopiedFrom(string machineId)
    {
        if (FolderFor(machineId) is not { } folder || !File.Exists(Path.Combine(folder, AutoMarker)))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, AutoMarker)));
            return doc.RootElement.TryGetProperty("copiedFrom", out var from) && from.ValueKind == JsonValueKind.String ? from.GetString() ?? "" : "";
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return "";
        }
    }

    // Create, marked automatic. Null = done, else why not.
    public string? CreateAuto(string machineId, string? copyFrom)
    {
        var problem = Create(machineId, copyFrom);
        if (problem is null && FolderFor(machineId) is { } folder)
            File.WriteAllText(Path.Combine(folder, AutoMarker),
                JsonSerializer.Serialize(new { copiedFrom = copyFrom ?? "", at = DateTimeOffset.Now }));
        return problem;
    }

    // Somebody checked the automatic tab: no longer marked.
    public bool ConfirmAuto(string machineId)
    {
        if (FolderFor(machineId) is not { } folder || !File.Exists(Path.Combine(folder, AutoMarker)))
            return false;
        File.Delete(Path.Combine(folder, AutoMarker));
        return true;
    }

    // Gives a machine its own mapping, starting from a copy of copyFrom's
    // (another machine's own, or the shared one when null). Null = done, else why not.
    public string? Create(string machineId, string? copyFrom)
    {
        if (!IsValidId(machineId))
            return "machine_id may only use letters, digits, '.', '_' and '-' (max 64), starting with a letter or digit.";
        if (_machines.ContainsKey(machineId))
            return $"{machineId} already has its own mapping.";

        string sourceFolder;
        if (string.IsNullOrWhiteSpace(copyFrom))
            sourceFolder = Shared.ConfigRoot;
        else if (_machines.TryGetValue(copyFrom, out var source))
            sourceFolder = source.Folder;
        else
            return $"{copyFrom} has no own mapping to copy.";

        var folder = Path.Combine(MachinesFolder, machineId);
        Directory.CreateDirectory(folder);
        foreach (var file in MappingFiles)
        {
            var from = Path.Combine(sourceFolder, file);
            var to = Path.Combine(folder, file);
            if (File.Exists(from))
                File.Copy(from, to, overwrite: true);
            else
                File.WriteAllText(to, file == "point-map.json" ? "[]" : "{}");
        }

        var entry = Load(machineId, folder);
        Console.WriteLine($"[config] {machineId}: own mapping created (copied from {(string.IsNullOrWhiteSpace(copyFrom) ? "the template" : copyFrom)})");

        // It was held (sending before it had a tab): no longer. The tags it sent
        // show in its Unidentified list (the mapped ones are filtered out there),
        // and the adapter's resend is now processed with this mapping.
        foreach (var (tag, value) in Held.Release(machineId))
            entry.Unidentified.Record(tag, value);
        return entry.Error;
    }

    // Back to the shared mapping. The folder is kept (renamed), so nothing is
    // lost; the machine's outbox keeps being delivered.
    public bool Remove(string machineId)
    {
        if (!_machines.TryRemove(machineId, out var entry))
            return false;

        var removed = Path.Combine(Shared.ConfigRoot, FolderName + "-removed");
        Directory.CreateDirectory(removed);
        Directory.Move(entry.Folder, Path.Combine(removed, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{entry.Id}-{DateTime.Now:yyyyMMdd-HHmmss}")));
        Console.WriteLine($"[config] {entry.Id}: own mapping removed (kept in Config/{FolderName}-removed); it uses the shared mapping now");
        return true;
    }

    // Gateway (re)start: every file read again.
    public void ReloadAll()
    {
        Shared.Reload();
        foreach (var entry in _machines.Values.ToList())
        {
            if (entry.Store is null)
                Load(entry.Id, entry.Folder); // was unreadable: try again
            else
                ReloadOrHold(entry);
        }
    }

    private Entry Load(string id, string folder)
    {
        var unidentified = _machines.TryGetValue(id, out var old) ? old.Unidentified : new UnidentifiedTagTracker();
        Entry entry;
        try
        {
            var store = new GatewayConfigStore(folder, _jsonOptions, unidentified, Shared.ConfigRoot, id);
            entry = new Entry(id, folder, store, unidentified, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] {id}: own mapping can't be read, its lines are refused until it is fixed: {ex.Message}");
            entry = new Entry(id, folder, null, unidentified, ex.Message);
        }
        _machines[id] = entry;
        return entry;
    }
}

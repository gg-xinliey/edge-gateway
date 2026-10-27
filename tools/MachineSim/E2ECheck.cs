using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// End-to-end check: do the MES records match what the machines sent?
//
//   dotnet run -- --check [--run run-data.jsonl] [--mes ..\MES_EMULATOR\received.jsonl]
//
// Source: run-data.jsonl (MachineSim's data lines, in the order made, with acked yes/no).
// MES:    received.jsonl (every event the fake MES stored, once per event_id).
// Only the machines in the run are looked at.
//
// The gateway sends one record per data group when a value of that group
// changes (the group's full values after the change). For each machine and
// group, walking the acknowledged lines in order:
//   * lost      a line changed the group, but MES has no record for it
//   * extra     MES has a record for a line that changed nothing (sent twice in effect)
//   * wrong     a record's values or event_time differ from the line it came from
//   * unknown   a record whose event_id matches no line of this run
// The record <-> line link is the event_id: the gateway makes it from the
// line's message_id and the group (same formula below).
static class E2ECheck
{
    // The test tabs' mapping (copied from the Template): incoming tag -> MES field, per group.
    private static readonly Dictionary<string, (string Tag, string Field)[]> Groups = new()
    {
        ["MACHINE_STATUS"] = new[] { ("MACHINE_STATUS", "machine_status") },
        ["PRODUCTION"] = new[] { ("CYCLE_ACTIVE", "cycle_active"), ("GOOD_QTY", "good_quantity"), ("NG_QTY", "ng_quantity"),
                                 ("work_order_no", "work_order_no"), ("cycle_time", "cycle_time"), ("station_id", "station_id") },
        ["PROCESS"] = new[] { ("TORQUE_1", "actual_value") },
        ["ALARM"] = new[] { ("ALARM_STATUS", "alarm_status"), ("ALARM_CODE", "alarm_code") },
        ["OPERATOR"] = new[] { ("operator_id", "operator_id"), ("operator_name", "operator_name"), ("login_time", "login_time") },
    };

    public static int Run(string[] args)
    {
        string Get(string name, string fallback)
        {
            var at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
        }
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        var runPath = Get("--run", Path.Combine(project, "run-data.jsonl"));
        var mesPath = Get("--mes", Path.Combine(project, "..", "MES_EMULATOR", "received.jsonl"));

        // ---- source lines ----
        var lines = new List<Line>();
        foreach (var text in File.ReadLines(runPath))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            using var doc = JsonDocument.Parse(text);
            var line = doc.RootElement.GetProperty("line");
            lines.Add(new Line(
                line.GetProperty("machine_id").GetString()!,
                line.GetProperty("message_id").GetString()!,
                line.GetProperty("timestamp").GetString()!,
                doc.RootElement.GetProperty("acked").GetBoolean(),
                line.GetProperty("data").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));
        }
        var machines = lines.Select(l => l.Machine).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ---- MES records of those machines ----
        var records = new Dictionary<string, JsonElement>(); // event_id -> envelope
        var mesTotal = 0;
        foreach (var text in File.ReadLines(mesPath))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var e = JsonDocument.Parse(text).RootElement;
            if (!machines.Contains(e.GetProperty("machine_id").GetString() ?? "")) continue;
            records[e.GetProperty("event_id").GetString()!] = e;
            mesTotal++;
        }

        Console.WriteLine($"source: {lines.Count} data lines from {machines.Count} machine(s), {lines.Count(l => !l.Acked)} never acknowledged ({runPath})");
        Console.WriteLine($"MES:    {mesTotal} records of these machines ({mesPath})");
        Console.WriteLine();

        var used = new HashSet<string>();
        int totalLost = 0, totalExtra = 0, totalWrong = 0, totalMatched = 0, carried = 0;
        Console.WriteLine($"{"machine",-16} {"group",-15} {"changes",8} {"MES",6} {"matched",8} {"lost",5} {"extra",6} {"wrong",6}");
        foreach (var machine in machines.OrderBy(m => m))
        {
            var mine = lines.Where(l => l.Machine.Equals(machine, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var (group, fields) in Groups)
            {
                int changes = 0, inMes = 0, matched = 0, lost = 0, extra = 0, wrong = 0;
                string? previous = null;
                Line? previousLine = null;
                foreach (var line in mine)
                {
                    var id = EventId(line.Machine, line.MessageId, group);
                    var hasRecord = records.TryGetValue(id, out var record);
                    if (hasRecord) { used.Add(id); inMes++; }
                    if (!line.Acked)
                    {
                        // Never acknowledged: the gateway may or may not have it. Not counted either way.
                        continue;
                    }

                    // Only MES columns count: a line that changes only an extra field (e.g.
                    // cycle_active) makes no record, the gateway skips it on purpose.
                    var columns = MesColumns.Value.GetValueOrDefault(group);
                    var values = string.Join("|", fields.Where(f => columns is null || columns.Contains(f.Field, StringComparer.OrdinalIgnoreCase))
                        .Select(f => Canon(line.Data.GetValueOrDefault(f.Tag))));
                    var changed = values != previous;
                    previous = values;
                    if (changed) changes++;

                    if (!hasRecord)
                    {
                        // The run's first line may equal the values the gateway kept from an
                        // earlier run of the same machine id: then no record is right.
                        if (changed && previousLine is null) { carried++; Report("SAME ", machine, group, line, "first line, no record: same values as the gateway had before this run (not judged)"); }
                        else if (changed) { lost++; Report("LOST ", machine, group, line, "changed values, no MES record"); }
                        previousLine = line;
                        continue;
                    }
                    previousLine = line;

                    if (!changed) { extra++; Report("EXTRA", machine, group, line, "MES record for unchanged values"); }
                    var problem = Compare(record, line, fields);
                    if (problem is null) matched++;
                    else { wrong++; Report("WRONG", machine, group, line, problem); }
                }
                totalLost += lost; totalExtra += extra; totalWrong += wrong; totalMatched += matched;
                Console.WriteLine($"{machine,-16} {group,-15} {changes,8} {inMes,6} {matched,8} {lost,5} {extra,6} {wrong,6}");
            }
        }

        var unknown = records.Keys.Where(id => !used.Contains(id)).ToList();
        foreach (var id in unknown.Take(10))
            Console.WriteLine($"  UNKNOWN  {records[id].GetProperty("machine_id")} {records[id].GetProperty("data_group")} {id}: no line of this run gives this event_id");

        Console.WriteLine();
        // A line never acknowledged was not taken by the gateway (held, or the run
        // ended first): the machine still has it, but this run did not get it to MES.
        var unanswered = lines.Count(l => !l.Acked);
        var pass = totalLost == 0 && totalExtra == 0 && totalWrong == 0 && unknown.Count == 0 && unanswered == 0 && totalMatched > 0;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")}: {totalMatched} records match their source line; lost {totalLost}, extra {totalExtra}, wrong {totalWrong}, unknown {unknown.Count}" +
                          (unanswered > 0 ? $"; {unanswered} line(s) never acknowledged by the gateway" : "") +
                          (carried > 0 ? $"; {carried} first line(s) equal to values from an earlier run (not judged)" : "") +
                          (totalMatched == 0 ? "; nothing reached MES" : ""));
        return pass ? 0 : 1;
    }

    private static int _reported;
    private static void Report(string kind, string machine, string group, Line line, string what)
    {
        if (++_reported <= 30)
            Console.WriteLine($"  {kind}  {machine} {group} message {line.MessageId} ({line.Timestamp}): {what}");
    }

    // null = the record carries the line's values and time.
    private static string? Compare(JsonElement record, Line line, (string Tag, string Field)[] fields)
    {
        var payload = record.GetProperty("payload");
        var columns = MesColumns.Value.GetValueOrDefault(record.GetProperty("data_group").GetString() ?? "");
        foreach (var (tag, field) in fields)
        {
            if (columns is not null && !columns.Contains(field, StringComparer.OrdinalIgnoreCase))
                continue; // no MES column (e.g. cycle_active): the gateway does not send it

            var sent = Canon(line.Data.GetValueOrDefault(tag));
            var got = payload.TryGetProperty(field, out var v) ? Canon(v) : "(missing)";
            if (sent != got)
                return $"{field}: machine sent {sent}, MES has {got}";
        }

        // The machine's timestamp has no zone: the gateway reads it as its
        // timestampsWithoutZone setting says (UTC or Local). MES gets UTC.
        var source = DateTimeOffset.Parse(line.Timestamp, CultureInfo.InvariantCulture,
            ZoneLessIsLocal.Value ? DateTimeStyles.AssumeLocal : DateTimeStyles.AssumeUniversal);
        var eventTime = DateTimeOffset.Parse(record.GetProperty("event_time").GetString()!, CultureInfo.InvariantCulture);
        if (Math.Abs((source - eventTime).TotalMilliseconds) > 1)
            return $"event_time {record.GetProperty("event_time")} is not the machine's timestamp {line.Timestamp}";
        return null;
    }

    private static readonly Lazy<bool> ZoneLessIsLocal = new(() =>
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Config", "machine-config.json")); // bin\Debug\net8.0 of tools\MachineSim -> the gateway project
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty("timestampsWithoutZone", out var z) && string.Equals(z.GetString(), "Local", StringComparison.OrdinalIgnoreCase);
    });

    private static readonly Lazy<Dictionary<string, string[]>> MesColumns = new(() =>
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Config", "mes-schema.json"));
        var schema = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path))!;
        return new Dictionary<string, string[]>(schema, StringComparer.OrdinalIgnoreCase);
    });

    // Numbers compared as numbers (44345 = 44345.0), the rest as JSON text.
    // Not sent at all (Undefined) = null, as the gateway fills it for MES.
    private static string Canon(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => "null",
        JsonValueKind.Number => value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        JsonValueKind.String => value.GetString()!,
        _ => value.GetRawText(),
    };

    // Same as the gateway's EnvelopeBuilder.EventId (MachineSim sends no data_source: "").
    private static string EventId(string machine, string messageId, string dataGroup)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(machine + "\n" + "" + "\n" + messageId + "\n" + dataGroup));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    private sealed record Line(string Machine, string MessageId, string Timestamp, bool Acked, Dictionary<string, JsonElement> Data);
}

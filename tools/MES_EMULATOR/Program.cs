using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Fake MES for local tests. Not the real MES: its rules are our best guess
// from the MES DB format, to be confirmed against the real API.
//
//   http://localhost:5000/   envelopes from the gateway (POST, any path)
//     * checks each envelope like MES would (see Validate): bad -> 400 + {"error": ...}
//     * keeps each event_id once: a repeat is answered 200 but not stored again
//     * every stored event goes to received.jsonl (next to the project) for the E2E check
//
//   http://localhost:5001/   control (for tests), GET:
//     /mode?set=up            normal
//     /mode?set=down          503 to everything (MES up but failing)
//     /mode?set=unreachable   port 5000 closed (connection refused)
//     /mode?set=slow          answers after 20 s (the gateway gives up after 15 s)
//     /mode?set=reject        400 to everything
//     /mode?set=up&rejectGroup=ALARM   400 for that data group only
//     /stats                  per machine and group: stored, repeats, rejected; plus mode
//     /reset                  forget everything (and empty received.jsonl)
//
//   dotnet run            prints one short line per envelope
//   dotnet run -- -v      prints whole envelopes
//
// In Docker (Dockerfile next to this file, started by the gateway's docker-compose.yml):
//   --all-ips             listen on every address, not only localhost
//   --data <folder>       where received.jsonl goes (default: this project folder)
//   --schema <file>       the gateway's mes-schema.json (missing: the copy next to the exe)

var verbose = args.Contains("-v");
string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var host = args.Contains("--all-ips") ? "+" : "localhost";
var projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var receivedFile = Path.Combine(Arg("--data") ?? projectDir, "received.jsonl");
var schema = LoadSchema(new[] { Arg("--schema"), Path.Combine(AppContext.BaseDirectory, "mes-schema.json"),
        Path.Combine(projectDir, "..", "..", "Config", "mes-schema.json") } // tools\MES_EMULATOR -> the gateway project
    .First(path => path is not null && File.Exists(path))!);

var mode = "up";
string? rejectGroup = null;
var stored = new ConcurrentDictionary<string, string>();                    // event_id -> machine|group
var counts = new ConcurrentDictionary<(string Machine, string Group), Count>();
var fileLock = new object();
long requests = 0;

// Like a real database: what was stored before a restart of this program is still there.
if (File.Exists(receivedFile))
{
    foreach (var line in File.ReadLines(receivedFile))
    {
        if (string.IsNullOrWhiteSpace(line) || JsonNode.Parse(line) is not JsonObject old) continue;
        var (m, g) = (Text(old, "machine_id") ?? "?", Text(old, "data_group") ?? "?");
        if (stored.TryAdd(Text(old, "event_id") ?? "", $"{m}|{g}"))
            counts.GetOrAdd((m, g), _ => new Count()).Stored++;
    }
    if (!stored.IsEmpty) Console.WriteLine($"{stored.Count} events from before (received.jsonl). /reset forgets them.");
}

HttpListener? data = null;
void OpenData()
{
    if (data is { IsListening: true }) return;
    data = new HttpListener();
    data.Prefixes.Add($"http://{host}:5000/");
    data.Start();
    _ = Task.Run(() => ServeData(data));
}
void CloseData()
{
    var d = data;
    data = null;
    try { d?.Stop(); d?.Close(); } catch { }
}

OpenData();
var control = new HttpListener();
control.Prefixes.Add($"http://{host}:5001/");
control.Start();
Console.WriteLine($"Fake MES on http://localhost:5000/  (control: http://localhost:5001/mode?set=up|down|unreachable|slow|reject, /stats, /reset)");
Console.WriteLine($"Known data groups: {string.Join(", ", schema.Keys)}");
Console.WriteLine($"Stored events go to {receivedFile}");

while (true)
{
    var ctx = await control.GetContextAsync();
    try { HandleControl(ctx); }
    catch (Exception ex) { Reply(ctx, 500, new { error = ex.Message }); }
}

void HandleControl(HttpListenerContext ctx)
{
    var path = ctx.Request.Url!.AbsolutePath.TrimEnd('/').ToLowerInvariant();
    var q = ctx.Request.QueryString;
    switch (path)
    {
        case "/mode":
            var set = (q["set"] ?? "").ToLowerInvariant();
            if (set is not ("up" or "down" or "unreachable" or "slow" or "reject"))
            {
                Reply(ctx, 400, new { error = "set=up|down|unreachable|slow|reject" });
                return;
            }
            mode = set;
            rejectGroup = string.IsNullOrWhiteSpace(q["rejectGroup"]) ? null : q["rejectGroup"]!.Trim().ToUpperInvariant();
            if (mode == "unreachable") CloseData(); else OpenData();
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  ==== mode {mode}{(rejectGroup is null ? "" : $", rejecting {rejectGroup}")} ====");
            Reply(ctx, 200, new { mode, rejectGroup });
            return;

        case "/stats":
            Reply(ctx, 200, new
            {
                mode, rejectGroup, requests = Interlocked.Read(ref requests), events = stored.Count,
                machines = counts.GroupBy(c => c.Key.Machine).OrderBy(g => g.Key).ToDictionary(g => g.Key,
                    g => g.OrderBy(c => c.Key.Group).ToDictionary(c => c.Key.Group, c => new { stored = c.Value.Stored, repeats = c.Value.Repeats, rejected = c.Value.Rejected }))
            });
            return;

        case "/reset":
            stored.Clear();
            counts.Clear();
            Interlocked.Exchange(ref requests, 0);
            lock (fileLock) File.WriteAllText(receivedFile, "");
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  ==== reset ====");
            Reply(ctx, 200, new { ok = true });
            return;

        default:
            Reply(ctx, 404, new { error = "use /mode, /stats or /reset" });
            return;
    }
}

async Task ServeData(HttpListener listener)
{
    while (listener.IsListening)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); }
        catch { return; } // closed (mode unreachable)
        _ = Task.Run(() => HandleEnvelope(ctx));
    }
}

async Task HandleEnvelope(HttpListenerContext ctx)
{
    Interlocked.Increment(ref requests);
    string body;
    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
        body = await reader.ReadToEndAsync();

    try
    {
        switch (mode)
        {
            case "down":
                Reply(ctx, 503, new { error = "MES is down (test mode)" });
                return;
            case "slow":
                await Task.Delay(TimeSpan.FromSeconds(20));
                break;
            case "reject":
                Reply(ctx, 400, new { error = "rejected (test mode reject)" });
                return;
        }

        JsonObject? envelope;
        try { envelope = JsonNode.Parse(body) as JsonObject; }
        catch (JsonException ex) { Reject(ctx, "?", "?", $"not JSON: {ex.Message}"); return; }
        if (envelope is null) { Reject(ctx, "?", "?", "not a JSON object"); return; }

        var machine = Text(envelope, "machine_id") ?? "?";
        var group = Text(envelope, "data_group") ?? "?";
        if (rejectGroup is not null && group.Equals(rejectGroup, StringComparison.OrdinalIgnoreCase))
        {
            Reject(ctx, machine, group, $"data group {group} not accepted (test mode rejectGroup)");
            return;
        }

        var problem = Validate(envelope);
        if (problem is not null)
        {
            Reject(ctx, machine, group, problem);
            return;
        }

        var eventId = Text(envelope, "event_id")!;
        var c = counts.GetOrAdd((machine, group), _ => new Count());
        if (!stored.TryAdd(eventId, $"{machine}|{group}"))
        {
            Interlocked.Increment(ref c.Repeats);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  repeat  {machine,-14} {group,-15} {eventId} (already stored, 200)");
            Reply(ctx, 200, new { ok = true, duplicate = true });
            return;
        }

        Interlocked.Increment(ref c.Stored);
        lock (fileLock) File.AppendAllText(receivedFile, envelope.ToJsonString() + "\n");
        Console.WriteLine(verbose
            ? $"{DateTime.Now:HH:mm:ss.fff}  stored  {body}"
            : $"{DateTime.Now:HH:mm:ss.fff}  stored  {machine,-14} {group,-15} {Text(envelope, "event_time")}  {Short(envelope["payload"])}");
        Reply(ctx, 200, new { ok = true });
    }
    catch (Exception ex)
    {
        try { Reply(ctx, 500, new { error = ex.Message }); } catch { }
    }
}

// What MES would refuse. Known: envelope fields filled, a data group MES has
// a table for, every column of that table present (null allowed), and
// machine_status at most 30 characters (varchar(30)). Other lengths: the
// real MES API decides (to confirm from 26 Oct).
string? Validate(JsonObject e)
{
    foreach (var field in new[] { "event_id", "gateway_id", "machine_id", "data_group", "event_time" })
        if (string.IsNullOrWhiteSpace(Text(e, field)))
            return $"{field} is missing or empty";

    if (!Guid.TryParse(Text(e, "event_id"), out _))
        return "event_id is not a GUID";
    if (!DateTimeOffset.TryParse(Text(e, "event_time"), out _))
        return $"event_time '{Text(e, "event_time")}' is not a date-time";

    var group = Text(e, "data_group")!;
    if (!schema.TryGetValue(group, out var columns))
        return $"unknown data_group '{group}' (MES tables: {string.Join(", ", schema.Keys)})";

    if (e["payload"] is not JsonObject payload)
        return "payload is missing or not an object";
    var missing = columns.Where(col => !payload.ContainsKey(col)).ToList();
    if (missing.Count > 0)
        return $"payload misses MES column(s) of {group}: {string.Join(", ", missing)}";

    foreach (var (column, max) in MaxLength)
        if (payload[column] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > max)
            return $"{column} is {s.Length} characters, MES column is varchar({max})";

    return null;
}

void Reject(HttpListenerContext ctx, string machine, string group, string error)
{
    Interlocked.Increment(ref counts.GetOrAdd((machine, group), _ => new Count()).Rejected);
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  REJECT  {machine,-14} {group,-15} {error}");
    Reply(ctx, 400, new { error });
}

static void Reply(HttpListenerContext ctx, int status, object body)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength64 = bytes.Length;
    ctx.Response.OutputStream.Write(bytes);
    ctx.Response.Close();
}

static string? Text(JsonObject o, string name) =>
    o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

static string Short(JsonNode? payload)
{
    if (payload is not JsonObject p) return "";
    var text = string.Join(" ", p.Where(kv => kv.Value is not null).Select(kv => $"{kv.Key}={kv.Value!.ToJsonString()}"));
    return text.Length > 140 ? text[..140] + "…" : text;
}

// The MES tables: the gateway's Config/mes-schema.json (same file the gateway uses).
static Dictionary<string, string[]> LoadSchema(string path)
{
    Console.WriteLine($"MES tables from {Path.GetFullPath(path)}");
    var schema = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path))!;
    return new Dictionary<string, string[]>(schema, StringComparer.OrdinalIgnoreCase);
}

// Column lengths known from the MES DB format.
partial class Program
{
    static readonly Dictionary<string, int> MaxLength = new() { ["machine_status"] = 30 };
}

sealed class Count
{
    public long Stored, Repeats, Rejected;
}

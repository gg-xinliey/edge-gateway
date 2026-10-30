// Regression checks for the GitHub issues, against the gateway's real classes.
// Each check is named after its issue (#1 ... #16) and failed before that fix.
//   dotnet run --project tests/EdgeGateway.Checks             all checks (about 1 min)
//   dotnet run --project tests/EdgeGateway.Checks -- 14       one issue's checks
//   dotnet run --project tests/EdgeGateway.Checks -- cfg <Config folder>
//                                                             would the gateway load this config?
// Works while the gateway runs (own build configuration, loopback ports 19400-19899).
using EdgeGateway.Models;
using EdgeGateway.Services;

var which = args.Length > 0 ? args[0] : "all";
var failed = 0;
void Report(string name, bool ok, string detail) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}: {detail}"); if (!ok) failed++; }

if (which is "all" or "1")
{
    // #1: the MES sender reads a new day file while the writer is still creating its table.
    var errors = 0; var runs = 300;
    for (var i = 0; i < runs; i++)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gw-issue1", Guid.NewGuid().ToString("N"));
        var outbox = new Outbox(dir);
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            var e = 0;
            while (!stop.IsCancellationRequested)
                try { outbox.ReadUnsent(100); } catch { e++; }
            return e;
        });
        var saved = new TaskCompletionSource();
        outbox.Write(new Envelope { event_id = Guid.NewGuid().ToString(), machine_id = "M1", data_group = "PROCESS" }, 0, () => saved.TrySetResult());
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(5);
        stop.Cancel();
        errors += await reader;
        await outbox.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dir, true); } catch { }
    }
    Report("#1 new day file while reading", errors == 0, $"{errors} read errors in {runs} new files");
}

// Replies a sender got so far, e.g. ["ack:B", "nack:Y"].
static List<string> Drain(System.Threading.Channels.ChannelReader<string> r)
{
    var got = new List<string>();
    while (r.TryRead(out var line))
    {
        using var doc = System.Text.Json.JsonDocument.Parse(line);
        foreach (var p in doc.RootElement.EnumerateObject()) { if (p.Name is "ack" or "nack") got.Add($"{p.Name}:{p.Value}"); }
    }
    return got;
}

if (which is "all" or "2")
{
    // #2: message B queued but never saved (writer blocked), gateway restarted (new run:
    // new ReplyHub + AckTracker, same process-wide filter), adapter resends B.
    var filter = new MessageIdFilter();
    var run1 = new AckTracker(new ReplyHub(), filter);
    var first = run1.Begin("B", "s1");                 // processing starts, never finishes
    var hub2 = new ReplyHub(); var s2 = hub2.Open("s2");
    var run2 = new AckTracker(hub2, filter);
    var resend = run2.Begin("B", "s2");
    var path = Path.Combine(Path.GetTempPath(), "gw-issue2-ids.txt");
    filter.Save(path);
    var savedIds = File.ReadAllText(path);
    Report("#2 resend after stop is processed, not acked", first is not null && resend is not null && Drain(s2).Count == 0, $"resend gets a ticket: {resend is not null}");
    Report("#2 unsaved id not kept on Ctrl+C", !savedIds.Contains("\tB"), "message-ids file does not hold B");
}

if (which is "all" or "3")
{
    var filter = new MessageIdFilter();
    var hub = new ReplyHub();
    var s = Enumerable.Range(1, 6).ToDictionary(i => i, i => hub.Open($"s{i}"));
    var acks = new AckTracker(hub, filter);

    // held: the copy waiting on it gets nothing; a later resend is processed again
    var h = acks.Begin("X", "s1"); var h2 = acks.Begin("X", "s2"); h!.Drop();
    var h3 = acks.Begin("X", "s3");
    Report("#3 held first copy", h2 is null && Drain(s[1]).Count == 0 && Drain(s[2]).Count == 0 && h3 is not null, "waiting copy no reply, resend processed again");
    h3!.Drop();

    // rejected: the waiting copy gets the same nack, not an ack
    var r = acks.Begin("Y", "s4"); var r2 = acks.Begin("Y", "s5"); r!.Reject("no machine_id");
    var got5 = Drain(s[5]);
    Report("#3 rejected first copy", r2 is null && got5.SequenceEqual(new[] { "nack:Y" }), $"waiting copy got [{string.Join(",", got5)}]");

    // saved: waiting copy and a later copy are acked, nothing processed twice
    var a = acks.Begin("Z", "s1"); var a2 = acks.Begin("Z", "s2"); a!.Release();
    var a3 = acks.Begin("Z", "s3");
    Report("#3 saved first copy", a2 is null && a3 is null && Drain(s[1]).Contains("ack:Z") && Drain(s[2]).Contains("ack:Z") && Drain(s[3]).Contains("ack:Z"), "all three copies acked, processed once");

    // race like the issue: two connections, same 20,000 ids, machine held -> no ack may ever come
    var hubR = new ReplyHub(); var c1 = hubR.Open("c1"); var c2 = hubR.Open("c2");
    var race = new AckTracker(hubR, new MessageIdFilter());
    void Send(string conn) { for (var i = 0; i < 20_000; i++) race.Begin($"A{i}", conn)?.Drop(); }
    await Task.WhenAll(Task.Run(() => Send("c1")), Task.Run(() => Send("c2")));
    var stray = Drain(c1).Count + Drain(c2).Count;
    Report("#3 race, held machine, 2 x 20,000 lines", stray == 0, $"{stray} stray replies");
}

if (which is "all" or "8")
{
    // #8: a storm of connections reset right after connecting must not stop the listener.
    var port = 19400 + Random.Shared.Next(500);
    var lines = 0;
    var server = new TcpIngestServer(System.Net.IPAddress.Loopback, port, (_, _) => Interlocked.Increment(ref lines));
    using var stop = new CancellationTokenSource();
    var run = server.RunAsync(stop.Token);
    await Task.Delay(300);
    await Parallel.ForEachAsync(Enumerable.Range(0, 2000), new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (_, _) =>
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            await c.ConnectAsync(System.Net.IPAddress.Loopback, port);
            c.LingerState = new System.Net.Sockets.LingerOption(true, 0); // close = RST
        }
        catch { }
    });
    using (var ok = new System.Net.Sockets.TcpClient())
    {
        await ok.ConnectAsync(System.Net.IPAddress.Loopback, port);
        var w = new StreamWriter(ok.GetStream()) { AutoFlush = true };
        await w.WriteLineAsync("{\"message_id\":\"t1\",\"machine_id\":\"M\"}");
        await Task.Delay(500);
    }
    Report("#8 listener survives 2,000 reset connections", !run.IsCompleted && lines == 1, $"still running: {!run.IsCompleted}, normal line received: {lines == 1}");
    stop.Cancel();
    try { await run; } catch { }
}

if (which is "all" or "13")
{
    // #13: a sender that never sends a line break must not fill the memory: closed.
    var port = 19400 + Random.Shared.Next(500);
    var got = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var server = new TcpIngestServer(System.Net.IPAddress.Loopback, port, (line, _) => got.Enqueue(line));
    using var stop = new CancellationTokenSource();
    var run = server.RunAsync(stop.Token);
    await Task.Delay(300);
    var closed = false;
    using (var bad = new System.Net.Sockets.TcpClient())
    {
        await bad.ConnectAsync(System.Net.IPAddress.Loopback, port);
        var s = bad.GetStream();
        try
        {
            await s.WriteAsync("{\"before\":1}\n"u8.ToArray());
            var junk = new byte[64 * 1024]; Array.Fill(junk, (byte)'x');
            for (var i = 0; i < 48; i++) await s.WriteAsync(junk); // 3 MB, no line break
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            closed = await s.ReadAsync(new byte[1], wait.Token) == 0;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { closed = ex is IOException; }
    }
    using (var ok = new System.Net.Sockets.TcpClient())
    {
        await ok.ConnectAsync(System.Net.IPAddress.Loopback, port);
        await ok.GetStream().WriteAsync("{\"after\":1}\n"u8.ToArray());
        await Task.Delay(500);
    }
    var lines = got.ToArray();
    Report("#13 3 MB without a line break", closed && lines.SequenceEqual(new[] { "{\"before\":1}", "{\"after\":1}" }),
        $"connection closed: {closed}, lines: {string.Join(" ", lines.Select(l => l.Length > 20 ? $"({l.Length} chars)" : l))}");

    // Line endings like ReadLine: "\r\n", "\n", and a last line without one.
    got.Clear();
    using (var c = new System.Net.Sockets.TcpClient())
    {
        await c.ConnectAsync(System.Net.IPAddress.Loopback, port);
        await c.GetStream().WriteAsync("a\r\nb\n\nc"u8.ToArray());
    }
    await Task.Delay(500);
    Report("#13 line endings", got.SequenceEqual(new[] { "a", "b", "c" }), string.Join(",", got));
    stop.Cancel();
    try { await run; } catch { }
}

if (which is "all" or "14")
{
    // #14: the old connection's queued lines and the new connection's lines (same PC)
    // must be handled in the order sent, also while the old ones are still queued.
    var port = 19400 + Random.Shared.Next(500);
    var order = new System.Collections.Concurrent.ConcurrentQueue<int>();
    var server = new TcpIngestServer(System.Net.IPAddress.Loopback, port, (line, _) => { Thread.Sleep(1); order.Enqueue(int.Parse(line)); });
    using var stop = new CancellationTokenSource();
    var run = server.RunAsync(stop.Token);
    await Task.Delay(300);
    async Task SendRange(int from, int to)
    {
        using var c = new System.Net.Sockets.TcpClient();
        await c.ConnectAsync(System.Net.IPAddress.Loopback, port);
        await c.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"{i}\n"))));
    } // closed here: the connection drops with lines still queued
    await SendRange(1, 300);   // ~300 ms of work queued
    await Task.Delay(50);
    await SendRange(301, 400); // the "reconnect"
    for (var i = 0; i < 100 && order.Count < 400; i++) await Task.Delay(100);
    var got = order.ToArray();
    var firstWrong = Enumerable.Range(1, got.Length - 1).FirstOrDefault(i => got[i] < got[i - 1]);
    Report("#14 order kept over a reconnect", got.Length == 400 && firstWrong == 0,
        $"{got.Length} lines, {(firstWrong == 0 ? "in order" : $"{got[firstWrong]} after {got[firstWrong - 1]}")}");
    stop.Cancel();
    try { await run; } catch { }
}

// A throw-away Config folder: Template + machine tabs with their own files.
static (string Root, System.Text.Json.JsonSerializerOptions Json) TempConfig(params string[] machines)
{
    var root = Path.Combine(Path.GetTempPath(), "gw-cfg", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "mes-schema.json"), """{"TRACEABILITY":["product_serial_number","station_id"],"MACHINE_STATUS":["machine_status","previous_status","machine_mode","status_start_time","status_end_time"]}""");
    var pm = """[{"tag":"product_sn","dataGroup":"TRACEABILITY","fieldName":"product_serial_number"},{"tag":"module_no","dataGroup":"TRACEABILITY","fieldName":"station_id"},{"tag":"MACHINE_STATUS","dataGroup":"MACHINE_STATUS","fieldName":"machine_status"},{"tag":"MACHINE_MODE","dataGroup":"MACHINE_STATUS","fieldName":"machine_mode"}]""";
    var tr = """{"MACHINE_STATUS":{"mode":"Span","triggerField":"machine_status","startField":"status_start_time","endField":"status_end_time","previousField":"previous_status"}}""";
    foreach (var folder in new[] { root }.Concat(machines.Select(m => Path.Combine(root, "machines", m))))
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "point-map.json"), pm);
        File.WriteAllText(Path.Combine(folder, "timestamp-rules.json"), tr);
    }
    var json = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    return (root, json);
}

if (which is "all" or "4")
{
    var (root, json) = TempConfig("NXT101", "NXT102");
    var registry = new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json);
    File.WriteAllText(Path.Combine(root, "machines", "NXT101", "point-map.json"), "[ { broken");
    string? error = null;
    try { registry.ReloadAll(); } catch (Exception ex) { error = ex.Message; }
    registry.TryGetOwn("NXT101", out var s101, out var e101);
    var s102 = registry.StoreFor("NXT102");
    Report("#4 ReloadAll with one broken machine file", error is null && s101 is null && s102 is not null && (e101 ?? "").Contains("NXT101"),
        $"threw: {error ?? "no"}; NXT101 held: {s101 is null}; NXT102 fine: {s102 is not null}; error names the file: {(e101 ?? "").Contains("point-map.json")}");
}

if (which == "cfg")
{
    // Loads a real Config folder the way the gateway does: every file that would be refused.
    var json = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    try
    {
        var registry = new MachineRegistry(new GatewayConfigStore(Path.GetFullPath(args[1]), json), new UnidentifiedTagTracker(), json); // relative to where it is run
        var list = registry.List();
        Report("config folder loads", list.All(m => m.Readable), $"shared ok, {list.Count(m => m.Readable)} of {list.Count} machine tabs ok" +
            string.Concat(list.Where(m => !m.Readable).Select(m => $"\n      {m.Id}: {m.Error}")));
    }
    catch (Exception ex) { Report("config folder loads", false, $"shared: {ex.Message}"); }
}

if (which is "all" or "docker")
{
    // Config page access (#12), also in a container (--admin-all-ips): Docker's port mapping
    // brings requests in from the bridge (172.17.0.1), maybe on another port (8080 -> 5100).
    var bridge = System.Net.IPAddress.Parse("172.17.0.1");
    var loop = System.Net.IPAddress.Loopback;
    string? R(System.Net.IPAddress ip, string method, string? type, string? site, string? origin, string url, bool all) =>
        EdgeGateway.Admin.AdminWebServer.Refused(ip, method, type, site, origin, new Uri(url), all);
    var cases = new (string Name, bool Allowed, string? Got)[]
    {
        ("PC: GET from loopback", true, R(loop, "GET", null, null, null, "http://localhost:5100/api/status", false)),
        ("PC: GET from another PC", false, R(bridge, "GET", null, null, null, "http://localhost:5100/api/status", false)),
        ("PC: page's own save", true, R(loop, "PUT", "application/json", "same-origin", "http://localhost:5100", "http://localhost:5100/api/config/point-map", false)),
        ("PC: other site's POST", false, R(loop, "POST", "text/plain", "cross-site", "https://evil.example", "http://localhost:5100/api/gateway/restart", false)),
        ("Docker: GET via mapping", true, R(bridge, "GET", null, null, null, "http://localhost:5100/api/status", true)),
        ("Docker: page's own save on mapped port 8080", true, R(bridge, "PUT", "application/json", "same-origin", "http://localhost:8080", "http://localhost:8080/api/config/point-map", true)),
        ("Docker: other name (DNS rebinding / other PC)", false, R(bridge, "GET", null, null, null, "http://evil.example:5100/api/status", true)),
        ("Docker: other site's POST", false, R(bridge, "POST", "application/json", "cross-site", "https://evil.example", "http://localhost:5100/api/failed/discard", true)),
        ("Docker: POST not JSON", false, R(bridge, "POST", "text/plain", null, null, "http://localhost:5100/api/gateway/restart", true)),
    };
    var wrong = cases.Where(c => (c.Got is null) != c.Allowed).Select(c => $"{c.Name}: {(c.Got is null ? "allowed" : "refused")}").ToList();
    Report("docker: config page access rules", wrong.Count == 0, wrong.Count == 0 ? $"{cases.Length} cases as expected" : string.Join("; ", wrong));
}

if (which is "all" or "20")
{
    // #20: one message with an unknown tag adds 1 to its count, not 2. The gateway
    // calls Unmapped() then Sort() for every MES message.
    var tracker = new UnidentifiedTagTracker();
    var normalizer = new Normalizer(new List<PointMapEntry> { new() { Tag = "known", DataGroup = "TRACEABILITY", FieldName = "station_id" } }, tracker);
    var raw = new Dictionary<string, object?> { ["known"] = "1", ["brand_new"] = "2" };
    normalizer.Unmapped(raw);
    normalizer.Sort(raw);
    var counts = tracker.Snapshot().Select(t => $"{t.Tag}={t.Count}").ToList();
    Report("#20 unknown tag counted once per message", counts.SequenceEqual(new[] { "brand_new=1" }), string.Join(", ", counts));
}

if (which is "all" or "16")
{
    // #16: a file edited by hand that is valid JSON but breaks a rule (Span with no endField)
    // holds that machine (red tab) instead of loading and failing every message.
    var (root, json) = TempConfig("NXT101", "NXT102");
    File.WriteAllText(Path.Combine(root, "machines", "NXT101", "timestamp-rules.json"),
        """{"MACHINE_STATUS":{"mode":"Span","triggerField":"machine_status","startField":"status_start_time"}}""");
    var registry = new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json);
    registry.TryGetOwn("NXT101", out var s101, out var e101);
    var s102 = registry.StoreFor("NXT102");
    Report("#16 hand-edited rule checked on load", s101 is null && s102 is not null && (e101 ?? "").Contains("timestamp-rules.json") && (e101 ?? "").Contains("Span needs"),
        $"NXT101 held: {s101 is null}; NXT102 fine: {s102 is not null}; error: {e101 ?? "none"}");
}

// Sends one raw message through mapping + IntervalSender and waits until its rows are saved.
static async Task SendRaw(GatewayConfigStore store, IntervalSender sender, Dictionary<string, object?> raw, DateTimeOffset at, string source = "S")
{
    var buckets = store.Current.Normalizer.Sort(raw);
    IntervalSender.AddCycleTime(store.Current, buckets); // as GatewayHost does
    foreach (var (group, fields) in buckets)
    {
        if (group == Normalizer.IgnoredGroup) continue;
        var saved = new TaskCompletionSource();
        if (sender.Accept("NXT101", group, fields, at, source, Guid.NewGuid().ToString(), () => saved.TrySetResult()))
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}

if (which is "all" or "6")
{
    var (root, json) = TempConfig();
    var store = new GatewayConfigStore(root, json);
    var outboxDir = Path.Combine(root, "outbox");
    var outbox = new Outbox(outboxDir);
    var envelopes = new EnvelopeBuilder("TEST", "SRC", "TCP");
    var sender = new IntervalSender(outbox, envelopes, () => store.Current);
    var t = DateTimeOffset.UtcNow;
    await SendRaw(store, sender, new() { ["product_sn"] = "P1", ["module_no"] = "1" }, t);
    // the page sets module_no to "ignore (never sent)"
    var pm = File.ReadAllText(Path.Combine(root, "point-map.json")).Replace("\"tag\":\"module_no\",\"dataGroup\":\"TRACEABILITY\"", "\"tag\":\"module_no\",\"dataGroup\":\"IGNORED\"");
    File.WriteAllText(Path.Combine(root, "point-map.json"), pm);
    store.Reload();
    await SendRaw(store, sender, new() { ["product_sn"] = "P2", ["module_no"] = "7" }, t.AddSeconds(1));
    // restart: a new sender carries on from the state saved in the outbox
    var sender2 = new IntervalSender(outbox, envelopes, () => store.Current);
    await SendRaw(store, sender2, new() { ["product_sn"] = "P3" }, t.AddSeconds(2));
    await Task.Delay(1500); // rows paced by sendEveryMs (1 s) become due
    var rows = outbox.ReadUnsent(100).Where(r => r.Envelope.data_group == "TRACEABILITY")
        .Select(r => $"{r.Envelope.payload["product_serial_number"]}:{r.Envelope.payload["station_id"] ?? "null"}").ToList();
    Report("#6 ignored tag's last value not sent any more", rows.SequenceEqual(new[] { "P1:1", "P2:null", "P3:null" }), string.Join("  ", rows));
    await outbox.DisposeAsync();
}

if (which is "all" or "7")
{
    var (root, json) = TempConfig();
    var store = new GatewayConfigStore(root, json);
    var outbox = new Outbox(Path.Combine(root, "outbox"));
    var sender = new IntervalSender(outbox, new EnvelopeBuilder("TEST", "SRC", "TCP"), () => store.Current);
    var day = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
    await SendRaw(store, sender, new() { ["MACHINE_STATUS"] = "RUN", ["MACHINE_MODE"] = "A" }, day.AddHours(8));
    await SendRaw(store, sender, new() { ["MACHINE_STATUS"] = "RUN", ["MACHINE_MODE"] = "B" }, day.AddHours(8).AddMinutes(5));
    store.Reload(); // a save on the config page
    await SendRaw(store, sender, new() { ["MACHINE_STATUS"] = "RUN", ["MACHINE_MODE"] = "C" }, day.AddHours(8).AddMinutes(10));
    store.Reload();
    await SendRaw(store, sender, new() { ["MACHINE_STATUS"] = "IDLE", ["MACHINE_MODE"] = "C" }, day.AddHours(8).AddMinutes(15));
    await Task.Delay(3500); // paced rows (1 s each) become due
    var rows = outbox.ReadUnsent(100).Where(r => r.Envelope.data_group == "MACHINE_STATUS").Select(r => r.Envelope.payload).ToList();
    string T(object? v) => v is null ? "-" : DateTimeOffset.Parse(v.ToString()!).ToString("HH:mm");
    var got = rows.Select(p => $"{p["machine_status"]} start {T(p["status_start_time"])} end {T(p["status_end_time"])} prev {p["previous_status"] ?? "-"}").ToList();
    var want = new[] { "RUN start 08:00 end - prev -", "RUN start 08:00 end - prev -", "RUN start 08:00 end - prev -", "IDLE start 08:15 end 08:15 prev RUN" };
    Report("#7 span kept over config saves", got.SequenceEqual(want), string.Join(" | ", got));
    await outbox.DisposeAsync();
}

if (which is "all" or "5")
{
    var thai = new System.Globalization.CultureInfo("th-TH");
    var oldCulture = System.Globalization.CultureInfo.CurrentCulture;
    System.Globalization.CultureInfo.DefaultThreadCurrentCulture = thai;
    System.Globalization.CultureInfo.CurrentCulture = thai;
    var dir = Path.Combine(Path.GetTempPath(), "gw-issue5", Guid.NewGuid().ToString("N"));
    var outbox = new Outbox(dir);
    var saved = new TaskCompletionSource();
    outbox.Write(new Envelope { event_id = Guid.NewGuid().ToString(), machine_id = "M1", data_group = "PROCESS" }, 0, () => saved.TrySetResult());
    await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
    outbox.MarkSent(outbox.ReadUnsent(10).Select(r => r.Key));
    await outbox.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var today = Directory.GetFiles(dir, "outbox-*.db").Select(Path.GetFileName).Single()!;
    // two fully delivered files from 30 days ago: one named the right way, one with the Buddhist year
    var old = DateTime.Now.Date.AddDays(-30);
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    File.Copy(Path.Combine(dir, today), Path.Combine(dir, $"outbox-{old.ToString("yyyy-MM-dd", inv)}.db"));
    File.Copy(Path.Combine(dir, today), Path.Combine(dir, $"outbox-{old.AddYears(543).ToString("yyyy-MM-dd", inv)}.db"));
    var again = new Outbox(dir);
    var removed = again.PurgeOldFiles();
    await again.DisposeAsync();
    System.Globalization.CultureInfo.CurrentCulture = oldCulture;
    System.Globalization.CultureInfo.DefaultThreadCurrentCulture = null;
    Report("#5 Thai locale: day file name", today.StartsWith($"outbox-{DateTime.Now.Year}-"), today);
    Report("#5 Thai locale: old delivered files cleaned up", removed == 2, $"{removed} of 2 old files removed");
}

static async Task WriteSaved(Outbox outbox, string eventId)
{
    var saved = new TaskCompletionSource();
    outbox.Write(new Envelope { event_id = eventId, machine_id = "M1", data_group = "PROCESS" }, 0, () => saved.TrySetResult());
    await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
}

if (which is "all" or "midnight")
{
    // A message saved yesterday (event X) is sent again after midnight: today's file must not get it.
    var dir = Path.Combine(Path.GetTempPath(), "gw-midnight", Guid.NewGuid().ToString("N"));
    var first = new Outbox(dir);
    await WriteSaved(first, "X");
    await first.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var todayFile = Path.Combine(dir, $"outbox-{DateTime.Now.ToString("yyyy-MM-dd", inv)}.db");
    File.Move(todayFile, Path.Combine(dir, $"outbox-{DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd", inv)}.db"));
    var second = new Outbox(dir);
    await WriteSaved(second, "X");   // the resend: still acked (onSaved runs)
    await WriteSaved(second, "Y");   // a new message
    await second.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={todayFile};Mode=ReadOnly;Pooling=False");
    conn.Open();
    using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT group_concat(event_id) FROM outbox;";
    var today = cmd.ExecuteScalar() as string;
    Report("#4 resend across midnight not stored twice", today == "Y", $"today's file holds [{today}] (yesterday holds X)");
}

if (which is "all" or "409")
{
    // MES answers 409 Conflict ("already have it"): counted as delivered, not a failed message.
    var port = 19900 + Random.Shared.Next(90);
    var mes = new System.Net.HttpListener(); mes.Prefixes.Add($"http://localhost:{port}/"); mes.Start();
    _ = Task.Run(async () => { while (mes.IsListening) { try { var c = await mes.GetContextAsync(); c.Response.StatusCode = 409; c.Response.Close(); } catch { break; } } });
    var dir = Path.Combine(Path.GetTempPath(), "gw-409", Guid.NewGuid().ToString("N"));
    var outbox = new Outbox(dir);
    await WriteSaved(outbox, Guid.NewGuid().ToString());
    using var stop = new CancellationTokenSource();
    var forwarder = new MesForwarder(new HttpClient(), $"http://localhost:{port}/", outbox).RunAsync(stop.Token);
    await Task.Delay(1500);
    var waiting = outbox.CountUnsent();
    var failedRows = EdgeGateway.Admin.OutboxReader.CountFailed(); // reads the gateway's folder, not this one: use the outbox instead
    stop.Cancel(); try { await forwarder; } catch { }
    mes.Stop(); await outbox.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var file = Directory.GetFiles(dir, "outbox-*.db").Single();
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
    conn.Open();
    using var cmd = conn.CreateCommand(); cmd.CommandText = "SELECT sent, failed_at IS NOT NULL FROM outbox;";
    using var r = cmd.ExecuteReader(); r.Read();
    Report("#4 MES 409 counts as delivered", r.GetInt64(0) == 1 && r.GetInt64(1) == 0 && waiting == 0, $"sent {r.GetInt64(0)}, failed {r.GetInt64(1)}, waiting {waiting}");
}

if (which is "all" or "extras")
{
    // An extra field (no MES column) changing alone makes no MES row.
    var (root, json) = TempConfig();
    var pmPath = Path.Combine(root, "point-map.json");
    File.WriteAllText(pmPath, File.ReadAllText(pmPath).Replace("[{", """[{"tag":"evt","dataGroup":"TRACEABILITY","fieldName":"event_time"},{"""));
    var store = new GatewayConfigStore(root, json);
    var outbox = new Outbox(Path.Combine(root, "outbox"));
    var sender = new IntervalSender(outbox, new EnvelopeBuilder("TEST", "SRC", "TCP"), () => store.Current);
    var t = DateTimeOffset.UtcNow;
    await SendRaw(store, sender, new() { ["product_sn"] = "P1", ["evt"] = "1" }, t);
    await SendRaw(store, sender, new() { ["product_sn"] = "P1", ["evt"] = "2" }, t.AddSeconds(1)); // only the extra changed
    await SendRaw(store, sender, new() { ["product_sn"] = "P2", ["evt"] = "3" }, t.AddSeconds(2));
    await Task.Delay(2500);
    var rows = outbox.ReadUnsent(100).Where(r => r.Envelope.data_group == "TRACEABILITY").Select(r => r.Envelope.payload["product_serial_number"]?.ToString()).ToList();
    Report("#5 extra field change alone makes no row", rows.SequenceEqual(new[] { "P1", "P2" }), $"rows: {string.Join(", ", rows)}");
    await outbox.DisposeAsync();
}

// ---- Linear SVI-95 ... SVI-101 ----

// One row with its group's state key (machine + data_source + group), as IntervalSender writes it.
static Task<bool> QueueRow(Outbox outbox, string machine, string group, long sendAfterMs = 0, object? value = null)
{
    var saved = new TaskCompletionSource<bool>();
    var envelope = new Envelope { event_id = Guid.NewGuid().ToString(), machine_id = machine, data_group = group, payload = new() { ["v"] = value } };
    outbox.Write(envelope, sendAfterMs, () => saved.TrySetResult(true), new SavedState(SavedState.KeyOf(machine, "", group), new(), null));
    return saved.Task;
}

// A fake MES on localhost that answers each envelope with status(body).
static System.Net.HttpListener FakeMes(int port, Func<string, int> status)
{
    var mes = new System.Net.HttpListener(); mes.Prefixes.Add($"http://localhost:{port}/"); mes.Start();
    _ = Task.Run(async () =>
    {
        while (mes.IsListening)
        {
            try
            {
                var c = await mes.GetContextAsync();
                var body = await new StreamReader(c.Request.InputStream).ReadToEndAsync();
                c.Response.StatusCode = status(body); c.Response.Close();
            }
            catch { break; }
        }
    });
    return mes;
}

// Rows per state in every day file of an outbox folder, optionally only one machine's.
static (long Sent, long Failed, long Waiting) Counts(string dir, string? machine = null)
{
    long sent = 0, failedRows = 0, waiting = 0;
    foreach (var file in Directory.GetFiles(dir, "outbox-*.db"))
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(sent = 1), 0), COALESCE(SUM(sent = 0 AND failed_at IS NOT NULL), 0), COALESCE(SUM(sent = 0 AND failed_at IS NULL AND discarded_at IS NULL), 0) " +
                          "FROM outbox WHERE $m IS NULL OR json_extract(body, '$.machine_id') = $m;";
        cmd.Parameters.AddWithValue("$m", (object?)machine ?? DBNull.Value);
        using var r = cmd.ExecuteReader(); r.Read();
        sent += r.GetInt64(0); failedRows += r.GetInt64(1); waiting += r.GetInt64(2);
    }
    return (sent, failedRows, waiting);
}

var inv2 = System.Globalization.CultureInfo.InvariantCulture;
string DayName(DateTime day) => $"outbox-{day.ToString("yyyy-MM-dd", inv2)}.db";

if (which is "all" or "99")
{
    // SVI-99: an older row of a group that is not due yet (paced before a restart) holds the
    // newer rows of that group back, so MES gets them in order.
    var dir = Path.Combine(Path.GetTempPath(), "gw-99", Guid.NewGuid().ToString("N"));
    var outbox = new Outbox(dir);
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    await QueueRow(outbox, "M1", "PRODUCTION", now + 3000, "older");
    await QueueRow(outbox, "M1", "PRODUCTION", 0, "newer");
    var early = outbox.ReadUnsent(100).Select(r => r.Envelope.payload["v"]?.ToString()).ToList();
    await Task.Delay(3200);
    var later = outbox.ReadUnsent(100).Select(r => r.Envelope.payload["v"]?.ToString()).ToList();
    await outbox.DisposeAsync();
    Report("SVI-99 group order kept when an older row is not due yet", early.Count == 0 && later.SequenceEqual(new[] { "older", "newer" }),
        $"before the older one is due: [{string.Join(", ", early)}], after: [{string.Join(", ", later)}]");
}

if (which is "all" or "99")
{
    // SVI-99 as reported: TRACEABILITY sendEveryMs 3000. P-A (due now), P-B (due in 3 s), then a
    // restart (new IntervalSender) or sendEveryMs set to 0, then P-C (due now). MES must get A, B, C.
    async Task<string> Delivered(Outbox outbox)
    {
        var order = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            var rows = outbox.ReadUnsent(100);
            order.AddRange(rows.Select(r => r.Envelope.payload["product_serial_number"]?.ToString() ?? "?"));
            if (rows.Count > 0) outbox.MarkSent(rows.Select(r => r.Key));
            await Task.Delay(200);
        }
        return string.Join(", ", order);
    }
    foreach (var variant in new[] { "restart", "sendEveryMs 0" })
    {
        var (root, json) = TempConfig();
        File.WriteAllText(Path.Combine(root, "timestamp-rules.json"), """{"TRACEABILITY":{"mode":"SinglePoint","sendEveryMs":3000}}""");
        var store = new GatewayConfigStore(root, json);
        var outbox = new Outbox(Path.Combine(root, "outbox"));
        var sender = new IntervalSender(outbox, new EnvelopeBuilder("TEST", "SRC", "TCP"), () => store.Current);
        var t = DateTimeOffset.UtcNow;
        await SendRaw(store, sender, new() { ["product_sn"] = "P-A" }, t);
        await SendRaw(store, sender, new() { ["product_sn"] = "P-B" }, t.AddSeconds(1));
        if (variant == "restart")
            sender = new IntervalSender(outbox, new EnvelopeBuilder("TEST", "SRC", "TCP"), () => store.Current);
        else
        {
            File.WriteAllText(Path.Combine(root, "timestamp-rules.json"), """{"TRACEABILITY":{"mode":"SinglePoint","sendEveryMs":0}}""");
            store.Reload();
        }
        await SendRaw(store, sender, new() { ["product_sn"] = "P-C" }, t.AddSeconds(2));
        if (variant == "restart")
        {
            // Write side: the restarted sender paces P-C from P-B, which still waits (seeded NextDueMs).
            long DueOf(string sn)
            {
                using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "outbox", DayName(DateTime.Now))};Mode=ReadOnly;Pooling=False");
                c.Open();
                using var q = c.CreateCommand();
                q.CommandText = "SELECT send_after FROM outbox WHERE json_extract(body, '$.payload.product_serial_number') = $sn;";
                q.Parameters.AddWithValue("$sn", sn);
                return (long)q.ExecuteScalar()!;
            }
            var (dueB, dueC) = (DueOf("P-B"), DueOf("P-C"));
            Report("SVI-99 after a restart a new record is not due before one still waiting", dueC >= dueB,
                $"P-B due {dueB}, P-C due {dueC} ({dueC - dueB:+#;-#;0} ms)");
        }
        var got = await Delivered(outbox);
        await outbox.DisposeAsync();
        Report($"SVI-99 reported case ({variant}): MES gets the group in order", got == "P-A, P-B, P-C", $"MES gets: {got}");
    }

    // The row not due yet is in yesterday's day file (restart just after midnight): it still holds
    // back the newer row of its group in today's file.
    var dir2 = Path.Combine(Path.GetTempPath(), "gw-99", Guid.NewGuid().ToString("N"));
    var first = new Outbox(dir2);
    await QueueRow(first, "M1", "PRODUCTION", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3000, "older");
    await first.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    File.Move(Path.Combine(dir2, DayName(DateTime.Now)), Path.Combine(dir2, DayName(DateTime.Now.AddDays(-1))));
    var outbox2 = new Outbox(dir2);
    await QueueRow(outbox2, "M1", "PRODUCTION", 0, "newer");
    var early2 = outbox2.ReadUnsent(100).Select(r => r.Envelope.payload["v"]?.ToString()).ToList();
    await Task.Delay(3200);
    var later2 = outbox2.ReadUnsent(100).Select(r => r.Envelope.payload["v"]?.ToString()).ToList();
    await outbox2.DisposeAsync();
    Report("SVI-99 group order kept across day files", early2.Count == 0 && later2.SequenceEqual(new[] { "older", "newer" }),
        $"before the older one is due: [{string.Join(", ", early2)}], after: [{string.Join(", ", later2)}]");

    // MES down overnight: M1 has 150 rows in yesterday's file (more than its share of one read)
    // and 10 in today's, M2 has 5. MES must get all of M1's rows in the order they were made.
    var dir3 = Path.Combine(Path.GetTempPath(), "gw-99", Guid.NewGuid().ToString("N"));
    var night = new Outbox(dir3);
    await Task.WhenAll(Enumerable.Range(0, 150).Select(i => QueueRow(night, "M1", "PROCESS", 0, $"y{i}")));
    await Task.WhenAll(Enumerable.Range(0, 5).Select(i => QueueRow(night, "M2", "PROCESS", 0, $"m{i}")));
    await night.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    File.Move(Path.Combine(dir3, DayName(DateTime.Now)), Path.Combine(dir3, DayName(DateTime.Now.AddDays(-1))));
    var morning = new Outbox(dir3);
    await Task.WhenAll(Enumerable.Range(0, 10).Select(i => QueueRow(morning, "M1", "PROCESS", 0, $"t{i}")));
    var m1 = new List<string>();
    for (var pass = 0; pass < 10; pass++)
    {
        var rows = morning.ReadUnsent(200);
        if (rows.Count == 0) break;
        m1.AddRange(rows.Where(r => r.Envelope.machine_id == "M1").Select(r => r.Envelope.payload["v"]?.ToString() ?? "?"));
        morning.MarkSent(rows.Select(r => r.Key));
    }
    await morning.DisposeAsync();
    var want = Enumerable.Range(0, 150).Select(i => $"y{i}").Concat(Enumerable.Range(0, 10).Select(i => $"t{i}")).ToList();
    var firstWrong = m1.Zip(want).Select((x, i) => (x, i)).FirstOrDefault(p => p.x.First != p.x.Second);
    Report("SVI-99 overnight backlog over two day files keeps each group's order", m1.SequenceEqual(want),
        m1.SequenceEqual(want) ? $"{m1.Count} rows in order" : $"{m1.Count} rows; first out of order at #{firstWrong.i}: {firstWrong.x.First} (want {firstWrong.x.Second})");
}

if (which is "all" or "101")
{
    // SVI-101: yesterday's file looked done (no unsent rows) just after midnight, then the writer's
    // last batch for it was committed: those rows must still be read, not skipped until a restart.
    var dir = Path.Combine(Path.GetTempPath(), "gw-101", Guid.NewGuid().ToString("N"));
    var first = new Outbox(dir);
    await QueueRow(first, "M1", "PROCESS");
    first.MarkSent(first.ReadUnsent(10).Select(r => r.Key));
    await first.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var yesterday = Path.Combine(dir, DayName(DateTime.Now.AddDays(-1)));
    File.Move(Path.Combine(dir, DayName(DateTime.Now)), yesterday);
    var outbox = new Outbox(dir);
    outbox.ReadUnsent(10); // sees yesterday with nothing to send
    using (var late = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={yesterday};Pooling=False"))
    {
        late.Open();
        using var cmd = late.CreateCommand();
        cmd.CommandText = "INSERT INTO outbox (event_id, data_group, body, sent, created_at, send_after) VALUES ('late', 'PROCESS', $body, 0, '2026-01-01T00:00:00Z', 0);";
        cmd.Parameters.AddWithValue("$body", System.Text.Json.JsonSerializer.Serialize(new Envelope { event_id = "late", machine_id = "M1", data_group = "PROCESS" }));
        cmd.ExecuteNonQuery();
    }
    var rows = outbox.ReadUnsent(10).Select(r => r.Envelope.event_id).ToList();
    var backlog = outbox.CountUnsent();
    await outbox.DisposeAsync();
    Report("SVI-101 a late batch in yesterday's file is still read and counted", rows.Contains("late") && backlog == 1,
        $"read: [{string.Join(", ", rows)}], backlog {backlog} (want 1)");
}

if (which is "all" or "100")
{
    // SVI-100: whole numbers above 2^53 reach MES exactly, and a change between two of them is a change.
    var (root, json) = TempConfig();
    var store = new GatewayConfigStore(root, json);
    var outbox = new Outbox(Path.Combine(root, "outbox"));
    var sender = new IntervalSender(outbox, new EnvelopeBuilder("TEST", "SRC", "TCP"), () => store.Current);
    object? Parse(string number) { using var d = System.Text.Json.JsonDocument.Parse($"{{\"product_sn\":{number}}}"); return PayloadParser.Parse(d.RootElement)["product_sn"]; }
    var t = DateTimeOffset.UtcNow;
    await SendRaw(store, sender, new() { ["product_sn"] = Parse("9007199254740993") }, t);
    await SendRaw(store, sender, new() { ["product_sn"] = Parse("9007199254740995") }, t.AddSeconds(1));
    await Task.Delay(1500);
    var sent = outbox.ReadUnsent(100).Select(r => System.Text.Json.JsonSerializer.Serialize(r.Envelope.payload["product_serial_number"])).ToList();
    await outbox.DisposeAsync();
    Report("SVI-100 integers above 2^53 kept exactly", sent.SequenceEqual(new[] { "9007199254740993", "9007199254740995" }), $"MES gets: [{string.Join(", ", sent)}]");
}

if (which is "all" or "reassign")
{
    // #30 / #31: another mapping for a tab. A confirmed tab needs mappingPassword when one is set (none set =
    // none asked), an automatic one never and stays automatic; the old files are kept as .bak, no .tmp left behind.
    var (root, json) = TempConfig("A", "B");
    File.WriteAllText(Path.Combine(root, "machines", "B", "point-map.json"), """[{"tag":"bTag","dataGroup":"TRACEABILITY","fieldName":"station_id"}]""");
    var reg = new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json);
    string Tags(string id) => string.Join(",", reg.StoreFor(id)!.Current.PointMap.Select(p => p.Tag));
    var aFolder = Path.Combine(root, "machines", "A");
    var wrong = reg.Reassign("A", "B", "nope", "pw");
    var self = reg.Reassign("A", "A", "pw", "pw");
    var unknown = reg.Reassign("A", "Z", "pw", "pw");
    var untouched = Tags("A");
    var ok = reg.Reassign("A", "B", "pw", "pw");
    var copied = Tags("A");
    var leftovers = Directory.GetFiles(aFolder, "*.tmp").Length;
    var bak = File.ReadAllText(Path.Combine(aFolder, "point-map.json.bak")).Contains("product_sn");
    var noneSet = reg.Reassign("A", null, null, ""); // confirmed tab, no password set: back to the template
    var afterNoneSet = Tags("A");
    var created = reg.CreateAuto("C", "B");
    var autoNoPassword = reg.Reassign("C", null, null, "pw");
    var got = $"wrong: {wrong}; self: {self}; unknown: {unknown}; before: [{untouched}]; ok: {ok ?? "done"} [{copied}] " +
              $".tmp {leftovers}, .bak has the old: {bak}; no password set: {noneSet ?? "done"} [{afterNoneSet}]; " +
              $"auto tab without password: {created ?? "created"} / {autoNoPassword ?? "done"} [{Tags("C")}] still auto: {reg.IsAuto("C")}";
    Report("#30/#31 change a tab's mapping", noneSet is null && afterNoneSet == "product_sn,module_no,MACHINE_STATUS,MACHINE_MODE"
        && wrong == MachineRegistry.WrongPassword && self is not null && unknown is not null
        && untouched == "product_sn,module_no,MACHINE_STATUS,MACHINE_MODE" && ok is null && copied == "bTag" && leftovers == 0 && bak
        && created is null && autoNoPassword is null && Tags("C") == "product_sn,module_no,MACHINE_STATUS,MACHINE_MODE" && reg.IsAuto("C"), got);

    // The config page never gets mappingPassword back; saving the mask keeps the stored one.
    var file = Path.Combine(root, "machine-config.json");
    File.WriteAllText(file, """{"gatewayId":"T","mappingPassword":"secret"}""");
    var shown = System.Text.Json.Nodes.JsonNode.Parse("""{"gatewayId":"T","mappingPassword":"secret"}""");
    EdgeGateway.Admin.AdminWebServer.MaskPassword(shown, "mappingPassword");
    string Saved(string sent) => System.Text.Json.Nodes.JsonNode.Parse(EdgeGateway.Admin.AdminWebServer.KeepStoredPassword(
        $$"""{"gatewayId":"T","mappingPassword":"{{sent}}"}""", file, "mappingPassword"))!["mappingPassword"]!.GetValue<string>();
    var masked = shown!["mappingPassword"]!.GetValue<string>();
    Report("#31 mappingPassword masked on the page", masked == EdgeGateway.Admin.AdminWebServer.PasswordMask
        && Saved(EdgeGateway.Admin.AdminWebServer.PasswordMask) == "secret" && Saved("new") == "new",
        $"page gets {masked}; saving the mask keeps: {Saved(EdgeGateway.Admin.AdminWebServer.PasswordMask)}; saving new: {Saved("new")}");
}

if (which is "all" or "autoconfirm")
{
    // A tab created automatically that nobody confirmed or cancelled within 3 days counts as confirmed.
    var (root, json) = TempConfig("OLD", "NEW", "NOAT");
    var reg = new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json);
    var now = DateTimeOffset.Now;
    void Mark(string id, string marker) => File.WriteAllText(Path.Combine(root, "machines", id, MachineRegistry.AutoMarker), marker);
    Mark("OLD", System.Text.Json.JsonSerializer.Serialize(new { copiedFrom = "", at = now.AddDays(-3).AddMinutes(-1) }));
    Mark("NEW", System.Text.Json.JsonSerializer.Serialize(new { copiedFrom = "", at = now.AddDays(-2) }));
    Mark("NOAT", "{}"); // no time in it: the file's own (now)
    var confirmed = reg.ConfirmOldAutoTabs(GatewayHost.AutoTabConfirmAfter, now);
    var again = reg.ConfirmOldAutoTabs(GatewayHost.AutoTabConfirmAfter, now);
    Report("auto tab counts as confirmed after 3 days", confirmed.SequenceEqual(new[] { "OLD" }) && again.Count == 0
        && !reg.IsAuto("OLD") && reg.IsAuto("NEW") && reg.IsAuto("NOAT"),
        $"confirmed [{string.Join(",", confirmed)}] (want OLD), then [{string.Join(",", again)}]; still auto: NEW {reg.IsAuto("NEW")}, NOAT {reg.IsAuto("NOAT")}");
}

if (which is "all" or "race")
{
    // Review: the auto-mapper and a page save wrote point-map.json under different locks, and a save from the
    // copy the page loaded earlier dropped tags mapped automatically since. Now one lock per file, and the page's
    // copy has a version (ETag): a save from an older one (If-Match) is refused (409).
    var (root, json) = TempConfig("M1");
    DataFolder.Use(root);
    var registry = new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json);
    var port = 19400 + Random.Shared.Next(90);
    var page = new EdgeGateway.Admin.AdminWebServer(port, "wwwroot", registry, new GatewayHost(registry, json, null, new MessageIdFilter()),
        GatewayLog.Install(Path.Combine(root, "logs"), 1), json);
    using var stop = new CancellationTokenSource();
    var serving = page.RunAsync(stop.Token);
    await Task.Delay(300);
    using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}/") };
    const string url = "api/config/point-map?machine=M1";
    async Task<(string? Version, string Body)> Load() { using var r = await http.GetAsync(url); return (r.Headers.ETag?.Tag, await r.Content.ReadAsStringAsync()); }
    async Task<int> Save(string body, string? version)
    {
        using var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        if (version is not null) put.Headers.TryAddWithoutValidation("If-Match", version);
        using var r = await http.SendAsync(put);
        return (int)r.StatusCode;
    }
    var file = Path.Combine(root, "machines", "M1", "point-map.json");
    var (loadedVersion, pageCopy) = await Load();
    registry.StoreFor("M1")!.AddPointMapEntry(new PointMapEntry { Tag = "GOOD_QTY", DataGroup = "TRACEABILITY", FieldName = "station_id", Auto = 1 }); // meanwhile
    var stale = await Save(pageCopy, loadedVersion);
    var autoKept = File.ReadAllText(file).Contains("GOOD_QTY");
    var (newVersion, newCopy) = await Load();
    var current = await Save(newCopy, newVersion);
    var noCheck = await Save(newCopy, null); // a script without If-Match: saved, as before
    stop.Cancel(); try { await serving; } catch { }
    Report("review: a save from an older copy can't drop auto-mapped tags", loadedVersion is not null && stale == 409 && autoKept && current == 200 && noCheck == 200,
        $"version sent: {loadedVersion is not null}; save from the older copy -> {stale} (want 409), GOOD_QTY kept: {autoKept}; from the newer -> {current}; without If-Match -> {noCheck}");
}

if (which is "all" or "utc")
{
    // Every time to UTC: a span start / end the machine sends (zone converted, none = UTC, other text as sent) ...
    var stamper = new TimeStamper(new Dictionary<string, TimestampRule>
    {
        ["MACHINE_STATUS"] = new() { Mode = TimestampMode.Span, TriggerField = "machine_status", StartField = "status_start_time", EndField = "status_end_time" },
    });
    string End(string sent)
    {
        var fields = new Dictionary<string, object?> { ["machine_status"] = "RUN", ["status_end_time"] = sent };
        stamper.Stamp(Guid.NewGuid().ToString(), "MACHINE_STATUS", fields, DateTimeOffset.UtcNow);
        return fields["status_end_time"] as string ?? "";
    }
    var ends = new[] { End("2026-09-02T04:50:15.045+07:00"), End("2026-09-02 04:50:15"), End("n/a") };
    Report("utc: machine's span end time to UTC", ends.SequenceEqual(new[] { "2026-09-01T21:50:15.045Z", "2026-09-02T04:50:15.000Z", "n/a" }), string.Join("; ", ends));

    // ... and the message's "timestamp": +07:00 converted, no zone = UTC (event_time at MES).
    var (root, json) = TempConfig();
    var tcp = 19500 + Random.Shared.Next(300);
    var mesPort = 19900 + Random.Shared.Next(90);
    var bodies = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var mes = FakeMes(mesPort, body => { bodies.Enqueue(body); return 200; });
    File.WriteAllText(Path.Combine(root, "machine-config.json"),
        $$"""{"gatewayId":"T","listenAddress":"127.0.0.1","tcpPort":{{tcp}},"mesUrl":"http://localhost:{{mesPort}}/","unregisteredMachines":"shared","unmappedTags":"send"}""");
    File.WriteAllText(Path.Combine(root, "mqtt-config.json"),
        """{"brokerAddress":"127.0.0.1","brokerPort":1,"clientIdSuffix":"checks","reconnectCooldownSeconds":60,"area":"T","keepaliveIntervalSeconds":60,"motionDeadband":0.01,"maxPublishRateMs":50}""");
    DataFolder.Use(root);
    var host = new GatewayHost(new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json), json, null, new MessageIdFilter());
    using var stop = new CancellationTokenSource();
    var run = host.RunAsync(stop.Token);
    for (var i = 0; i < 50 && host.State != "running"; i++) await Task.Delay(100);
    using (var c = new System.Net.Sockets.TcpClient())
    {
        await c.ConnectAsync("127.0.0.1", tcp);
        var s = c.GetStream();
        var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\n" }; var r = new StreamReader(s);
        foreach (var (id, machine, stamp) in new[] { ("U1", "M1", "2026-10-30T10:00:00.000+07:00"), ("U2", "M2", "2026-10-30T10:00:00.000") })
        {
            await w.WriteLineAsync($$$"""{"message_id":"{{{id}}}","machine_id":"{{{machine}}}","timestamp":"{{{stamp}}}","data":{"product_sn":"SN"}}""");
            await r.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
    for (var i = 0; i < 100 && bodies.Count < 2; i++) await Task.Delay(100);
    stop.Cancel(); try { await run; } catch { }
    mes.Stop();
    var events = bodies.Select(b => System.Text.Json.JsonDocument.Parse(b).RootElement)
        .ToDictionary(e => e.GetProperty("machine_id").GetString()!, e => e.GetProperty("event_time").GetString());
    Report("utc: message timestamp to UTC", events.GetValueOrDefault("M1") == "2026-10-30T03:00:00.000Z" && events.GetValueOrDefault("M2") == "2026-10-30T10:00:00.000Z",
        $"+07:00 -> {events.GetValueOrDefault("M1")} (want 03:00Z); no zone -> {events.GetValueOrDefault("M2")} (want 10:00Z)");
}

if (which is "all" or "29")
{
    // #29: start and end of a cycle in the same message -> cycle_time = end - start (s); the machine's own cycle_time wins.
    var (root, json) = TempConfig();
    File.WriteAllText(Path.Combine(root, "mes-schema.json"), """{"PRODUCTION":["station_id","cycle_time"]}""");
    File.WriteAllText(Path.Combine(root, "point-map.json"), """[{"tag":"start_time","dataGroup":"PRODUCTION","fieldName":"cycle_start_time"},{"tag":"cycle_end_time","dataGroup":"PRODUCTION","fieldName":"cycle_end_time"},{"tag":"cycle_time","dataGroup":"PRODUCTION","fieldName":"cycle_time"}]""");
    File.WriteAllText(Path.Combine(root, "timestamp-rules.json"), """{"PRODUCTION":{"mode":"Span","triggerField":"cycle_active","startField":"cycle_start_time","endField":"cycle_end_time"}}""");
    var store = new GatewayConfigStore(root, json);
    var outbox = new Outbox(Path.Combine(root, "outbox"));
    var sender = new IntervalSender(outbox, new EnvelopeBuilder("TEST", "SRC", "TCP"), () => store.Current);
    var t = DateTimeOffset.UtcNow;
    await SendRaw(store, sender, new() { ["start_time"] = "2026-10-29T10:00:00.000+07:00", ["cycle_end_time"] = "2026-10-29T10:01:56.850+07:00" }, t);
    await SendRaw(store, sender, new() { ["start_time"] = "2026-10-29T10:02:00.000+07:00", ["cycle_end_time"] = "2026-10-29T10:03:00.000+07:00", ["cycle_time"] = 99.0 }, t.AddSeconds(1));
    await Task.Delay(1500);
    var sent = outbox.ReadUnsent(100).Select(r => System.Text.Json.JsonSerializer.Serialize(r.Envelope.payload["cycle_time"])).ToList();
    await outbox.DisposeAsync();
    Report("#29 cycle_time from start and end in one message", sent.SequenceEqual(new[] { "116.85", "99" }), $"MES gets cycle_time: [{string.Join(", ", sent)}] (want 116.85, 99)");

    // LDM: start_time / cycle_end_time mapped to MACHINE_STATUS (MES needs them there): they stay there,
    // and PRODUCTION still gets cycle_time from them.
    var (root2, json2) = TempConfig();
    File.WriteAllText(Path.Combine(root2, "mes-schema.json"), """{"PRODUCTION":["station_id","cycle_time"],"MACHINE_STATUS":["machine_status","status_start_time","status_end_time"]}""");
    File.WriteAllText(Path.Combine(root2, "point-map.json"), """[{"tag":"start_time","dataGroup":"MACHINE_STATUS","fieldName":"status_start_time"},{"tag":"cycle_end_time","dataGroup":"MACHINE_STATUS","fieldName":"status_end_time"}]""");
    File.WriteAllText(Path.Combine(root2, "timestamp-rules.json"), """{"PRODUCTION":{"mode":"Span","triggerField":"cycle_active","startField":"cycle_start_time","endField":"cycle_end_time"},"MACHINE_STATUS":{"mode":"Span","triggerField":"machine_status","startField":"status_start_time","endField":"status_end_time"}}""");
    var store2 = new GatewayConfigStore(root2, json2);
    var outbox2 = new Outbox(Path.Combine(root2, "outbox"));
    var sender2 = new IntervalSender(outbox2, new EnvelopeBuilder("TEST", "SRC", "TCP"), () => store2.Current);
    await SendRaw(store2, sender2, new() { ["start_time"] = "2026-09-02T04:48:53.832+07:00", ["cycle_end_time"] = "2026-09-02T04:50:15.045+07:00" }, t);
    await Task.Delay(1500);
    var rows2 = outbox2.ReadUnsent(100).Select(r => r.Envelope).ToList();
    await outbox2.DisposeAsync();
    string Got(string group, string field) => rows2.FirstOrDefault(e => e.data_group == group)?.payload.GetValueOrDefault(field) is { } v ? System.Text.Json.JsonSerializer.Serialize(v) : "none";
    var ldm = $"PRODUCTION cycle_time {Got("PRODUCTION", "cycle_time")}, MACHINE_STATUS start {Got("MACHINE_STATUS", "status_start_time")} end {Got("MACHINE_STATUS", "status_end_time")}";
    Report("#29 cycle_time from MACHINE_STATUS start / end, which stay", Got("PRODUCTION", "cycle_time") == "81.213"
        && Got("MACHINE_STATUS", "status_start_time") != "none" && Got("MACHINE_STATUS", "status_end_time") != "none", ldm);
}

if (which is "all" or "cycleactive")
{
    // Review: a machine that only signals cycle_active on / off. Its change is stamped at the message's own time
    // (not at the next record), and a cycle ending gives cycle_time = how long it was on; a cycle starting makes no row.
    var (root, json) = TempConfig();
    File.WriteAllText(Path.Combine(root, "mes-schema.json"), """{"PRODUCTION":["station_id","cycle_time"]}""");
    File.WriteAllText(Path.Combine(root, "point-map.json"), """[{"tag":"CYCLE_ACTIVE","dataGroup":"PRODUCTION","fieldName":"cycle_active"},{"tag":"STATION","dataGroup":"PRODUCTION","fieldName":"station_id"}]""");
    File.WriteAllText(Path.Combine(root, "timestamp-rules.json"), """{"PRODUCTION":{"mode":"Span","triggerField":"cycle_active","startField":"cycle_start_time","endField":"cycle_end_time","sendEveryMs":0}}""");
    var outboxDir = Path.Combine(root, "outbox");
    var store = new GatewayConfigStore(root, json);
    var outbox = new Outbox(outboxDir);
    var sender = new IntervalSender(outbox, new EnvelopeBuilder("TEST", "SRC", "TCP"), () => store.Current);
    var t0 = DateTimeOffset.UtcNow;
    await SendRaw(store, sender, new() { ["CYCLE_ACTIVE"] = 1.0, ["STATION"] = "S1" }, t0);  // station: a row
    await SendRaw(store, sender, new() { ["CYCLE_ACTIVE"] = 0.0 }, t0.AddSeconds(42.5));       // cycle 1 ends: 42.5 s
    await SendRaw(store, sender, new() { ["CYCLE_ACTIVE"] = 1.0 }, t0.AddSeconds(50));         // cycle 2 starts: no row
    await Task.Delay(300);
    await outbox.DisposeAsync();
    // The gateway restarts in the middle of cycle 2 (new outbox, mapping, TimeStamper, sender on the same folder).
    store = new GatewayConfigStore(root, json);
    outbox = new Outbox(outboxDir);
    sender = new IntervalSender(outbox, new EnvelopeBuilder("TEST", "SRC", "TCP"), () => store.Current);
    await SendRaw(store, sender, new() { ["CYCLE_ACTIVE"] = 0.0 }, t0.AddSeconds(80.25));      // cycle 2 ends: 30.25 s
    await Task.Delay(500);
    var rows = outbox.ReadUnsent(100).Select(r => r.Envelope.payload.GetValueOrDefault("cycle_time") is { } v ? System.Text.Json.JsonSerializer.Serialize(v) : "-").ToList();
    var openLeft = outbox.OpenSpans().Count;
    await outbox.DisposeAsync();
    Report("review: cycle_time from cycle_active on / off, also across a restart", rows.SequenceEqual(new[] { "-", "42.5", "30.25" }) && openLeft == 0,
        $"rows' cycle_time: [{string.Join(", ", rows)}] (want -, 42.5, 30.25: no row when a cycle starts; cycle 2 spans a restart); open spans left: {openLeft}");
}

if (which is "all" or "23")
{
    // #23: problems pushed to alarmWebhookUrl when one starts or clears; a failed POST is sent again next check.
    var port = 19900 + Random.Shared.Next(90);
    var bodies = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var calls = 0;
    using var hookServer = FakeMes(port, body => { bodies.Enqueue(body); return Interlocked.Increment(ref calls) == 1 ? 500 : 200; });
    var hook = new AlarmWebhook(new Uri($"http://localhost:{port}/"), "T");
    var held = new[] { ("held:M1", "M1 held") };
    await hook.CheckAsync(held);                                    // 500: not counted as told
    await hook.CheckAsync(held);                                    // raised again, 200
    await hook.CheckAsync(held);                                    // nothing new: no POST
    await hook.CheckAsync(Array.Empty<(string, string)>());         // cleared
    string Lists(string body) { var e = System.Text.Json.JsonDocument.Parse(body).RootElement; return $"raised [{string.Join(",", e.GetProperty("raised").EnumerateArray())}] cleared [{string.Join(",", e.GetProperty("cleared").EnumerateArray())}]"; }
    var got = bodies.Select(Lists).ToList();
    Report("#23 webhook: raised, retried after a failure, cleared", got.SequenceEqual(new[] { "raised [M1 held] cleared []", "raised [M1 held] cleared []", "raised [] cleared [M1 held]" }),
        string.Join("; ", got));

    // Through the gateway: a new machine gets a tab automatically (hold mode) -> pushed with the next [stats] check.
    var (root, json) = TempConfig();
    var tcp = 19500 + Random.Shared.Next(300);
    var mesPort = 19900 + Random.Shared.Next(90);
    while (mesPort == port) mesPort = 19900 + Random.Shared.Next(90);
    using var mes = FakeMes(mesPort, _ => 200);
    bodies.Clear();
    File.WriteAllText(Path.Combine(root, "machine-config.json"),
        $$"""{"gatewayId":"T","listenAddress":"127.0.0.1","tcpPort":{{tcp}},"mesUrl":"http://localhost:{{mesPort}}/","unregisteredMachines":"hold","unmappedTags":"send","alarmWebhookUrl":"http://localhost:{{port}}/"}""");
    File.WriteAllText(Path.Combine(root, "mqtt-config.json"),
        """{"brokerAddress":"127.0.0.1","brokerPort":1,"clientIdSuffix":"checks","reconnectCooldownSeconds":60,"area":"T","keepaliveIntervalSeconds":60,"motionDeadband":0.01,"maxPublishRateMs":50}""");
    DataFolder.Use(root);
    var host = new GatewayHost(new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json), json, null, new MessageIdFilter());
    using var stop = new CancellationTokenSource();
    var run = host.RunAsync(stop.Token);
    for (var i = 0; i < 50 && host.State != "running"; i++) await Task.Delay(100);
    using (var c = new System.Net.Sockets.TcpClient())
    {
        await c.ConnectAsync("127.0.0.1", tcp);
        await new StreamWriter(c.GetStream()) { AutoFlush = true, NewLine = "\n" }.WriteLineAsync("""{"message_id":"A1","machine_id":"NEW-9","data":{"product_sn":"SN-1"}}""");
        await Task.Delay(300);
    }
    for (var i = 0; i < 130 && bodies.IsEmpty; i++) await Task.Delay(100); // one [stats] check: 10 s
    stop.Cancel(); try { await run; } catch { }
    mes.Stop();
    var pushed = bodies.Select(Lists).ToList();
    Report("#23 gateway pushes a tab created automatically", pushed.Count == 1 && pushed[0].Contains("NEW-9: tab created automatically"), string.Join("; ", pushed));
}

if (which is "all" or "range")
{
    // A value that can't be saved (Infinity) must not stay in the group's snapshot: the next change still goes.
    var (root, json) = TempConfig();
    var store = new GatewayConfigStore(root, json);
    var outbox = new Outbox(Path.Combine(root, "outbox"));
    var sender = new IntervalSender(outbox, new EnvelopeBuilder("TEST", "SRC", "TCP"), () => store.Current);
    var t = DateTimeOffset.UtcNow;
    // module_no is not in the next message: before the fix it stayed in the snapshot, so every save failed
    try { await SendRaw(store, sender, new() { ["product_sn"] = "P1", ["module_no"] = double.PositiveInfinity }, t); } catch { }
    string next;
    try { await SendRaw(store, sender, new() { ["product_sn"] = "P2" }, t.AddSeconds(1)); next = "saved"; }
    catch (Exception ex) { next = $"{ex.GetType().Name}: {ex.Message}"; }
    await Task.Delay(1500);
    var sent = outbox.ReadUnsent(100).Select(r => System.Text.Json.JsonSerializer.Serialize(r.Envelope.payload["product_serial_number"])).ToList();
    await outbox.DisposeAsync();
    Report("range: an unsaveable value doesn't block the group", next == "saved" && sent.SequenceEqual(new[] { "\"P2\"" }),
        $"next change: {next}; MES gets: [{string.Join(", ", sent)}] (want [\"P2\"])");

    // 1e400 over TCP: nacked (not left unanswered), and the machine's next line is acked.
    var (root2, json2) = TempConfig();
    var tcp = 19500 + Random.Shared.Next(300);
    var mesPort = 19900 + Random.Shared.Next(90);
    using var mes = FakeMes(mesPort, _ => 200);
    File.WriteAllText(Path.Combine(root2, "machine-config.json"),
        $$"""{"gatewayId":"T","listenAddress":"127.0.0.1","tcpPort":{{tcp}},"mesUrl":"http://localhost:{{mesPort}}/","unregisteredMachines":"shared","unmappedTags":"send"}""");
    File.WriteAllText(Path.Combine(root2, "mqtt-config.json"),
        """{"brokerAddress":"127.0.0.1","brokerPort":1,"clientIdSuffix":"checks","reconnectCooldownSeconds":60,"area":"T","keepaliveIntervalSeconds":60,"motionDeadband":0.01,"maxPublishRateMs":50}""");
    DataFolder.Use(root2);
    var host = new GatewayHost(new MachineRegistry(new GatewayConfigStore(root2, json2), new UnidentifiedTagTracker(), json2), json2, null, new MessageIdFilter());
    using var stop = new CancellationTokenSource();
    var run = host.RunAsync(stop.Token);
    for (var i = 0; i < 50 && host.State != "running"; i++) await Task.Delay(100);
    async Task<string> Send(string line)
    {
        using var c = new System.Net.Sockets.TcpClient();
        await c.ConnectAsync("127.0.0.1", tcp);
        var s = c.GetStream();
        await new StreamWriter(s) { AutoFlush = true, NewLine = "\n" }.WriteLineAsync(line);
        try { return await new StreamReader(s).ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? "(closed)"; }
        catch (TimeoutException) { return "(no answer)"; }
    }
    var bad = await Send("""{"message_id":"R1","machine_id":"M1","data_source":"spi","data":{"product_sn":"SN-1","module_no":1e400}}""");
    var good = await Send("""{"message_id":"R2","machine_id":"M1","data_source":"spi","data":{"product_sn":"SN-2"}}""");
    stop.Cancel(); try { await run; } catch { }
    mes.Stop();
    Report("range: 1e400 is nacked, the next line acked", bad == """{"nack":"R1","reason":"number out of range"}""" && good == """{"ack":"R2"}""",
        $"1e400 -> {bad}; next line -> {good}");
}

if (which is "all" or "98")
{
    // SVI-98: MES 404 / 405 (wrong mesUrl path, MES being redeployed) is MES's problem, not the row's: retried.
    // 413 too, until the real MES codes are known (a proxy / MES size limit set too low).
    foreach (var code in new[] { 404, 413 })
    {
    var port = 19900 + Random.Shared.Next(90);
    var mes = FakeMes(port, _ => code);
    var dir = Path.Combine(Path.GetTempPath(), "gw-98", Guid.NewGuid().ToString("N"));
    var outbox = new Outbox(dir);
    await QueueRow(outbox, "M1", "PROCESS");
    using var stop = new CancellationTokenSource();
    var forwarder = new MesForwarder(new HttpClient(), $"http://localhost:{port}/", outbox).RunAsync(stop.Token);
    await Task.Delay(1500);
    stop.Cancel(); try { await forwarder; } catch { }
    mes.Stop(); await outbox.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    var c = Counts(dir);
    Report($"SVI-98 MES {code} keeps the row waiting (retried)", c.Waiting == 1 && c.Failed == 0, $"waiting {c.Waiting}, failed {c.Failed}");
    }
}

if (which is "all" or "97")
{
    // SVI-97: MES answers 500 for every row of BAD (2,100 rows, older than everyone else's).
    // The other machine's rows must still go, and BAD's first row moves to failed messages after
    // a few tries while MES takes other records, so BAD's next rows go on too.
    var port = 19900 + Random.Shared.Next(90);
    var mes = FakeMes(port, body => body.Contains("\"machine_id\":\"BAD\"") ? 500 : 200);
    var dir = Path.Combine(Path.GetTempPath(), "gw-97", Guid.NewGuid().ToString("N"));
    var outbox = new Outbox(dir);
    var stats = new GatewayStats();
    var writes = Enumerable.Range(0, 2100).Select(i => QueueRow(outbox, "BAD", "PROCESS", 0, i)).ToList();
    await Task.WhenAll(writes);
    using var stop = new CancellationTokenSource();
    var forwarder = new MesForwarder(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, $"http://localhost:{port}/", outbox, stats).RunAsync(stop.Token);
    var goodFeed = Task.Run(async () => { for (var i = 0; !stop.IsCancellationRequested && i < 60; i++) { await QueueRow(outbox, "GOOD", "PROCESS", 0, i); await Task.Delay(1000); } });
    var deadline = DateTime.UtcNow.AddSeconds(50);
    (long Sent, long Failed, long Waiting) good = default, bad = default;
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(1000);
        good = Counts(dir, "GOOD"); bad = Counts(dir, "BAD");
        if (good.Sent > 0 && bad.Failed > 0) break;
    }
    stop.Cancel(); try { await forwarder; } catch { } try { await goodFeed; } catch { }
    mes.Stop(); await outbox.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Report("SVI-97 other machines' rows still go while one machine's rows get 5xx", good.Sent > 0, $"GOOD sent {good.Sent}");
    Report("SVI-97 a row that keeps getting 500 while MES takes others moves to failed", bad.Failed >= 1 && bad.Failed < 2100,
        $"BAD failed {bad.Failed}, waiting {bad.Waiting} (MES up for others: rows are not all dropped at once)");

    // SVI-97 as reported: shared outbox, MES 500 for M1, 200 for others. 2,001 M1 rows, then 3 M2 rows:
    // M2's 3 rows must reach MES within 10 s.
    var port2 = 19900 + Random.Shared.Next(90);
    var mes2 = FakeMes(port2, body => body.Contains("\"machine_id\":\"M1\"") ? 500 : 200);
    var dir2 = Path.Combine(Path.GetTempPath(), "gw-97", Guid.NewGuid().ToString("N"));
    var outbox2 = new Outbox(dir2);
    await Task.WhenAll(Enumerable.Range(0, 2001).Select(i => QueueRow(outbox2, "M1", "PROCESS", 0, i)));
    await Task.WhenAll(Enumerable.Range(0, 3).Select(i => QueueRow(outbox2, "M2", "PROCESS", 0, i)));
    using var stop2 = new CancellationTokenSource();
    var forwarder2 = new MesForwarder(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, $"http://localhost:{port2}/", outbox2, new GatewayStats()).RunAsync(stop2.Token);
    var m2 = Counts(dir2, "M2");
    for (var i = 0; i < 10 && m2.Sent < 3; i++) { await Task.Delay(1000); m2 = Counts(dir2, "M2"); }
    stop2.Cancel(); try { await forwarder2; } catch { }
    mes2.Stop(); await outbox2.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Report("SVI-97 reported case: 3 M2 rows go behind 2,001 stuck M1 rows", m2.Sent == 3, $"M2 sent {m2.Sent} of 3 within 10 s");

    // SVI-97 default setup: a machine alone in its own outbox, MES 500 on its first row and nothing
    // else delivered ("MES took others" never comes). After the tries and the stuck limit it moves to
    // failed messages and the machine's next rows go on.
    var port3 = 19900 + Random.Shared.Next(90);
    var mes3 = FakeMes(port3, body => body.Contains("\"v\":0") ? 500 : 200);
    var dir3 = Path.Combine(Path.GetTempPath(), "gw-97", Guid.NewGuid().ToString("N"));
    var outbox3 = new Outbox(dir3);
    await Task.WhenAll(Enumerable.Range(0, 3).Select(i => QueueRow(outbox3, "M1", "PROCESS", 0, i)));
    using var stop3 = new CancellationTokenSource();
    Console.WriteLine("      (SVI-97 waits ~30 s: 5 tries with the growing retry delay)");
    var forwarder3 = new MesForwarder(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, $"http://localhost:{port3}/", outbox3,
        new GatewayStats(), "M1", stuckLimit: TimeSpan.FromSeconds(5)).RunAsync(stop3.Token);
    var alone = Counts(dir3);
    for (var i = 0; i < 60 && alone.Sent < 2; i++) { await Task.Delay(1000); alone = Counts(dir3); }
    stop3.Cancel(); try { await forwarder3; } catch { }
    mes3.Stop(); await outbox3.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Report("SVI-97 machine alone: stuck row moves to failed after the time limit", alone.Failed == 1 && alone.Sent == 2,
        $"failed {alone.Failed} (want 1), sent {alone.Sent} (want 2), waiting {alone.Waiting}");

    // SVI-98 413 (until the real MES codes are known): a row MES keeps answering 413 follows the 500 rule.
    // Exit 1: another machine delivers meanwhile. Exit 2: the machine is alone, after the time limit.
    foreach (var withOthers in new[] { true, false })
    {
        var port4 = 19900 + Random.Shared.Next(90);
        var mes4 = FakeMes(port4, body => body.Contains("\"machine_id\":\"BIG\"") && body.Contains("\"v\":0") ? 413 : 200);
        var dir4 = Path.Combine(Path.GetTempPath(), "gw-97", Guid.NewGuid().ToString("N"));
        var outbox4 = new Outbox(dir4);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(i => QueueRow(outbox4, "BIG", "PROCESS", 0, i)));
        using var stop4 = new CancellationTokenSource();
        if (!withOthers) Console.WriteLine("      (413 alone waits ~30 s: 5 tries with the growing retry delay)");
        var forwarder4 = new MesForwarder(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, $"http://localhost:{port4}/", outbox4,
            new GatewayStats(), null, stuckLimit: withOthers ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(5)).RunAsync(stop4.Token);
        var feed4 = withOthers
            ? Task.Run(async () => { for (var i = 0; !stop4.IsCancellationRequested && i < 60; i++) { await QueueRow(outbox4, "OTHER", "PROCESS", 0, i); await Task.Delay(1000); } })
            : Task.CompletedTask;
        var big = Counts(dir4, "BIG");
        for (var i = 0; i < 60 && big.Sent < 2; i++) { await Task.Delay(1000); big = Counts(dir4, "BIG"); }
        stop4.Cancel(); try { await forwarder4; } catch { } try { await feed4; } catch { }
        mes4.Stop(); await outbox4.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Report($"SVI-98 413 row moves to failed {(withOthers ? "while others deliver" : "after the time limit (machine alone)")}",
            big.Failed == 1 && big.Sent == 2, $"BIG failed {big.Failed} (want 1), sent {big.Sent} (want 2), waiting {big.Waiting}");
    }
}

if (which is "all" or "95")
{
    // SVI-95: one failed write (file locked past the 30 s busy timeout) must not stop saving for good:
    // with yesterday's file attached, the next try used to fail on "database prev is already in use".
    var dir = Path.Combine(Path.GetTempPath(), "gw-95", Guid.NewGuid().ToString("N"));
    var first = new Outbox(dir);
    await QueueRow(first, "M1", "PROCESS");
    await first.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    File.Move(Path.Combine(dir, DayName(DateTime.Now)), Path.Combine(dir, DayName(DateTime.Now.AddDays(-1))));
    var outbox = new Outbox(dir);
    await QueueRow(outbox, "M1", "PROCESS"); // today's file made, yesterday's attached
    var lockFile = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, DayName(DateTime.Now))};Pooling=False");
    lockFile.Open();
    using (var begin = lockFile.CreateCommand()) { begin.CommandText = "BEGIN EXCLUSIVE;"; begin.ExecuteNonQuery(); }
    Console.WriteLine("      (SVI-95 waits ~35 s: the gateway's write must time out once)");
    var blocked = QueueRow(outbox, "M1", "PROCESS", 0, "after the lock");
    await Task.Delay(TimeSpan.FromSeconds(32)); // the writer's busy timeout (30 s) runs out: one failed write
    using (var end = lockFile.CreateCommand()) { end.CommandText = "ROLLBACK;"; end.ExecuteNonQuery(); }
    lockFile.Dispose();
    var savedAfter = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromSeconds(10))) == blocked;
    await outbox.DisposeAsync();
    Report("SVI-95 saving goes on after one failed write", savedAfter, savedAfter ? "saved on the retry" : "still not saved 10 s after the lock was gone");

    // SVI-95: a disposed outbox (gateway Restart) whose file stays locked stops its writer instead of
    // retrying forever next to the new one, and its "not written" line counts the batch in flight.
    var dir2 = Path.Combine(Path.GetTempPath(), "gw-95", Guid.NewGuid().ToString("N"));
    var old = new Outbox(dir2);
    await QueueRow(old, "M1", "PROCESS");
    var lock2 = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir2, DayName(DateTime.Now))};Pooling=False");
    lock2.Open();
    using (var begin = lock2.CreateCommand()) { begin.CommandText = "BEGIN EXCLUSIVE;"; begin.ExecuteNonQuery(); }
    Console.WriteLine("      (SVI-95 waits ~32 s more: the disposed writer's last try must time out)");
    _ = QueueRow(old, "M1", "PROCESS", 0, "never written");
    await Task.Delay(500); // the writer has taken it out of the queue
    var console = Console.Out; var log = new StringWriter();
    Console.SetOut(log);
    await old.DisposeAsync(); // gives up after 10 s
    Console.SetOut(console);
    var writer = (Task)typeof(Outbox).GetField("_writer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(old)!;
    var stopped = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(40))) == writer; // its try in progress runs out first
    using (var end = lock2.CreateCommand()) { end.CommandText = "ROLLBACK;"; end.ExecuteNonQuery(); }
    lock2.Dispose();
    await Task.Delay(1500);
    var left = Counts(dir2).Waiting;
    Report("SVI-95 disposed outbox stops retrying and counts the batch in flight",
        stopped && left == 1 && log.ToString().Contains(": 1 row(s) not written"),
        $"writer stopped: {stopped}; rows on disk {left} (want 1); line: {log.ToString().Trim()}");
}

if (which is "all" or "96")
{
    // SVI-96: two machines (or sources) using the same message_id are two messages: both reach MES, both acked.
    // data_source in another case (AOI/SPI drafts lower case, screw draft upper) is the same message.
    var (root, json) = TempConfig();
    var tcp = 19500 + Random.Shared.Next(300);
    var mesPort = 19900 + Random.Shared.Next(90);
    var mesBodies = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var mes96 = FakeMes(mesPort, body => { mesBodies.Enqueue(body); return 200; });
    File.WriteAllText(Path.Combine(root, "machine-config.json"),
        $$"""{"gatewayId":"T","listenAddress":"127.0.0.1","tcpPort":{{tcp}},"mesUrl":"http://localhost:{{mesPort}}/","unregisteredMachines":"shared","unmappedTags":"send"}""");
    // broker explicitly local and closed: the built-in default is a real broker
    File.WriteAllText(Path.Combine(root, "mqtt-config.json"),
        """{"brokerAddress":"127.0.0.1","brokerPort":1,"clientIdSuffix":"checks","reconnectCooldownSeconds":60,"area":"T","keepaliveIntervalSeconds":60,"motionDeadband":0.01,"maxPublishRateMs":50}""");
    DataFolder.Use(root);
    var registry = new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json);
    var host = new GatewayHost(registry, json, null, new MessageIdFilter());
    using var stop = new CancellationTokenSource();
    var run = host.RunAsync(stop.Token);
    for (var i = 0; i < 50 && host.State != "running"; i++) await Task.Delay(100);
    var replies = new List<string?>();
    using (var c = new System.Net.Sockets.TcpClient())
    {
        await c.ConnectAsync("127.0.0.1", tcp);
        var s = c.GetStream(); s.ReadTimeout = 5000;
        var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\n" }; var r = new StreamReader(s);
        foreach (var (machine, source, sn) in new[] { ("M1", "spi", "SN-A"), ("M2", "spi", "SN-B"), ("M1", "aoi", "SN-C"), ("M1", "SPI", "SN-A") })
        {
            await w.WriteLineAsync($$$"""{"message_id":"1001","machine_id":"{{{machine}}}","data_source":"{{{source}}}","data":{"product_sn":"{{{sn}}}"}}""");
            replies.Add(await r.ReadLineAsync());
        }
    }
    for (var i = 0; i < 100 && mesBodies.Count < 3; i++) await Task.Delay(100);
    await Task.Delay(1000); // a 4th record would arrive by now
    stop.Cancel(); try { await run; } catch { }
    mes96.Stop();
    var atMes = mesBodies.Select(b => System.Text.Json.JsonDocument.Parse(b).RootElement)
        .Select(e => (Id: e.GetProperty("event_id").GetString(), What: $"{e.GetProperty("machine_id").GetString()}/{e.GetProperty("data_source").GetString()}/{e.GetProperty("payload").GetProperty("product_serial_number").GetString()}"))
        .ToList();
    var what = atMes.Select(x => x.What).OrderBy(x => x).ToList();
    Report("SVI-96 same message_id from another machine / source reaches MES",
        what.SequenceEqual(new[] { "M1/aoi/SN-C", "M1/spi/SN-A", "M2/spi/SN-B" }) && atMes.Select(x => x.Id).Distinct().Count() == 3
        && replies.All(x => x == "{\"ack\":\"1001\"}"),
        $"MES got [{string.Join(", ", what)}] (want M1/aoi/SN-C, M1/spi/SN-A, M2/spi/SN-B; M1/SPI is a repeat), " +
        $"{atMes.Select(x => x.Id).Distinct().Count()} event_ids, replies: {string.Join(" ", replies)}");
}

if (which is "all" or "stuck")
{
    // Stuck-row limits as General settings: tries >= 1, minutes 1-10, 0 refused.
    var (_, json) = TempConfig();
    string? Check(string extra) => EdgeGateway.Admin.ConfigValidator.Validate("machine-config",
        $$"""{"listenAddress":"0.0.0.0","tcpPort":9000,"mesUrl":"http://localhost:5001/"{{extra}}}""", json, 8080);
    var cases = new (string Extra, bool Ok)[]
    {
        ("", true), (",\"mesStuckRowTries\":1", true), (",\"mesStuckRowTries\":1000", true), (",\"mesStuckRowTries\":0", false),
        (",\"mesStuckRowMinutes\":1", true), (",\"mesStuckRowMinutes\":10", true), (",\"mesStuckRowMinutes\":0", false), (",\"mesStuckRowMinutes\":11", false),
    };
    var wrong = cases.Where(c => (Check(c.Extra) is null) != c.Ok).Select(c => $"{c.Extra.TrimStart(',')} -> {Check(c.Extra) ?? "accepted"}").ToList();
    Report("settings: stuck-row limits checked on save", wrong.Count == 0, wrong.Count == 0 ? $"{cases.Length} cases as expected" : string.Join("; ", wrong));

    // The forwarder uses the tries value it is given: 2 tries, then failed (another machine delivering).
    var port = 19900 + Random.Shared.Next(90);
    var tries = 0;
    var mes = FakeMes(port, body => { if (!body.Contains("\"machine_id\":\"BIG\"")) return 200; Interlocked.Increment(ref tries); return 500; });
    var dir = Path.Combine(Path.GetTempPath(), "gw-97", Guid.NewGuid().ToString("N"));
    var outbox = new Outbox(dir);
    await QueueRow(outbox, "BIG", "PROCESS");
    await QueueRow(outbox, "OTHER", "PROCESS", 0, -1); // delivered in the first pass, after BIG's first 500
    using var stop = new CancellationTokenSource();
    var forwarder = new MesForwarder(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, $"http://localhost:{port}/", outbox,
        new GatewayStats(), null, TimeSpan.FromHours(1), stuckTries: 2).RunAsync(stop.Token);
    var feed = Task.Run(async () => { for (var i = 0; !stop.IsCancellationRequested && i < 30; i++) { await QueueRow(outbox, "OTHER", "PROCESS", 0, i); await Task.Delay(500); } });
    var big = Counts(dir, "BIG");
    for (var i = 0; i < 20 && big.Failed == 0; i++) { await Task.Delay(1000); big = Counts(dir, "BIG"); }
    stop.Cancel(); try { await forwarder; } catch { } try { await feed; } catch { }
    mes.Stop(); await outbox.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Report("settings: forwarder uses mesStuckRowTries", big.Failed == 1 && tries == 2, $"failed {big.Failed} (want 1) after {tries} tries (want 2)");
}

if (which is "all" or "mqtt")
{
    // MQTT broker login: a local test broker (this PC only) that wants username gw / password secret.
    var port = 19900 + Random.Shared.Next(90);
    var broker = new MQTTnet.MqttFactory().CreateMqttServer(new MQTTnet.Server.MqttServerOptionsBuilder()
        .WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(System.Net.IPAddress.Loopback).WithDefaultEndpointPort(port).Build());
    broker.ValidatingConnectionAsync += e =>
    {
        if (e.UserName != "gw" || e.Password != "secret")
            e.ReasonCode = MQTTnet.Protocol.MqttConnectReasonCode.BadUserNameOrPassword;
        return Task.CompletedTask;
    };
    await broker.StartAsync();

    // Twin connection (connects at start, keepalive).
    async Task<(bool Connected, string Error)> Twin(string password)
    {
        var twin = new MqttPublisherService(new MqttOptions { BrokerAddress = "127.0.0.1", BrokerPort = port, Username = "gw", Password = password }, $"checks-twin-{password}");
        try { await twin.ConnectAsync(); return (twin.IsConnected, ""); }
        catch (Exception ex) { return (false, MqttPublisherService.Why(ex)); }
        finally { try { await twin.DisconnectAsync(); } catch { } }
    }
    var good = await Twin("secret");
    var bad = await Twin("wrong");
    Report("mqtt: twin connection logs in with username / password", good.Connected, $"connected: {good.Connected} {good.Error}");
    Report("mqtt: a refused login says so", !bad.Connected && bad.Error.Contains("refused the login"), $"connected: {bad.Connected}; error: {bad.Error}");

    // Relay connection (connects when a line needs it): its failed attempt counts for the page's status.
    async Task<(bool Connected, bool Failed)> Relay(string password)
    {
        await using var relay = new MqttPublisher(new MqttConnectionConfig
            { BrokerAddress = "127.0.0.1", BrokerPort = port, ClientIdSuffix = $"checks-relay-{password}", Username = "gw", Password = password, ReconnectCooldownSeconds = 60 });
        var before = relay.ConnectFailed;
        relay.Enqueue("checks/IO/input", "{}");
        for (var i = 0; i < 50 && !relay.IsConnected && !relay.ConnectFailed; i++) await Task.Delay(100);
        return (relay.IsConnected, relay.ConnectFailed && !before);
    }
    var relayGood = await Relay("secret");
    var relayBad = await Relay("wrong");
    Report("mqtt: relay logs in; a refused login counts as failed", relayGood.Connected && !relayGood.Failed && !relayBad.Connected && relayBad.Failed,
        $"right login: connected {relayGood.Connected}, failed {relayGood.Failed}; wrong login: connected {relayBad.Connected}, failed {relayBad.Failed}");
    await broker.StopAsync(new MQTTnet.Server.MqttServerStopOptions());
    broker.Dispose();

    // The config page never gets the password back; saving the mask keeps the stored one.
    var file = Path.Combine(Path.GetTempPath(), $"gw-mqtt-{Guid.NewGuid():N}.json");
    File.WriteAllText(file, """{"brokerAddress":"b","password":"secret"}""");
    var shown = System.Text.Json.Nodes.JsonNode.Parse("""{"brokerAddress":"b","password":"secret"}""");
    EdgeGateway.Admin.AdminWebServer.MaskPassword(shown);
    var none = System.Text.Json.Nodes.JsonNode.Parse("""{"password":""}""");
    EdgeGateway.Admin.AdminWebServer.MaskPassword(none);
    string Saved(string sent) => System.Text.Json.Nodes.JsonNode.Parse(
        EdgeGateway.Admin.AdminWebServer.KeepStoredPassword($$"""{"brokerAddress":"b","password":"{{sent}}"}""", file))!["password"]!.GetValue<string>();
    var cases = new[]
    {
        ("page gets", shown!["password"]!.GetValue<string>(), EdgeGateway.Admin.AdminWebServer.PasswordMask),
        ("no password shows", none!["password"]!.GetValue<string>(), ""),
        ("save with mask keeps", Saved(EdgeGateway.Admin.AdminWebServer.PasswordMask), "secret"),
        ("save new replaces", Saved("new"), "new"),
        ("save empty removes", Saved(""), ""),
    };
    File.Delete(file);
    var wrong = cases.Where(c => c.Item2 != c.Item3).Select(c => $"{c.Item1}: {c.Item2} (want {c.Item3})").ToList();
    Report("mqtt: password never sent to the page, kept on save", wrong.Count == 0, wrong.Count == 0 ? $"{cases.Length} cases as expected" : string.Join("; ", wrong));
}

if (which is "all" or "namematch")
{
    // Name matching: which MES column a tag the point map doesn't know matches, and when it is automatic.
    var columns = new Dictionary<string, IReadOnlyList<string>>
    {
        ["PRODUCTION"] = new[] { "good_quantity", "ng_quantity", "station_id" },
        ["OPERATOR"] = new[] { "operator_id", "station_id" },
        ["ALARM"] = new[] { "alarm_code", "alarm_status" },
        ["PROCESS"] = new[] { "actual_value" },
    };
    var synonyms = NameMatcher.ParseSynonyms(new MachineConfig().NameMatchSynonyms);
    var filled = new List<PointMapEntry> { new() { Tag = "ALM", DataGroup = "ALARM", FieldName = "alarm_code" } };
    string Case(string tag)
    {
        var m = NameMatcher.Best(tag, columns, synonyms, filled)!;
        var auto = m.Conflict is null && m.Score >= 0.9;
        return $"{tag} -> {m.Group}/{m.Field} {m.Score:0.00} {(auto ? "auto" : "ask")}";
    }
    var want = new[]
    {
        "GOOD_QTY -> PRODUCTION/good_quantity 1.00 auto",   // synonym qty = quantity
        "goodQty -> PRODUCTION/good_quantity 1.00 auto",    // camelCase
        "NG_QTY -> PRODUCTION/ng_quantity 1.00 auto",
        "station_id -> PRODUCTION/station_id 1.00 ask",     // column in two groups
        "ALARM_CODE -> ALARM/alarm_code 1.00 ask",          // already filled by ALM
        "alarm_state -> ALARM/alarm_status 0.83 ask",       // below the threshold
    };
    var got = want.Select(w => Case(w[..w.IndexOf(' ')])).ToList();
    var wrongCases = got.Where((g, i) => g != want[i]).ToList();
    Report("name match: score, synonyms, ambiguous and filled columns", wrongCases.Count == 0,
        wrongCases.Count == 0 ? $"{want.Length} cases as expected" : string.Join("; ", wrongCases));

    // #27: whole words, rare ones weigh more. By letters alone "serial" is nearest material_id, and
    // "result_code" ties result / error_code ("code" is in three columns, "result" in one).
    var columns27 = new Dictionary<string, IReadOnlyList<string>>
    {
        ["TRACEABILITY"] = new[] { "product_serial_number", "material_id" },
        ["QUALITY"] = new[] { "result" },
        ["ALARM"] = new[] { "alarm_code", "error_code", "downtime_reason_code" },
    };
    var want27 = new[] { "serial -> product_serial_number ask", "result_code -> result ask", "test_result -> result ask" };
    var got27 = want27.Select(w => w[..w.IndexOf(' ')]).Select(tag =>
    {
        var m = NameMatcher.Best(tag, columns27, synonyms, new List<PointMapEntry>())!;
        return $"{tag} -> {m.Field} {(m.Conflict is null && m.Score >= 0.9 ? "auto" : "ask")}";
    }).ToList();
    Report("#27 name match: whole words, rare words count more", got27.SequenceEqual(want27), string.Join("; ", got27));

    // #25: a tag people mapped (or ignored) on other tabs is mapped the same way; tabs that disagree,
    // or a field filled here already, make it a suggestion; entries still marked auto don't count.
    static PointMapEntry E(string tag, string group, string field, double? auto = null) => new() { Tag = tag, DataGroup = group, FieldName = field, Auto = auto };
    var tabs = new List<IReadOnlyList<PointMapEntry>>
    {
        new[] { E("serial", "TRACEABILITY", "product_serial_number"), E("angle_deg", "IGNORED", "angle_deg"), E("torque", "PROCESS", "actual_value") },
        new[] { E("SERIAL", "TRACEABILITY", "product_serial_number"), E("torque", "QUALITY", "result") },
        new[] { E("serial", "PROCESS", "recipe_id", auto: 1) },
    };
    string Seen25(string tag, params PointMapEntry[] here) =>
        NameMatcher.Seen(tag, tabs, here) is { } m ? $"{m.Group}/{m.Field} seen {m.SeenOn} {(m.Conflict is null ? "auto" : "ask")}" : "none";
    var got25 = new[] { Seen25("serial"), Seen25("angle_deg"), Seen25("torque"), Seen25("serial", E("sn", "TRACEABILITY", "product_serial_number")), Seen25("tool") };
    var want25 = new[] { "TRACEABILITY/product_serial_number seen 2 auto", "IGNORED/angle_deg seen 1 auto", "PROCESS/actual_value seen 1 ask",
                         "TRACEABILITY/product_serial_number seen 2 ask", "none" };
    Report("#25 mapping seen on other tabs", got25.SequenceEqual(want25), string.Join("; ", got25));

    // #26: synonyms learned from mappings: cnt = quantity (two tags agree); tmp disagrees; val has one
    // example (the same tag on two tabs); pct comes only from an entry still marked auto.
    var mapped26 = new[]
    {
        E("GOOD_CNT", "PRODUCTION", "good_quantity"), E("NG_CNT", "PRODUCTION", "ng_quantity"),
        E("A_TMP", "PROCESS", "a_temperature"), E("B_TMP", "PROCESS", "b_timestamp"),
        E("X_VAL", "PROCESS", "x_value"), E("x_val", "PROCESS", "x_value"),
        E("Y_PCT", "PROCESS", "y_percent", auto: 1), E("Z_PCT", "PROCESS", "z_percent", auto: 1),
    };
    var learned = NameMatcher.LearnSynonyms(mapped26);
    var scrap = NameMatcher.Best("SCRAP_CNT", new Dictionary<string, IReadOnlyList<string>> { ["PRODUCTION"] = new[] { "scrap_quantity", "good_quantity" } },
        NameMatcher.WithLearned(synonyms, mapped26), new List<PointMapEntry>())!;
    // #28: the value as a hint: OK / NG -> a result column, a date-time -> a *_time column. Only reorders,
    // never auto ("judge" has no name match); a plain number gives no hint.
    var columns28 = new Dictionary<string, IReadOnlyList<string>>
    {
        ["QUALITY"] = new[] { "result", "test_timestamp" },
        ["PRODUCTION"] = new[] { "station_id", "cycle_end_time" },
    };
    string Hint28(string tag, string value)
    {
        var m = NameMatcher.Best(tag, columns28, synonyms, new List<PointMapEntry>(), value)!;
        return $"{tag}={value} -> {m.Field} {(m.Conflict is null && m.Score >= 0.9 ? "auto" : "ask")}";
    }
    var got28 = new[] { Hint28("judge", "OK"), Hint28("judge", "NG"), Hint28("t_end", "2026-10-29T10:01:56.850+07:00"), Hint28("judge", "12.5") };
    var ok28 = got28[0] == "judge=OK -> result ask" && got28[1] == "judge=NG -> result ask"
               && got28[2] == "t_end=2026-10-29T10:01:56.850+07:00 -> cycle_end_time ask" && !got28[3].Contains("-> result");
    Report("#28 value hints a column", ok28, string.Join("; ", got28));

    Report("#26 synonyms learned from mappings", string.Join(",", learned.Select(p => $"{p.Key}={p.Value}")) == "cnt=quantity" && scrap.Field == "scrap_quantity" && scrap.Score == 1,
        $"learned [{string.Join(", ", learned.Select(p => $"{p.Key}={p.Value}"))}] (want cnt=quantity); SCRAP_CNT -> {scrap.Field} {scrap.Score:0.00}");

    // Through the gateway: an unknown GOOD_QTY is mapped automatically (marked auto) and this very message
    // reaches MES with good_quantity; TORQUE_1 (no good match) stays unmapped.
    var (root, json) = TempConfig();
    File.WriteAllText(Path.Combine(root, "mes-schema.json"), """{"PRODUCTION":["good_quantity","ng_quantity"],"PROCESS":["actual_value"]}""");
    File.WriteAllText(Path.Combine(root, "point-map.json"), """[{"tag":"NG","dataGroup":"PRODUCTION","fieldName":"ng_quantity"}]""");
    var tcp = 19500 + Random.Shared.Next(300);
    var mesPort = 19900 + Random.Shared.Next(90);
    var bodies = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var mes = FakeMes(mesPort, body => { bodies.Enqueue(body); return 200; });
    File.WriteAllText(Path.Combine(root, "machine-config.json"),
        $$"""{"gatewayId":"T","listenAddress":"127.0.0.1","tcpPort":{{tcp}},"mesUrl":"http://localhost:{{mesPort}}/","unregisteredMachines":"shared","unmappedTags":"send"}""");
    File.WriteAllText(Path.Combine(root, "mqtt-config.json"),
        """{"brokerAddress":"127.0.0.1","brokerPort":1,"clientIdSuffix":"checks","reconnectCooldownSeconds":60,"area":"T","keepaliveIntervalSeconds":60,"motionDeadband":0.01,"maxPublishRateMs":50}""");
    DataFolder.Use(root);
    var registry = new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json);
    var host = new GatewayHost(registry, json, null, new MessageIdFilter());
    using var stop = new CancellationTokenSource();
    var run = host.RunAsync(stop.Token);
    for (var i = 0; i < 50 && host.State != "running"; i++) await Task.Delay(100);
    string? reply;
    using (var c = new System.Net.Sockets.TcpClient())
    {
        await c.ConnectAsync("127.0.0.1", tcp);
        var s = c.GetStream(); s.ReadTimeout = 5000;
        var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\n" };
        await w.WriteLineAsync("""{"message_id":"nm-1","machine_id":"M1","data":{"GOOD_QTY":5,"TORQUE_1":1.5}}""");
        reply = await new StreamReader(s).ReadLineAsync();
    }
    for (var i = 0; i < 50 && bodies.IsEmpty; i++) await Task.Delay(100);
    stop.Cancel(); try { await run; } catch { }
    mes.Stop();
    var map = System.Text.Json.JsonSerializer.Deserialize<List<PointMapEntry>>(File.ReadAllText(Path.Combine(root, "point-map.json")), json)!;
    var autoEntry = map.FirstOrDefault(p => p.Tag == "GOOD_QTY");
    var sentGood = bodies.Select(b => System.Text.Json.JsonDocument.Parse(b).RootElement.GetProperty("payload"))
        .Any(p => p.TryGetProperty("good_quantity", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number && v.GetDouble() == 5);
    Report("name match: unknown tag auto-mapped, marked, and sent in the same message",
        autoEntry is { DataGroup: "PRODUCTION", FieldName: "good_quantity", Auto: 1.0 } && sentGood && map.All(p => p.Tag != "TORQUE_1") && reply == "{\"ack\":\"nm-1\"}",
        $"point map: {string.Join(", ", map.Select(p => $"{p.Tag}->{p.FieldName}{(p.Auto is { } a ? $" (auto {a})" : "")}"))}; MES got good_quantity 5: {sentGood}; reply {reply}");
}

if (which is "all" or "autotab")
{
    // Similar machines: same name before the first "-", else the name without trailing digits.
    var families = new[] { ("NEXIM-08c4-aa", "nexim"), ("NEXIM-1", "nexim"), ("LINE01_NXT101", "line01_nxt"), ("NXT105", "nxt"), ("AOI", "aoi") };
    var wrongFamily = families.Where(f => MachineRegistry.Family(f.Item1) != f.Item2).Select(f => $"{f.Item1} -> {MachineRegistry.Family(f.Item1)}").ToList();
    Report("auto tab: similar-machine rule", wrongFamily.Count == 0, wrongFamily.Count == 0 ? $"{families.Length} names as expected" : string.Join("; ", wrongFamily));

    // A machine without a tab (hold mode) gets one on its first message, copied from a confirmed similar tab;
    // its message is processed. After 20 automatic tabs in an hour, new machines are held again.
    var (root, json) = TempConfig();
    var tcp = 19500 + Random.Shared.Next(300);
    var mesPort = 19900 + Random.Shared.Next(90);
    var bodies = new System.Collections.Concurrent.ConcurrentQueue<string>();
    using var mes = FakeMes(mesPort, body => { bodies.Enqueue(body); return 200; });
    File.WriteAllText(Path.Combine(root, "machine-config.json"),
        $$"""{"gatewayId":"T","listenAddress":"127.0.0.1","tcpPort":{{tcp}},"mesUrl":"http://localhost:{{mesPort}}/","unregisteredMachines":"hold","unmappedTags":"send"}""");
    File.WriteAllText(Path.Combine(root, "mqtt-config.json"),
        """{"brokerAddress":"127.0.0.1","brokerPort":1,"clientIdSuffix":"checks","reconnectCooldownSeconds":60,"area":"T","keepaliveIntervalSeconds":60,"motionDeadband":0.01,"maxPublishRateMs":50}""");
    DataFolder.Use(root);
    var registry = new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json);
    // the confirmed model tab: its own tag for machine_status, unlike the template's
    registry.Create("NEXIM-MODEL", null);
    File.WriteAllText(Path.Combine(registry.FolderFor("NEXIM-MODEL")!, "point-map.json"), """[{"tag":"STATE_CODE","dataGroup":"MACHINE_STATUS","fieldName":"machine_status"}]""");
    registry.Reload("NEXIM-MODEL");
    registry.CreateAuto("NEXIM-OLDAUTO", null); // an unchecked automatic one: not the copy source
    var host = new GatewayHost(registry, json, null, new MessageIdFilter());
    using var stop = new CancellationTokenSource();
    var run = host.RunAsync(stop.Token);
    for (var i = 0; i < 50 && host.State != "running"; i++) await Task.Delay(100);
    var replies = new List<string?>();
    using (var c = new System.Net.Sockets.TcpClient())
    {
        await c.ConnectAsync("127.0.0.1", tcp);
        var s = c.GetStream(); s.ReadTimeout = 3000;
        var w = new StreamWriter(s) { AutoFlush = true, NewLine = "\n" }; var r = new StreamReader(s);
        await w.WriteLineAsync("""{"message_id":"at-1","machine_id":"NEXIM-NEW","data":{"STATE_CODE":"RUN"}}""");
        replies.Add(await r.ReadLineAsync());
        for (var i = 1; i <= 20; i++) // 19 more fit in this hour's 20, the 20th of these is held
            await w.WriteLineAsync($$$"""{"message_id":"lim-{{{i}}}","machine_id":"LIM-{{{i}}}","data":{"x":1}}""");
        for (var i = 0; i < 19; i++) { try { replies.Add(await r.ReadLineAsync()); } catch (IOException) { break; } }
        await Task.Delay(500);
    }
    for (var i = 0; i < 50 && !bodies.Any(b => b.Contains("NEXIM-NEW")); i++) await Task.Delay(100);
    stop.Cancel(); try { await run; } catch { }
    mes.Stop();
    var copied = registry.AutoCopiedFrom("NEXIM-NEW");
    var newMap = File.ReadAllText(Path.Combine(registry.FolderFor("NEXIM-NEW") ?? root, "point-map.json"));
    var sentRun = bodies.Any(b => b.Contains("\"NEXIM-NEW\"") && b.Contains("\"machine_status\":\"RUN\""));
    Report("auto tab: new machine gets a copy of a confirmed similar tab, message processed",
        copied == "NEXIM-MODEL" && newMap.Contains("STATE_CODE") && sentRun && replies.FirstOrDefault() == "{\"ack\":\"at-1\"}",
        $"copied from {copied ?? "(no auto tab)"} (want NEXIM-MODEL); MES got machine_status RUN: {sentRun}; reply {replies.FirstOrDefault()}");
    var autoTabs = registry.Ids.Count(id => id.StartsWith("LIM-") && registry.IsAuto(id));
    var heldLast = registry.Held.ConnectionOf("LIM-20") is not null || registry.FolderFor("LIM-20") is null;
    Report("auto tab: at most 20 automatic tabs per hour, then held",
        autoTabs == 19 && registry.FolderFor("LIM-20") is null && heldLast,
        $"LIM tabs created: {autoTabs} (want 19, plus NEXIM-NEW = 20); LIM-20 has a tab: {registry.FolderFor("LIM-20") is not null}");
}

if (which is "all" or "msglog")
{
    // Message log: every line with its time and machine; searched by range and machine; old days deleted.
    var dir = Path.Combine(Path.GetTempPath(), "gw-msglog", Guid.NewGuid().ToString("N"));
    await using (var log = new MessageLog(dir))
    {
        foreach (var m in new[] { "A", "A", "A", "B" }) log.Record(m);
        await Task.Delay(1000);
        var now = DateTime.Now;
        var all = log.Count(now.AddMinutes(-1), now.AddMinutes(1), null);
        var onlyA = log.Count(now.AddMinutes(-1), now.AddMinutes(1), "A");
        var before = log.Count(now.AddMinutes(-30), now.AddMinutes(-10), null);
        Report("message log: counted per machine, searched by time and machine",
            all.Total == 4 && all.Machines.Single(x => x.Machine == "A").Count == 3 && all.Hours.Sum(x => x.Count) == 4 && onlyA.Total == 3 && before.Total == 0,
            $"all {all.Total} (A {all.Machines.FirstOrDefault(x => x.Machine == "A")?.Count}, per hour {all.Hours.Sum(x => x.Count)}); only A {onlyA.Total}; 10-30 min ago {before.Total}");

        string Day(int daysAgo) => Path.Combine(dir, $"traffic-{DateTime.Now.AddDays(-daysAgo).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.db");
        File.WriteAllText(Day(8), ""); File.WriteAllText(Day(6), "");
        log.Purge();
        Report("message log: days older than 7 are deleted", !File.Exists(Day(8)) && File.Exists(Day(6)), $"8 days old kept: {File.Exists(Day(8))}; 6 days old kept: {File.Exists(Day(6))}");
    }

    // Through the gateway: a MES line, a motion line (machine from its topic), a line without machine_id
    // and a broken line are all counted, each under its machine.
    var (root, json) = TempConfig();
    var tcp = 19500 + Random.Shared.Next(300);
    File.WriteAllText(Path.Combine(root, "machine-config.json"),
        $$"""{"gatewayId":"T","listenAddress":"127.0.0.1","tcpPort":{{tcp}},"mesUrl":"http://127.0.0.1:1/none","unregisteredMachines":"shared","unmappedTags":"send"}""");
    File.WriteAllText(Path.Combine(root, "mqtt-config.json"),
        """{"brokerAddress":"127.0.0.1","brokerPort":1,"clientIdSuffix":"checks","reconnectCooldownSeconds":60,"area":"T","keepaliveIntervalSeconds":60,"motionDeadband":0.01,"maxPublishRateMs":50}""");
    DataFolder.Use(root);
    var gwLog = new MessageLog(Path.Combine(root, "traffic"));
    var registry = new MachineRegistry(new GatewayConfigStore(root, json), new UnidentifiedTagTracker(), json);
    var host = new GatewayHost(registry, json, null, new MessageIdFilter(), gwLog);
    using var stop = new CancellationTokenSource();
    var run = host.RunAsync(stop.Token);
    for (var i = 0; i < 50 && host.State != "running"; i++) await Task.Delay(100);
    using (var c = new System.Net.Sockets.TcpClient())
    {
        await c.ConnectAsync("127.0.0.1", tcp);
        var w = new StreamWriter(c.GetStream()) { AutoFlush = true, NewLine = "\n" };
        await w.WriteLineAsync("""{"message_id":"ml-1","machine_id":"M1","data":{"product_sn":"P1"}}""");
        await w.WriteLineAsync("""{"topic":"svi/T/M2/motion/position","payload":{"timestamp":"2026-10-28 10:00:00.000","x":1}}""");
        await w.WriteLineAsync("""{"message_id":"ml-3","data":{"x":1}}""");
        await w.WriteLineAsync("""{"message_id":"ml-4", broken""");
        await Task.Delay(1500);
    }
    stop.Cancel(); try { await run; } catch { }
    var counted = gwLog.Count(DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1), null);
    await gwLog.DisposeAsync();
    var byMachine = string.Join(", ", counted.Machines.OrderBy(x => x.Machine).Select(x => $"{x.Machine} {x.Count}"));
    Report("message log: every kind of line counted under its machine",
        byMachine == "(no machine_id) 1, (unreadable) 1, M1 1, M2 1", $"counted: {byMachine}");
}

Console.WriteLine(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
return failed == 0 ? 0 : 1;

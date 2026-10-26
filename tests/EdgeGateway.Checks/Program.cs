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
    foreach (var (group, fields) in store.Current.Normalizer.Sort(raw))
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

Console.WriteLine(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
return failed == 0 ? 0 : 1;

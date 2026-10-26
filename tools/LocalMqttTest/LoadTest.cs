using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

// Load test: N simulated machines send to the gateway copy at a fixed total
// rate, and every second we measure what comes out the other side.
//
//   sent      lines the machines managed to write (drops when the gateway reads too slowly)
//   mqtt      messages the bench broker got from the gateway copy, + latency
//   queued    rows the gateway wrote into its outbox (read straight from outbox.db)
//   mes       envelopes the fake MES received, + latency
//   backlog   outbox rows not yet delivered
//   cpu / mem the gateway process
//
// Message mix per machine: 60% MES-style JSON (machine_id + 4 tags),
// 30% IO, 10% motion (world as JSON text, like the real machine).
public sealed class LoadTest
{
    private static readonly Regex ProblemWords = new(
        @"\b(fail(ed|s)?|cannot|can't|dropped|error|stopped|refused|exception|warning)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _mqttClientPrefix;
    private readonly string _outboxDir;

    private long _sent, _mqtt, _mes, _gatewayLines, _problems;
    private readonly Latency _mqttLatency = new(), _mesLatency = new();
    private readonly object _problemSync = new();
    private readonly List<string> _lastProblems = new();

    public LoadTest(string mqttClientPrefix, string outboxDir)
    {
        _mqttClientPrefix = mqttClientPrefix;
        _outboxDir = outboxDir;
    }

    // ---- hooks called by Program.cs ----

    public void OnMqtt(string clientId, ArraySegment<byte> payload)
    {
        if (!clientId.StartsWith(_mqttClientPrefix, StringComparison.Ordinal))
            return; // e.g. a live gateway on the same broker
        Interlocked.Increment(ref _mqtt);

        // payload carries "timestamp":"yyyy-MM-dd HH:mm:ss.fff" = when the machine sent it
        var text = Encoding.UTF8.GetString(payload);
        var at = text.IndexOf("\"timestamp\":\"", StringComparison.Ordinal);
        if (at >= 0 && at + 13 + 23 <= text.Length &&
            DateTime.TryParseExact(text.AsSpan(at + 13, 23), "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sentAt))
            _mqttLatency.Add((DateTime.Now - sentAt).TotalMilliseconds);
    }

    public void OnGatewayLine(string line)
    {
        Interlocked.Increment(ref _gatewayLines);
        if (!ProblemWords.IsMatch(line))
            return;
        Interlocked.Increment(ref _problems);
        lock (_problemSync)
        {
            _lastProblems.Add(line);
            if (_lastProblems.Count > 10)
                _lastProblems.RemoveAt(0);
        }
    }

    // ---- fake MES: answers 200 to everything, counts envelopes (single or array) ----

    public HttpListener StartFakeMes(int port)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleMes(ctx));
            }
        });
        return listener;
    }

    private void HandleMes(HttpListenerContext ctx)
    {
        try
        {
            using var doc = JsonDocument.Parse(ctx.Request.InputStream);
            var envelopes = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };

            Interlocked.Add(ref _mes, envelopes.Count);
            var now = DateTimeOffset.Now;
            foreach (var e in envelopes)
                if (e.TryGetProperty("publish_timestamp", out var p) && DateTimeOffset.TryParse(p.GetString(), out var built))
                    _mesLatency.Add((now - built).TotalMilliseconds);

            ctx.Response.StatusCode = 200;
        }
        catch
        {
            ctx.Response.StatusCode = 400;
        }
        finally
        {
            ctx.Response.Close();
        }
    }

    // ---- the run ----

    public async Task<string> RunAsync(int rate, int seconds, int machines, int drainSeconds, int tcpPort, Process gateway, string resultsDir)
    {
        var report = new StringBuilder();
        void Line(string text) { Console.WriteLine(text); report.AppendLine(text); }

        // Wait (max 15 s) until the gateway listens, so no machine starts on a refused connect.
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, tcpPort);
                break;
            }
            catch (SocketException)
            {
                await Task.Delay(500);
            }
        }

        Line($"load test {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {machines} machines, {rate} msg/s total, {seconds} s + {drainSeconds} s drain");
        Line("   t  |  sent/s |  mqtt/s  p95ms | queued/s |  mes/s  p95ms |  backlog | cpu% | mem MB | problems");

        using var stop = new CancellationTokenSource();
        var senders = Enumerable.Range(1, machines)
            .Select(i => SendMachineAsync(i, rate / (double)machines, tcpPort, stop.Token))
            .ToList();

        var cpuWas = gateway.TotalProcessorTime;
        long sentWas = 0, mqttWas = 0, mesWas = 0, rowsWas = 0, maxBacklog = 0, peakMem = 0;
        double cpuSum = 0;
        var totalSeconds = seconds + drainSeconds;

        for (var t = 1; t <= totalSeconds && !gateway.HasExited; t++)
        {
            await Task.Delay(1000);
            if (t == seconds + 1)
                stop.Cancel(); // machines stop; keep measuring while the gateway catches up

            gateway.Refresh();
            var cpu = (gateway.TotalProcessorTime - cpuWas).TotalMilliseconds / 10.0 / Environment.ProcessorCount;
            cpuWas = gateway.TotalProcessorTime;
            cpuSum += cpu;
            var mem = gateway.WorkingSet64 / 1024 / 1024;
            peakMem = Math.Max(peakMem, mem);

            var (rows, unsent) = ReadOutbox();
            maxBacklog = Math.Max(maxBacklog, unsent);

            var sent = Interlocked.Read(ref _sent);
            var mqtt = Interlocked.Read(ref _mqtt);
            var mes = Interlocked.Read(ref _mes);

            Line($"{t,4}{(t > seconds ? "d" : " ")} | {sent - sentWas,7} | {mqtt - mqttWas,7} {_mqttLatency.TakeP95(),6:0} | {rows - rowsWas,8} | {mes - mesWas,6} {_mesLatency.TakeP95(),6:0} | {unsent,8} | {cpu,4:0} | {mem,6} | {Interlocked.Read(ref _problems),8}");
            sentWas = sent; mqttWas = mqtt; mesWas = mes; rowsWas = rows;
        }

        stop.Cancel();
        try { await Task.WhenAll(senders); } catch { /* cancelled */ }

        var (totalRows, finalBacklog) = ReadOutbox();
        Line("");
        Line("summary");
        Line($"  sent by machines   {Interlocked.Read(ref _sent)} lines ({Interlocked.Read(ref _sent) / (double)seconds:0}/s of {rate}/s asked)");
        Line($"  mqtt published     {Interlocked.Read(ref _mqtt)}  latency p50 {_mqttLatency.P(50):0} ms, p95 {_mqttLatency.P(95):0} ms, max {_mqttLatency.P(100):0} ms");
        Line($"  outbox rows        {totalRows}  ({totalRows / (double)seconds:0}/s)");
        Line($"  mes delivered      {Interlocked.Read(ref _mes)}  latency p50 {_mesLatency.P(50):0} ms, p95 {_mesLatency.P(95):0} ms, max {_mesLatency.P(100):0} ms");
        Line($"  backlog            max {maxBacklog}, at end {finalBacklog}");
        Line($"  gateway            cpu avg {cpuSum / Math.Max(1, totalSeconds):0}% of all cores, peak mem {peakMem} MB, {Interlocked.Read(ref _gatewayLines)} console lines");
        Line($"  problems           {Interlocked.Read(ref _problems)}");
        lock (_problemSync)
            foreach (var p in _lastProblems)
                Line($"    {p}");

        Directory.CreateDirectory(resultsDir);
        var file = Path.Combine(resultsDir, $"load-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(file, report.ToString());
        return file;
    }

    // One machine: its own TCP connection, sending at its share of the rate.
    // If the gateway reads too slowly the machine skips ahead instead of
    // building an endless backlog, so "sent/s" shows what really got through.
    private async Task SendMachineAsync(int index, double perSecond, int tcpPort, CancellationToken stop)
    {
        var id = $"LOAD-M{index:00}";
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, tcpPort, stop);
            await using var stream = client.GetStream();

            var clock = Stopwatch.StartNew();
            long done = 0;
            var batch = new StringBuilder();

            while (!stop.IsCancellationRequested)
            {
                var due = (long)(clock.Elapsed.TotalSeconds * perSecond);
                if (due - done > perSecond)
                    done = due - (long)perSecond; // more than 1 s behind: skip, don't pile up

                var count = due - done;
                if (count <= 0)
                {
                    await Task.Delay(5, stop);
                    continue;
                }

                batch.Clear();
                for (long n = 0; n < count; n++)
                    batch.Append(MakeLine(id, done + n)).Append('\n');

                await stream.WriteAsync(Encoding.UTF8.GetBytes(batch.ToString()), stop);
                done += count;
                Interlocked.Add(ref _sent, count);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[load] {id} stopped: {ex.Message}");
        }
    }

    private static string MakeLine(string id, long seq)
    {
        var ts = DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var kind = seq % 10;

        if (kind < 6) // MES-style
        {
            var status = seq / 50 % 2 == 0 ? "RUN" : "IDLE";
            var torque = (12 + seq % 100 / 10.0).ToString(CultureInfo.InvariantCulture);
            return $"{{\"message_id\":\"{Guid.NewGuid()}\",\"machine_id\":\"{id}\",\"timestamp\":\"{ts}\",\"MACHINE_STATUS\":\"{status}\",\"GOOD_QTY\":{seq / 10},\"CYCLE_ACTIVE\":{seq / 30 % 2},\"TORQUE_1\":{torque}}}";
        }

        if (kind < 9) // IO
        {
            var inner = $"{{\"timestamp\":\"{ts}\",\"totalbits\":96,\"value\":\"{seq:X24}\"}}";
            return $"{{\"topic\":\"svi/TEST/{id}/IO/input\",\"payload\":{Quote(inner)}}}";
        }

        // motion, world as JSON text inside the payload like the real machine
        var x = (seq % 1000 / 10.0).ToString(CultureInfo.InvariantCulture);
        var world = $"{{\"X\":{x},\"Y\":0,\"U\":0,\"V\":0,\"W\":0}}";
        var motion = $"{{\"name\":\"XY_axis\",\"timestamp\":\"{ts}\",\"world\":{Quote(world)}}}";
        return $"{{\"topic\":\"svi/TEST/{id}/motion/position\",\"payload\":{Quote(motion)}}}";
    }

    private static string Quote(string json) => JsonSerializer.Serialize(json, Relaxed);

    // (rows ever written, rows not yet delivered) summed over the outbox files
    // (outbox.db or outbox-yyyy-MM-dd.db), read-only, no pooling so no file stays open.
    private (long Rows, long Unsent) ReadOutbox()
    {
        if (!Directory.Exists(_outboxDir))
            return (0, 0);

        long rows = 0, unsent = 0;
        foreach (var path in Directory.GetFiles(_outboxDir, "outbox*.db"))
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(MAX(id), 0), (SELECT COUNT(*) FROM outbox WHERE sent = 0) FROM outbox;";
                using var r = cmd.ExecuteReader();
                r.Read();
                rows += r.GetInt64(0);
                unsent += r.GetInt64(1);
            }
            catch
            {
                // file just created / table not there yet
            }
        }

        return (rows, unsent);
    }

    // Latency samples: per-second p95 for the live table, all samples for the summary.
    private sealed class Latency
    {
        private const int MaxSamples = 1_000_000;
        private readonly object _sync = new();
        private readonly List<double> _all = new();
        private readonly List<double> _window = new();

        public void Add(double ms)
        {
            lock (_sync)
            {
                if (_all.Count < MaxSamples) _all.Add(ms);
                _window.Add(ms);
            }
        }

        public double TakeP95()
        {
            lock (_sync)
            {
                var p = Percentile(_window, 95);
                _window.Clear();
                return p;
            }
        }

        public double P(int percentile)
        {
            lock (_sync)
                return Percentile(_all, percentile);
        }

        private static double Percentile(List<double> values, int percentile)
        {
            if (values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
        }
    }
}

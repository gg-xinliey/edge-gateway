using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

// Simulates N machines sending to the gateway, each on its own TCP connection:
//   * MES data every --data-s seconds (status, cycle, counts, torque, alarm, operator)
//   * motion/position every --motion-ms milliseconds (world/joint/pulse as JSON text)
//   * motion/status every --status-s seconds (0 = none, the default)
//   * IO every --io-s seconds on each topic in --io-topics (input and/or output),
//     --io-bits wide (default 16): input = a walking bit, output = a counter
// Any interval 0 = that kind is not sent.
// Machine ids are unique per run (SIM-HHmm-M01...), so the gateway starts
// fresh for them; --id NAME uses NAME instead (NAME-M01... with several machines).
// While sending it counts the MES envelopes the gateway should produce (one
// per data group per change) and prints them at the end, to compare with the outbox.
//
// Data lines behave like the data team's adapter: each is kept until the
// gateway answers ({"ack"} / {"nack"}); after a reconnect the unanswered ones
// are sent again first, in order. When the time is up it stops making new
// lines and waits (max --drain-s, default 60) until every data line is answered.
// Every data line goes to run-data.jsonl (with acked yes/no) for the E2E check.
//
//   dotnet run -- --machines 3 --minutes 2 --motion-ms 20 --data-s 3 --io-s 9
//   dotnet run -- --machines 1 --id MQTT-TEST-01 --minutes 3 --data-s 0 --motion-ms 20 --status-s 1 --io-s 1 --io-bits 8 --io-topics input,output
//   dotnet run -- --check     compare run-data.jsonl with what the fake MES stored (MES_EMULATOR\received.jsonl)

if (args.Contains("--check"))
    return E2ECheck.Run(args);

var opt = Options.Parse(args);
var run = DateTime.Now.ToString("HHmm");
var started = DateTime.Now;
string Every(double value, string unit) => value > 0 ? $"every {value} {unit}" : "off";
Console.WriteLine($"machine simulator: {opt.Machines} machines -> {opt.Host}:{opt.Port}, " +
                  (opt.Minutes > 0 ? $"{opt.Minutes} min, " : "until closed (Ctrl+C prints the summary), ") +
                  $"data {Every(opt.DataSeconds, "s")}, motion/position {Every(opt.MotionMs, "ms")}, motion/status {Every(opt.StatusSeconds, "s")}, " +
                  $"IO {string.Join("+", opt.IoTopics)} {opt.IoBits} bits {Every(opt.IoSeconds, "s")}");

string MachineId(int n) => opt.Id is null ? $"SIM-{run}-M{n:00}" : opt.Machines == 1 ? opt.Id : $"{opt.Id}-M{n:00}";
var machines = Enumerable.Range(1, opt.Machines).Select(n => new Machine(MachineId(n), n, opt)).ToList();

// --preview: print the first lines of each kind for the first machine, send nothing.
if (args.Contains("--preview"))
{
    foreach (var line in machines[0].Preview())
        Console.WriteLine(line);
    return 0;
}
// --minutes 0: run until Ctrl+C (or the window is closed, which skips the summary).
// stop = no new lines; quit = also stop waiting for answers (drain over, or a second Ctrl+C).
using var stop = opt.Minutes > 0 ? new CancellationTokenSource(TimeSpan.FromMinutes(opt.Minutes)) : new CancellationTokenSource();
using var quit = new CancellationTokenSource();
stop.Token.Register(() =>
{
    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  no new lines; waiting up to {opt.DrainSeconds} s for the gateway to answer every data line");
    quit.CancelAfter(TimeSpan.FromSeconds(opt.DrainSeconds));
});
Console.CancelKeyPress += (_, e) => { e.Cancel = true; if (stop.IsCancellationRequested) quit.Cancel(); else stop.Cancel(); };
var tasks = machines.Select(m => m.RunAsync(stop.Token, quit.Token)).ToList();

// one progress line every 10 s
while (!Task.WhenAll(tasks).IsCompleted)
{
    await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(10_000));
    var sec = (DateTime.Now - started).TotalSeconds;
    Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {sec,4:0} s  " +
        string.Join("   ", machines.Select(m => $"{m.Id[^3..]} data {m.DataSent} (ack {m.Acked}, waiting {m.Pending}) motion {m.MotionSent} status {m.StatusSent} io {m.IoSent}")));
}

// Summary on screen and in last-run.txt (next to the project) for comparing with the outbox.
var summary = new StringBuilder();
void Out(string line) { Console.WriteLine(line); summary.AppendLine(line); }

var ranSeconds = Math.Max(1, (DateTime.Now - started).TotalSeconds);
Out("");
Out($"done {DateTime.Now:HH:mm:ss} after {ranSeconds:0} s. Sent:");
foreach (var m in machines)
    Out($"  {m.Id}  data {m.DataSent}, motion {m.MotionSent} ({m.MotionSent / ranSeconds:0.0}/s), status {m.StatusSent}, io {m.IoSent}" +
        (m.Reconnects > 0 ? $"   reconnects {m.Reconnects}, data lines sent again {m.Resent}, lines in a failed write {m.LinesLost} (data ones kept and sent again), skipped while away {m.Skipped}" : ""));
Out($"  lines sent: {machines.Sum(m => m.DataSent + m.MotionSent + m.StatusSent + m.IoSent)}");
Out($"  gateway replies to data lines: ack {machines.Sum(m => m.Acked)}, nack {machines.Sum(m => m.Nacked)}, never answered {machines.Sum(m => m.Pending)} " +
    $"(of {machines.Sum(m => m.DataSent)} data lines; IO/motion get none)");
Out($"  MQTT expected: {machines.Sum(m => m.MotionSent + m.StatusSent + m.IoSent)} (motion + status + io)");
Out("");
Out("MES envelopes expected (one per data group per change):");
foreach (var m in machines)
    Out($"  {m.Id}  " + string.Join("  ", m.Expected.OrderBy(e => e.Key).Select(e => $"{e.Key} {e.Value}")) +
        $"  = {m.Expected.Values.Sum()}");
Out($"  total {machines.Sum(m => m.Expected.Values.Sum())}");
Out($"since (for OutboxDump): {started.ToUniversalTime():o}");
var projectDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
File.WriteAllText(Path.Combine(projectDir, "last-run.txt"), summary.ToString());

// Every data line, in the order made, with whether the gateway acknowledged it (for --check).
using (var runData = new StreamWriter(Path.Combine(projectDir, "run-data.jsonl")))
    foreach (var m in machines)
        foreach (var (messageId, line) in m.DataLines)
            runData.WriteLine($"{{\"acked\":{(m.IsAcked(messageId) ? "true" : "false")},\"line\":{line}}}");
Out($"data lines for --check: run-data.jsonl ({machines.Sum(m => m.DataLines.Count)})");
return 0;

sealed class Machine
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly int _index;
    private readonly Options _opt;
    private readonly Dictionary<string, string> _last = new();

    public Machine(string id, int index, Options opt) { Id = id; _index = index; _opt = opt; }

    public string Id { get; }
    public int DataSent, MotionSent, StatusSent;  // lines generated and written
    private int _ioTicks;                         // one IO line per topic per tick
    public int IoSent => _ioTicks * _opt.IoTopics.Length;
    public int Reconnects, LinesLost, Skipped;    // lost = IO/motion write failed; skipped = due while disconnected
    public int Acked, Nacked;                     // gateway replies {"ack":id} / {"nack":id,...} to data lines
    public int Resent;                            // data lines sent again after a reconnect (not answered before)

    // Data lines: all of them in order (for run-data.jsonl), the unanswered ones, the answered ones.
    public List<(string MessageId, string Line)> DataLines { get; } = new();
    private readonly SortedDictionary<long, (string MessageId, string Line)> _pending = new();
    private readonly Dictionary<string, long> _pendingSeq = new();
    private readonly HashSet<string> _acked = new();
    private readonly object _sync = new();
    public int Pending { get { lock (_sync) return _pending.Count; } }
    public bool IsAcked(string messageId) { lock (_sync) return _acked.Contains(messageId); }

    // Reads the gateway's replies (one JSON line each): an answered data line
    // is no longer kept. Also keeps the socket drained, so replies never pile up unread.
    private async Task ReadRepliesAsync(Stream stream, CancellationToken quit)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(quit)) != null)
            {
                string? id = null;
                var ack = false;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("ack", out var a)) { id = a.ToString(); ack = true; }
                    else if (doc.RootElement.TryGetProperty("nack", out var n)) id = n.ToString();
                }
                catch (JsonException) { continue; }
                if (id is null) continue;

                lock (_sync)
                {
                    if (!_pendingSeq.Remove(id, out var seq)) continue; // a repeat answer
                    _pending.Remove(seq);
                    if (ack) { _acked.Add(id); Acked++; } else Nacked++;
                }
            }
        }
        catch (Exception) { /* connection closed or stopping */ }
    }
    public Dictionary<string, int> Expected { get; } = new();

    private long _seq;
    private string KeepUntilAnswered(string messageId, string line)
    {
        lock (_sync)
        {
            DataLines.Add((messageId, line));
            _pending[++_seq] = (messageId, line);
            _pendingSeq[messageId] = _seq;
        }
        return line;
    }

    // Like a real machine: one connection; if it drops (gateway restart), reconnect
    // every 1 s and carry on. IO/motion lines that fell due while disconnected are
    // skipped, not sent in a burst afterwards; data lines not answered yet are sent
    // again first, in order. stop = no new lines; quit = stop waiting for answers.
    public async Task RunAsync(CancellationToken stop, CancellationToken quit)
    {
        var clock = Stopwatch.StartNew();
        var batch = new StringBuilder();

        while (!quit.IsCancellationRequested && !(stop.IsCancellationRequested && Pending == 0))
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(_opt.Host, _opt.Port, quit);
                await using var stream = client.GetStream();
                _ = ReadRepliesAsync(stream, quit);

                if (Reconnects > 0)
                {
                    SkipToNow(clock.Elapsed.TotalMilliseconds);
                    List<string> again;
                    lock (_sync) again = _pending.Values.Select(p => p.Line).ToList();
                    if (again.Count > 0)
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(string.Join("\n", again) + "\n"), quit);
                        Resent += again.Count;
                    }
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {Id} reconnected, {again.Count} unanswered data line(s) sent again");
                }

                while (!quit.IsCancellationRequested)
                {
                    if (stop.IsCancellationRequested)
                    {
                        if (Pending == 0) break; // everything answered
                        await Task.Delay(50, quit);
                        continue;
                    }

                    var t = clock.Elapsed.TotalMilliseconds;
                    batch.Clear();
                    var lines = 0;

                    // interval 0 = off (also keeps t / 0 from looping forever)
                    while (_opt.DataSeconds > 0 && DataSent <= t / (_opt.DataSeconds * 1000.0)) { batch.Append(DataLine(DataSent)).Append('\n'); DataSent++; lines++; }
                    while (_opt.IoSeconds > 0 && _ioTicks <= t / (_opt.IoSeconds * 1000.0))
                    {
                        foreach (var topic in _opt.IoTopics) { batch.Append(IoLine(_ioTicks, topic)).Append('\n'); lines++; }
                        _ioTicks++;
                    }
                    while (_opt.MotionMs > 0 && MotionSent <= t / _opt.MotionMs) { batch.Append(MotionLine(MotionSent)).Append('\n'); MotionSent++; lines++; }
                    while (_opt.StatusSeconds > 0 && StatusSent <= t / (_opt.StatusSeconds * 1000.0)) { batch.Append(StatusLine(StatusSent)).Append('\n'); StatusSent++; lines++; }

                    if (batch.Length > 0)
                    {
                        try { await stream.WriteAsync(Encoding.UTF8.GetBytes(batch.ToString()), quit); }
                        catch (Exception) when (!quit.IsCancellationRequested) { LinesLost += lines; throw; } // data lines among them stay kept
                    }
                    try { await Task.Delay(5, stop); } catch (OperationCanceledException) when (!quit.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (quit.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Reconnects++;
                // The gateway keeps its last values over a restart (saved with the outbox), so "expected" carries on too.
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {Id} connection lost ({ex.Message}), reconnecting");
                try { await Task.Delay(1000, quit); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public IEnumerable<string> Preview()
    {
        for (var i = 0; i < 3; i++)
        {
            if (_opt.DataSeconds > 0) yield return DataLine(i);
            if (_opt.IoSeconds > 0) foreach (var topic in _opt.IoTopics) yield return IoLine(i, topic);
            if (_opt.MotionMs > 0) yield return MotionLine(i);
            if (_opt.StatusSeconds > 0) yield return StatusLine(i);
        }
    }

    // After a reconnect: what fell due while away is not sent.
    private void SkipToNow(double t)
    {
        int Due(double every) => every > 0 ? (int)Math.Floor(t / every) + 1 : 0;
        var data = Due(_opt.DataSeconds * 1000.0); var io = Due(_opt.IoSeconds * 1000.0); var motion = Due(_opt.MotionMs); var status = Due(_opt.StatusSeconds * 1000.0);
        Skipped += Math.Max(0, data - DataSent) + Math.Max(0, io - _ioTicks) * _opt.IoTopics.Length + Math.Max(0, motion - MotionSent) + Math.Max(0, status - StatusSent);
        DataSent = Math.Max(DataSent, data - 1); _ioTicks = Math.Max(_ioTicks, io - 1); MotionSent = Math.Max(MotionSent, motion - 1); StatusSent = Math.Max(StatusSent, status - 1);
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Quote(string json) => JsonSerializer.Serialize(json, Relaxed);

    // Same pattern as the manual cases, shifted per machine so they differ.
    private string DataLine(int i)
    {
        var k = (i + _index * 5) % 20;
        var status = k < 7 ? "RUN" : k < 13 ? "IDLE" : "RUN";
        var cycle = (i + _index) % 4 < 2 ? 1 : 0;
        var good = 1000 * _index + (i + 2) / 4;
        var ng = k >= 10 ? 4 : 3;
        var torque = Num(12.2 + k * 0.02 + _index * 0.1);
        var alarm = k is 15 or 16 ? 1 : 0;
        var code = alarm == 1 ? "E10" + _index : "NONE";
        var (opId, opName, login) = k < 10 ? (44345, "fon", "2026-10-16T08:00:00") : (50012, "nok", "2026-10-16T10:40:00");

        // What the gateway should send: one envelope per group whose values changed.
        Count("MACHINE_STATUS", status);
        Count("PRODUCTION", $"{cycle}|{good}|{ng}|xxxxx{_index}|20.34|station{_index}");
        Count("PROCESS", torque);
        Count("ALARM", $"{alarm}|{code}");
        Count("OPERATOR", $"{opId}|{opName}|{login}");

        // The data team's format: message details at the top, the tags in "data".
        var messageId = Guid.NewGuid().ToString();
        return KeepUntilAnswered(messageId,
               $"{{\"message_id\":\"{messageId}\",\"machine_id\":\"{Id}\",\"timestamp\":\"{Now()}\",\"data\":{{\"MACHINE_STATUS\":\"{status}\",\"CYCLE_ACTIVE\":{cycle}," +
               $"\"GOOD_QTY\":{good},\"NG_QTY\":{ng},\"TORQUE_1\":{torque},\"ALARM_STATUS\":{alarm},\"ALARM_CODE\":\"{code}\"," +
               $"\"work_order_no\":\"xxxxx{_index}\",\"cycle_time\":20.34,\"station_id\":\"station{_index}\"," +
               $"\"operator_id\":{opId},\"operator_name\":\"{opName}\",\"login_time\":\"{login}\"}}}}");
    }

    private void Count(string group, string values)
    {
        if (_last.TryGetValue(group, out var last) && last == values)
            return;
        _last[group] = values;
        Expected[group] = Expected.GetValueOrDefault(group) + 1;
    }

    // input: one bit walks along (01, 02, 04 ... 80 for 8 bits); output: counts up.
    private string IoLine(int i, string topic)
    {
        var bits = _opt.IoBits;
        var mask = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
        var value = topic == "input" ? 1UL << (i % bits) : ((ulong)i + (ulong)_index * 16) & mask;
        var inner = $"{{\"timestamp\":\"{Now()}\",\"totalbits\":{bits},\"value\":\"{value.ToString("X" + bits / 4, CultureInfo.InvariantCulture)}\"}}";
        return $"{{\"topic\":\"svi/TEST/{Id}/IO/{topic}\",\"payload\":{Quote(inner)}}}";
    }

    // RUN for 10 ticks, IDLE for 5, then again.
    private string StatusLine(int i)
    {
        var status = i % 15 < 10 ? "RUN" : "IDLE";
        var inner = $"{{\"name\":\"XY_Axis\",\"timestamp\":\"{Now()}\",\"status\":\"{status}\",\"alarm\":0}}";
        return $"{{\"topic\":\"svi/TEST/{Id}/motion/status\",\"payload\":{Quote(inner)}}}";
    }

    private string MotionLine(int i)
    {
        var t = i * _opt.MotionMs / 1000.0;
        var x = Num(100 * Math.Sin(t / 2 + _index));
        var y = Num(50 * Math.Cos(t / 2 + _index));
        var world = $"{{\"X\":{x},\"Y\":{y},\"Z\":0,\"U\":0,\"V\":0,\"W\":0}}";
        var joint = $"{{\"J1\":{x},\"J2\":{y},\"J3\":0,\"J4\":0,\"J5\":0,\"J6\":0}}";
        var pulse = $"{{\"X\":{Num(Math.Round(100 * Math.Sin(t / 2 + _index) * 10))},\"Y\":{Num(Math.Round(50 * Math.Cos(t / 2 + _index) * 10))},\"Z\":0,\"U\":0,\"V\":0,\"W\":0}}";
        var inner = $"{{\"name\":\"XY_Axis\",\"timestamp\":\"{Now()}\",\"world\":{Quote(world)},\"joint\":{Quote(joint)},\"pulse\":{Quote(pulse)}}}";
        return $"{{\"topic\":\"svi/TEST/{Id}/motion/position\",\"payload\":{Quote(inner)}}}";
    }
}

sealed record Options(int Machines, double Minutes, int MotionMs, double DataSeconds, double IoSeconds, string Host, int Port,
    double StatusSeconds, int IoBits, string[] IoTopics, string? Id, int DrainSeconds)
{
    public static Options Parse(string[] args)
    {
        string Get(string name, string fallback)
        {
            var at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback;
        }

        var inv = CultureInfo.InvariantCulture;
        var ioBits = int.Parse(Get("--io-bits", "16"), inv);
        if (ioBits is < 4 or > 64 || ioBits % 4 != 0)
            throw new ArgumentException("--io-bits must be 4, 8, 12 ... 64");
        var ioTopics = Get("--io-topics", "input").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ioTopics.Length == 0 || ioTopics.Any(t => t is not ("input" or "output")))
            throw new ArgumentException("--io-topics: input, output or input,output");

        return new Options(
            int.Parse(Get("--machines", "3"), inv),
            double.Parse(Get("--minutes", "2"), inv),
            int.Parse(Get("--motion-ms", "20"), inv),
            double.Parse(Get("--data-s", "3"), inv),
            double.Parse(Get("--io-s", "9"), inv),
            Get("--host", "127.0.0.1"),
            int.Parse(Get("--port", "9000"), inv),
            double.Parse(Get("--status-s", "0"), inv),
            ioBits,
            ioTopics,
            Array.IndexOf(args, "--id") >= 0 ? Get("--id", "") : null,
            int.Parse(Get("--drain-s", "60"), inv));
    }
}

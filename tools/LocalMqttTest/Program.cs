using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MQTTnet;
using MQTTnet.Server;

// Offline test bench for the Edge Gateway. Nothing here talks to the real broker.
//
//   1. Starts a private MQTT broker on 127.0.0.1:1883 and prints every message it gets.
//   2. Builds the gateway (Debug), copies the build into .\gateway-run and points
//      that copy at 127.0.0.1. The real Config is never touched.
//   3. Starts the gateway copy and lets you fire test lines at it.
//
// The copy uses TCP port 19000, config page 15100 (not 9000 / 5100) and MQTT
// client ids with suffix "EdgeGateway-bench", so it can run on the same PC and
// the same broker as a live gateway without fighting over ports or client ids.
//
// dotnet run              -> interactive menu
// dotnet run -- --auto    -> sends every sample once, then stops
// dotnet run -- --hold 10 -> no menu; keeps broker + gateway up for 10 minutes
//                            (for testing the config page), then stops
// dotnet run -- --broker  -> only the local broker on 127.0.0.1:1883, prints every message
// dotnet run -- --load 5000 60 50
//                         -> load test: 50 machines, 5000 msg/s total, 60 s
//                            (+10 s drain), fake MES on :15000, results in .\load-results

const string LocalBroker = "127.0.0.1";
const int BrokerPort = 1883;
const int GatewayTcpPort = 19000;
const int AdminPort = 15100;
const int FakeMesPort = 15000;
const string ClientIdSuffix = "EdgeGateway-bench";

var benchDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var gatewayProjectDir = Path.GetFullPath(Path.Combine(benchDir, "..", "..")); // tools\LocalMqttTest -> the gateway project
var gatewayBuildDir = Path.Combine(gatewayProjectDir, "bin", "Debug", "net8.0");
var runDir = Path.Combine(benchDir, "gateway-run");

// --load [rate] [seconds] [machines]
var loadArg = Array.IndexOf(args, "--load");
int LoadParam(int offset, int fallback) =>
    loadArg + offset < args.Length && int.TryParse(args[loadArg + offset], out var v) ? v : fallback;
var load = loadArg >= 0
    ? new LoadTest($"{Environment.MachineName}_{ClientIdSuffix}", Path.Combine(runDir, "outbox"))
    : null;

// ---- 1. local broker ----
var server = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder()
    .WithDefaultEndpoint()
    .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
    .WithDefaultEndpointPort(BrokerPort)
    .Build());

server.ClientConnectedAsync += e =>
{
    Write(ConsoleColor.DarkCyan, $"[broker] client connected: {e.ClientId}");
    return Task.CompletedTask;
};
var printed = new Dictionary<string, (DateTime Last, long Skipped)>();
server.InterceptingPublishAsync += e =>
{
    if (load is not null)
    {
        load.OnMqtt(e.ClientId, e.ApplicationMessage.PayloadSegment); // count only, no printing at 1000s/s
        return Task.CompletedTask;
    }

    // Fast topics (motion at 50/s per machine) would scroll unreadably: print
    // each topic at most once a second, with how many arrived in between.
    var topic = e.ApplicationMessage.Topic;
    var now = DateTime.UtcNow;
    long skipped;
    lock (printed)
    {
        var (last, count) = printed.GetValueOrDefault(topic, (DateTime.MinValue, 0L));
        if (now - last < TimeSpan.FromSeconds(1))
        {
            printed[topic] = (last, count + 1);
            return Task.CompletedTask;
        }
        skipped = count;
        printed[topic] = (now, 0L);
    }

    var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
    Write(ConsoleColor.Green, $"[broker] {topic} = {payload}{(skipped > 0 ? $"   (+{skipped} more in the last second)" : "")}");
    return Task.CompletedTask;
};

await server.StartAsync();
Write(ConsoleColor.DarkCyan, $"[broker] listening on {LocalBroker}:{BrokerPort} (local only)");

// --broker: only the local broker (prints every message), for testing the real gateway.
if (args.Contains("--broker"))
{
    Write(ConsoleColor.Yellow, "[bench] broker only — Ctrl+C to stop");
    var stopBroker = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopBroker.TrySetResult(); };
    await stopBroker.Task;
    await server.StopAsync();
    return;
}

// ---- 2. build + copy + point at the local broker ----
Write(ConsoleColor.DarkGray, "[bench] building gateway (Debug)...");
if (Run("dotnet", $"build \"{Path.Combine(gatewayProjectDir, "EdgeGateway.csproj")}\" -c Debug -nologo -v q") != 0)
{
    Write(ConsoleColor.Red, "[bench] gateway build failed - fix it and run again");
    await server.StopAsync();
    return;
}

CopyDirectory(gatewayBuildDir, runDir);

var mqttConfigPath = Path.Combine(runDir, "Config", "mqtt-config.json");
var mqttConfig = JsonNode.Parse(File.ReadAllText(mqttConfigPath))!.AsObject();
mqttConfig["brokerAddress"] = LocalBroker;
mqttConfig["brokerPort"] = BrokerPort;
// Own client ids, so a live gateway on the same broker is not kicked off (and does not kick us).
mqttConfig["clientIdSuffix"] = ClientIdSuffix;
File.WriteAllText(mqttConfigPath, mqttConfig.ToJsonString(new() { WriteIndented = true }));

var machineConfigPath = Path.Combine(runDir, "Config", "machine-config.json");
var machineConfig = File.Exists(machineConfigPath)
    ? JsonNode.Parse(File.ReadAllText(machineConfigPath))!.AsObject()
    : new JsonObject();
machineConfig["tcpPort"] = GatewayTcpPort;
// The bench's simulated machines have no tabs: use the shared mapping instead of holding them.
machineConfig["unregisteredMachines"] = "shared";
if (load is not null)
    machineConfig["mesUrl"] = $"http://localhost:{FakeMesPort}/api/v1/machine-events";
File.WriteAllText(machineConfigPath, machineConfig.ToJsonString(new() { WriteIndented = true }));

HttpListener? fakeMes = null;
if (load is not null)
{
    // Every load run starts from an empty outbox (this is the bench copy's, never the real one).
    var outboxDir = Path.Combine(runDir, "outbox");
    if (Directory.Exists(outboxDir))
        Directory.Delete(outboxDir, recursive: true);
    fakeMes = load.StartFakeMes(FakeMesPort);
    Write(ConsoleColor.DarkGray, $"[bench] fake MES on http://localhost:{FakeMesPort}/ (answers 200, counts envelopes)");
}

// Refuse to start unless the copy really points at this PC and its own port.
var check = JsonNode.Parse(File.ReadAllText(mqttConfigPath))!;
var checkMachine = JsonNode.Parse(File.ReadAllText(machineConfigPath))!;
if ((string?)check["brokerAddress"] != LocalBroker || (int?)checkMachine["tcpPort"] != GatewayTcpPort)
{
    Write(ConsoleColor.Red, "[bench] could not point the gateway copy at 127.0.0.1 / its own port - not starting it");
    await server.StopAsync();
    return;
}
Write(ConsoleColor.DarkGray, $"[bench] gateway copy in {runDir} -> broker {LocalBroker}:{BrokerPort}, tcp {GatewayTcpPort}, page {AdminPort}");

// ---- 3. start the gateway copy ----
var gateway = Process.Start(new ProcessStartInfo(Path.Combine(runDir, "EdgeGateway.exe"), $"--admin-port {AdminPort}")
{
    WorkingDirectory = runDir,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true
})!;
gateway.OutputDataReceived += (_, e) =>
{
    if (e.Data is null) return;
    if (load is not null) load.OnGatewayLine(e.Data);
    else Write(ConsoleColor.Gray, $"  [gateway] {e.Data}");
};
gateway.ErrorDataReceived += (_, e) =>
{
    if (e.Data is null) return;
    if (load is not null) load.OnGatewayLine(e.Data);
    else Write(ConsoleColor.Red, $"  [gateway] {e.Data}");
};
gateway.BeginOutputReadLine();
gateway.BeginErrorReadLine();

Console.CancelKeyPress += (_, _) => StopGateway();

var samples = new (string Label, Func<string> Line)[]
{
    ("IO/input  (your real line)    -> publish",  () => Wrap("svi/TEST/ASSY-L01-FCT-01/IO/input",  $$"""{"timestamp":"{{Now()}}","totalbits":96,"value":"E77B300F8F0DF02A671D5819"}""")),
    ("IO/output                     -> publish",  () => Wrap("svi/TEST/ASSY-L01-FCT-01/IO/output", $$"""{"timestamp":"{{Now()}}","totalbits":80,"value":"00000000000000000000"}""")),
    ("motion/position               -> publish",  () => Wrap("svi/TEST/ASSY-L01-FCT-01/motion/position", $$"""{"timestamp":"{{Now()}}","axis":"X","world":12.5}""")),
    ("io/INPUT (wrong case)         -> publish as IO/input", () => Wrap("svi/TEST/ASSY-L01-FCT-01/io/INPUT", $$"""{"timestamp":"{{Now()}}","totalbits":8,"value":"FF"}""")),
    ("IO/foo (wrong ending)         -> dropped",  () => Wrap("svi/TEST/ASSY-L01-FCT-01/IO/foo", $$"""{"timestamp":"{{Now()}}","totalbits":8,"value":"FF"}""")),
    ("MES line (not twin)           -> nothing on broker", () => $$"""{"message_id":"{{Guid.NewGuid()}}","machine_id":"ASSY-L01-FCT-01","timestamp":"{{Now()}}","MACHINE_STATUS":"RUN","GOOD_QTY":125}"""),
    ("no machine_id                 -> rejected",          () => """{"MACHINE_STATUS":"RUN","GOOD_QTY":125}""")
};

try
{
    await Task.Delay(2000); // let the gateway open port 9000 and connect

    if (load is not null)
    {
        var resultFile = await load.RunAsync(
            rate: LoadParam(1, 5000), seconds: LoadParam(2, 60), machines: LoadParam(3, 50),
            drainSeconds: 10, tcpPort: GatewayTcpPort, gateway: gateway,
            resultsDir: Path.Combine(benchDir, "load-results"));
        Write(ConsoleColor.Yellow, $"[bench] results saved: {resultFile}");
        return;
    }

    if (args.Contains("--auto"))
    {
        foreach (var sample in samples)
        {
            var line = sample.Line();
            Write(ConsoleColor.Yellow, $"\n-> {sample.Label}");
            Write(ConsoleColor.DarkYellow, $"[tcp] sending {line}");
            await SendLineAsync(line);
            await Task.Delay(500);
        }
        await Task.Delay(1000);
        return;
    }

    var hold = Array.IndexOf(args, "--hold");
    if (hold >= 0)
    {
        var minutes = hold + 1 < args.Length && int.TryParse(args[hold + 1], out var m) ? m : 10;
        Write(ConsoleColor.Yellow, $"[bench] holding for {minutes} min — config page: http://localhost:{AdminPort}/config/");
        var until = DateTime.Now.AddMinutes(minutes);
        while (DateTime.Now < until && !gateway.HasExited)
            await Task.Delay(1000);
        return;
    }

    while (!gateway.HasExited)
    {
        Write(ConsoleColor.Yellow, "\nSend: " + string.Join("  ", samples.Select((s, i) => $"[{i + 1}] {s.Label.Split(' ')[0]}")) + "  [c] custom line  [q] quit");
        var key = Console.ReadKey(intercept: true).KeyChar;

        if (key is 'q' or 'Q')
            break;

        string? line = null;
        if (key is 'c' or 'C')
        {
            Console.Write("paste one line: ");
            line = Console.ReadLine();
        }
        else if (int.TryParse(key.ToString(), out var n) && n >= 1 && n <= samples.Length)
        {
            Write(ConsoleColor.Yellow, $"-> {samples[n - 1].Label}");
            line = samples[n - 1].Line();
        }

        if (string.IsNullOrWhiteSpace(line))
            continue;

        Write(ConsoleColor.DarkYellow, $"[tcp] sending {line}");
        try
        {
            await SendLineAsync(line);
        }
        catch (Exception ex)
        {
            Write(ConsoleColor.Red, $"[tcp] could not reach gateway on port {GatewayTcpPort}: {ex.Message}");
        }

        await Task.Delay(300); // let the broker line print before the menu
    }
}
finally
{
    StopGateway();
    fakeMes?.Stop();
    await server.StopAsync();
    Write(ConsoleColor.DarkGray, "[bench] stopped");
}

// ---- helpers ----

void StopGateway()
{
    try
    {
        if (!gateway.HasExited)
            gateway.Kill(entireProcessTree: true);
    }
    catch
    {
        // already gone
    }
}

static string Now() => DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff");

// Same shape your machine sends: payload as a JSON string.
static string Wrap(string topic, string payloadJson) =>
    new JsonObject { ["topic"] = topic, ["payload"] = payloadJson }
        .ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

static async Task SendLineAsync(string line)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, GatewayTcpPort);
    await using var stream = client.GetStream();
    await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
}

static int Run(string file, string args)
{
    using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false })!;
    p.WaitForExit();
    return p.ExitCode;
}

static void CopyDirectory(string from, string to)
{
    foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(dir.Replace(from, to));
    Directory.CreateDirectory(to);
    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        File.Copy(file, file.Replace(from, to), overwrite: true);
}

static void Write(ConsoleColor color, string text)
{
    lock (Console.Out)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}

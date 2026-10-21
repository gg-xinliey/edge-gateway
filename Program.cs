using System.Text.Json;
using System.Text.Json.Serialization;
using EdgeGateway.Services;
using EdgeGateway.Admin;

// Machine settings (ids, TCP port, MES url) live in Config/machine-config.json
// and MQTT settings in Config/mqtt-config.json. Both are read again on every
// restart from the config page. Only the page's own port is set here, so a
// config change can never lock the page out. Default 5100; "--admin-port <n>"
// overrides it (the offline test bench uses that to run next to a live gateway).
var adminPortArg = Array.IndexOf(args, "--admin-port");
var adminPort = adminPortArg >= 0 && adminPortArg + 1 < args.Length && int.TryParse(args[adminPortArg + 1], out var port)
    ? port
    : 5100;

// First: every console line also goes to logs\gateway-<date>.log (kept 14 days)
// and to the config page's Log view, so errors are not lost in the background.
var log = GatewayLog.Install(Path.Combine(AppContext.BaseDirectory, "logs"), keepDays: 14);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    Converters = { new JsonStringEnumConverter() }
};

var unidentified = new UnidentifiedTagTracker();
var configStore = new GatewayConfigStore("Config", jsonOptions, unidentified);
// Machines with their own mapping (Config/machines/<machine_id>/); the rest use the shared one.
var machines = new MachineRegistry(configStore, unidentified, jsonOptions);
if (machines.Ids.Count > 0)
    Console.WriteLine($"[config] machines with their own mapping: {string.Join(", ", machines.Ids)}");
var traffic = new TrafficRegistry(); // per connection / machine counts for the Machines view

// message_ids of the last 10 min, kept over a clean restart (saved below on Ctrl+C).
var messageIdsPath = Path.Combine(AppContext.BaseDirectory, "state", "message-ids.txt");
var messageIds = MessageIdFilter.Load(messageIdsPath);
if (messageIds.Count > 0)
    Console.WriteLine($"[ingest] {messageIds.Count:N0} message_ids from the last {MessageIdFilter.Window.TotalMinutes:0} min loaded (duplicate check)");

var gateway = new GatewayHost(machines, jsonOptions, traffic, messageIds);
var adminServer = new AdminWebServer(adminPort, "wwwroot", machines, gateway, log, jsonOptions, traffic);

Console.WriteLine("Edge gateway starting — Ctrl+C to stop");
Console.WriteLine($"Configuration UI: http://localhost:{adminPort}/config/");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await Task.WhenAll(
    gateway.RunAsync(cts.Token),      // TCP in -> MES outbox / digital twin; restartable
    adminServer.RunAsync(cts.Token)   // config page; stays up across restarts
);

// Only reached on Ctrl+C (a closed window or a hard kill never gets here),
// so the log shows whether the last stop was a clean one. ("stopped" would mark
// the line as a problem in the log.)
var savedIds = messageIds.Save(messageIdsPath);
Console.WriteLine($"[gateway] shut down cleanly (Ctrl+C): held data written to the outbox, {savedIds:N0} message_ids saved");

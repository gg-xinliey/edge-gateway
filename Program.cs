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

// "--data <folder>": where Config\, outbox\, logs\ and state\ live (default: next to the exe).
// The Docker image uses /data, a volume.
var dataArg = Array.IndexOf(args, "--data");
if (dataArg >= 0 && dataArg + 1 < args.Length)
    DataFolder.Use(args[dataArg + 1]);

// "--admin-all-ips": the config page listens on every address (in a container, where
// Docker's port mapping brings the requests in); see AdminWebServer.Refused.
var adminAllIps = args.Contains("--admin-all-ips");

// Ctrl+C, and SIGTERM (docker stop): a clean stop, the outbox writes what it holds.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, signal =>
    {
        signal.Cancel = true;
        cts.Cancel();
    });
await RunAsync(cts.Token);

async Task RunAsync(CancellationToken stop)
{
    // First: every console line also goes to logs\gateway-<date>.log (kept 14 days)
    // and to the config page's Log view, so errors are not lost in the background.
    var log = GatewayLog.Install(Path.Combine(DataFolder.Root, "logs"), keepDays: 14);

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // A new data folder (e.g. an empty Docker volume) starts with the default Config next to the exe.
    var configDir = Path.Combine(DataFolder.Root, "Config");
    if (!Directory.Exists(configDir))
    {
        Directory.CreateDirectory(configDir);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Config"), "*.json"))
            File.Copy(file, Path.Combine(configDir, Path.GetFileName(file)));
        Console.WriteLine($"[config] new data folder: default Config copied to {configDir}");
    }

    var unidentified = new UnidentifiedTagTracker();
    var configStore = new GatewayConfigStore(configDir, jsonOptions, unidentified);
    // Machines with their own mapping (Config/machines/<machine_id>/); the rest use the shared one.
    var machines = new MachineRegistry(configStore, unidentified, jsonOptions);
    if (machines.Ids.Count > 0)
        Console.WriteLine($"[config] machines with their own mapping: {string.Join(", ", machines.Ids)}");
    var traffic = new TrafficRegistry(); // per connection / machine counts for the Machines view

    // message_ids of the last 10 min, kept over a clean restart (saved below on Ctrl+C / docker stop).
    var messageIdsPath = Path.Combine(DataFolder.Root, "state", "message-ids.txt");
    var messageIds = MessageIdFilter.Load(messageIdsPath);
    if (messageIds.Count > 0)
        Console.WriteLine($"[ingest] {messageIds.Count:N0} message_ids from the last {MessageIdFilter.Window.TotalMinutes:0} min loaded (duplicate check)");

    // Every line received (time + machine), searchable on General -> Machines, kept 7 days.
    await using var messageLog = new MessageLog(Path.Combine(DataFolder.Root, "traffic"));
    var gateway = new GatewayHost(machines, jsonOptions, traffic, messageIds, messageLog);
    var adminServer = new AdminWebServer(adminPort, "wwwroot", machines, gateway, log, jsonOptions, traffic, adminAllIps, messageLog);

    Console.WriteLine("Edge gateway starting — Ctrl+C to stop");
    Console.WriteLine($"Data folder: {DataFolder.Root}");
    Console.WriteLine($"Configuration UI: http://localhost:{adminPort}/config/");

    await Task.WhenAll(
        gateway.RunAsync(stop),   // TCP in -> MES outbox / digital twin; restartable
        PageAsync()               // config page; stays up across restarts
    );

    // Only reached on Ctrl+C or SIGTERM (a hard kill never gets here), so the log
    // shows whether the last stop was a clean one. ("stopped" would mark the line as
    // a problem in the log.) Only ids of saved (acked) messages are in the filter;
    // rows an outbox could not write in time are logged by that outbox.
    var savedIds = messageIds.Save(messageIdsPath);
    Console.WriteLine($"[gateway] shut down cleanly, {savedIds:N0} message_ids of saved messages kept for the duplicate check");

    // The page failing to start (port taken, or no rights for the address)
    // must not stop the gateway, and must not stay silent until the next stop.
    async Task PageAsync()
    {
        try { await adminServer.RunAsync(stop); }
        catch (Exception ex) { Console.WriteLine($"[config] config page could not start on port {adminPort}, the gateway runs without it: {ex.Message}"); }
    }
}

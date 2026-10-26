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
var dataArg = Array.IndexOf(args, "--data");
if (dataArg >= 0 && dataArg + 1 < args.Length)
    DataFolder.Use(args[dataArg + 1]);

// "--service": started by Windows as the EdgeGateway service (service\install-service.ps1).
// Started by hand (dotnet run, the .bat files) it is a console window, Ctrl+C to stop.
var asService = args.Contains("--service");
if (asService && OperatingSystem.IsWindows())
{
    System.ServiceProcess.ServiceBase.Run(new GatewayService(RunAsync));
    return;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
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

    var unidentified = new UnidentifiedTagTracker();
    var configStore = new GatewayConfigStore(Path.Combine(DataFolder.Root, "Config"), jsonOptions, unidentified);
    // Machines with their own mapping (Config/machines/<machine_id>/); the rest use the shared one.
    var machines = new MachineRegistry(configStore, unidentified, jsonOptions);
    if (machines.Ids.Count > 0)
        Console.WriteLine($"[config] machines with their own mapping: {string.Join(", ", machines.Ids)}");
    var traffic = new TrafficRegistry(); // per connection / machine counts for the Machines view

    // message_ids of the last 10 min, kept over a clean restart (saved below on Ctrl+C / service stop).
    var messageIdsPath = Path.Combine(DataFolder.Root, "state", "message-ids.txt");
    var messageIds = MessageIdFilter.Load(messageIdsPath);
    if (messageIds.Count > 0)
        Console.WriteLine($"[ingest] {messageIds.Count:N0} message_ids from the last {MessageIdFilter.Window.TotalMinutes:0} min loaded (duplicate check)");

    var gateway = new GatewayHost(machines, jsonOptions, traffic, messageIds);
    var adminServer = new AdminWebServer(adminPort, "wwwroot", machines, gateway, log, jsonOptions, traffic);

    Console.WriteLine(asService ? "Edge gateway starting as a Windows service" : "Edge gateway starting — Ctrl+C to stop");
    Console.WriteLine($"Data folder: {DataFolder.Root}");
    Console.WriteLine($"Configuration UI: http://localhost:{adminPort}/config/");

    await Task.WhenAll(
        gateway.RunAsync(stop),   // TCP in -> MES outbox / digital twin; restartable
        PageAsync()               // config page; stays up across restarts
    );

    // Only reached on Ctrl+C or a service stop (a closed window or a hard kill never
    // gets here), so the log shows whether the last stop was a clean one. ("stopped"
    // would mark the line as a problem in the log.) Only ids of saved (acked) messages
    // are in the filter; rows an outbox could not write in time are logged by that outbox.
    var savedIds = messageIds.Save(messageIdsPath);
    Console.WriteLine($"[gateway] shut down cleanly ({(asService ? "service stop" : "Ctrl+C")}), {savedIds:N0} message_ids of saved messages kept for the duplicate check");

    // The page failing to start (port taken, or no URL rights for the service account)
    // must not stop the gateway, and must not stay silent until the next stop.
    async Task PageAsync()
    {
        try { await adminServer.RunAsync(stop); }
        catch (Exception ex) { Console.WriteLine($"[config] config page could not start on port {adminPort}, the gateway runs without it: {ex.Message}"); }
    }
}

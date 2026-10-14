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
var gateway = new GatewayHost(configStore, jsonOptions);
var adminServer = new AdminWebServer(adminPort, "wwwroot", configStore, gateway, unidentified, log, jsonOptions);

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

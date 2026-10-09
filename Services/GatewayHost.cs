using System.Text.Json;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Runs the gateway pipeline (TCP in -> MQTT relay / MES outbox / twin) and
// can restart it inside the same process: Restart() stops the current run,
// every config file is read again, and a fresh run starts. The config page
// lives outside this class, so it stays up across restarts and can report
// how the restart went.
//
// While restarting, the TCP port is closed for a moment: connected machines
// are dropped and must reconnect on their own.
public sealed class GatewayHost
{
    // How long a restart waits for the old run's parts to wind down.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly GatewayConfigStore _configStore;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _sync = new();
    private CancellationTokenSource? _currentRun;

    public GatewayHost(GatewayConfigStore configStore, JsonSerializerOptions jsonOptions)
    {
        _configStore = configStore;
        _jsonOptions = jsonOptions;
    }

    // starting | running | restarting | error
    public string State { get; private set; } = "starting";
    public string? LastError { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public int RunNumber { get; private set; }
    public MachineConfig Machine { get; private set; } = new();

    public void Restart()
    {
        lock (_sync)
        {
            State = "restarting";
            _currentRun?.Cancel();
        }

        Console.WriteLine("[gateway] restart requested from the config page");
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            using var run = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            lock (_sync)
                _currentRun = run;

            try
            {
                await RunOnceAsync(run.Token);
            }
            catch (Exception) when (run.IsCancellationRequested)
            {
                // Restart() or Ctrl+C: go round again (or leave, on Ctrl+C).
            }
            catch (Exception ex)
            {
                State = "error";
                LastError = ex.Message;
                Console.WriteLine($"[gateway] stopped: {ex.Message} — fix it on the config page and restart");

                // Stay down until Restart() or Ctrl+C; the config page keeps running.
                try { await Task.Delay(Timeout.Infinite, run.Token); }
                catch (OperationCanceledException) { }
            }
        }
    }

    // One run, from reading the config to being stopped. Returns or throws
    // only when the run ends.
    private async Task RunOnceAsync(CancellationToken runToken)
    {
        State = "starting";
        LastError = null;

        var machine = LoadMachineConfig();
        var mqttJson = await File.ReadAllTextAsync(Path.Combine(_configStore.ConfigRoot, "mqtt-config.json"), runToken);
        var mqttOptions = JsonSerializer.Deserialize<MqttOptions>(mqttJson, _jsonOptions)!;
        var relayConfig = JsonSerializer.Deserialize<MqttConnectionConfig>(mqttJson, _jsonOptions)!;
        _configStore.Reload(); // point-map.json + timestamp-rules.json

        var envelopeBuilder = new EnvelopeBuilder(machine.GatewayId, machine.DataSource, machine.ProtocolLabel);
        var outbox = new Outbox("outbox/outbox.db");

        var mqttPublisher = new MqttPublisherService(mqttOptions);
        var twinForwarder = new TwinForwarder(mqttPublisher, mqttOptions, machine.MachineId);
        await using var mqttRelay = new MqttRelay(relayConfig);

        using var http = new HttpClient();
        var mesForwarder = new MesForwarder(http, machine.MesUrl, outbox);

        void OnMessage(string raw)
        {
            if (mqttRelay.TryRelay(raw)) return;
            var config = _configStore.Current;
            var rawTags = PayloadParser.Parse(raw);

            var resolvedMachineId = rawTags.TryGetValue("topic_machine_id", out var m) && m is string ms ? ms : machine.MachineId;
            var qualifier = rawTags.TryGetValue("topic_qualifier", out var q) ? q?.ToString() : null;

            var buckets = config.Normalizer.Sort(rawTags);

            foreach (var (dataGroup, fields) in buckets)
            {
                if (TwinRouter.IsTwinOnly(dataGroup))
                {
                    _ = twinForwarder.PublishRecordAsync(resolvedMachineId, dataGroup, qualifier, fields);
                    continue;
                }

                config.TimeStamper.Stamp(resolvedMachineId, dataGroup, fields);
                var envelope = envelopeBuilder.Build(resolvedMachineId, dataGroup, fields);
                outbox.Write(envelope);
                Console.WriteLine($"[outbox] queued {envelope.event_id} ({dataGroup})");
            }
        }

        var tcpServer = new TcpIngestServer(machine.TcpPort, OnMessage);

        // Own token for the parts, so one failing part can stop the others
        // without looking like a Restart().
        using var parts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        var tasks = new[]
        {
            tcpServer.RunAsync(parts.Token),     // task on fetching from machine
            mesForwarder.RunAsync(parts.Token),  // task on sending to mes
            twinForwarder.RunAsync(parts.Token)  // task on publishing to the digital twin
        };

        try
        {
            // The TCP listener binds before its first await, so a busy port shows up right here.
            if (!tasks[0].IsFaulted)
            {
                Machine = machine;
                RunNumber++;
                StartedAt = DateTimeOffset.Now;
                State = "running";
                Console.WriteLine($"[gateway] running: machine {machine.MachineId}, gateway {machine.GatewayId}, tcp port {machine.TcpPort}");
            }

            await await Task.WhenAny(tasks);
        }
        finally
        {
            parts.Cancel();
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(StopTimeout));

            try { await mqttPublisher.DisconnectAsync(); }
            catch { /* shutting this run down anyway */ }
        }
    }

    private MachineConfig LoadMachineConfig()
    {
        var path = Path.Combine(_configStore.ConfigRoot, "machine-config.json");
        if (!File.Exists(path))
        {
            Console.WriteLine("[gateway] Config/machine-config.json not found — using built-in defaults");
            return new MachineConfig();
        }

        return JsonSerializer.Deserialize<MachineConfig>(File.ReadAllText(path), _jsonOptions) ?? new MachineConfig();
    }
}

using System.Globalization;
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

    // Throughput; sampled every StatsEvery into a [stats] line and /api/status.
    public GatewayStats Stats { get; } = new();
    private static readonly TimeSpan StatsEvery = TimeSpan.FromSeconds(10);

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
        // Next to the exe (not the working directory: a Windows service starts in System32).
        // Disposed last, so envelopes still queued in memory are written before the run ends.
        await using var outbox = new Outbox(Path.Combine(AppContext.BaseDirectory, "outbox"), machine.OutboxKeepSentDays, Stats);

        var mqttPublisher = new MqttPublisherService(mqttOptions);
        var twinForwarder = new TwinForwarder(mqttPublisher, mqttOptions, machine.MachineId);
        await using var mqttRelay = new MqttRelay(relayConfig, Stats);

        // 16 parallel sends; a hung MES must not hold a slot for the default 100 s.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var mesForwarder = new MesForwarder(http, machine.MesUrl, outbox, Stats);

        // MES data goes out per data group on its interval (sendEveryMs, read live).
        var intervalSender = new IntervalSender(outbox, envelopeBuilder, () => _configStore.Current);

        var rejected = new ThrottledLog(TimeSpan.FromSeconds(60));

        // One line from one machine, parsed once: IO/motion lines go to MQTT,
        // everything else to MES. "machine_id" and "timestamp" describe the
        // message; they are not data tags.
        void OnMessage(string raw, string sender)
        {
            Stats.Received();
            Dictionary<string, object?> rawTags;
            var text = raw.AsSpan().TrimStart();
            if (text.Length > 0 && text[0] == '{')
            {
                using var doc = JsonDocument.Parse(raw);
                if (mqttRelay.TryRelay(doc.RootElement)) return;
                rawTags = PayloadParser.Parse(doc.RootElement);
            }
            else
            {
                rawTags = PayloadParser.ParseCsv(raw);
            }

            // Shared server: data must never be filed under the wrong machine, so
            // a line that doesn't say which machine it is from is rejected.
            var machineId = TakeString(rawTags, "machine_id")
                ?? (rawTags.TryGetValue("topic_machine_id", out var m) ? m as string : null);
            if (string.IsNullOrWhiteSpace(machineId))
            {
                Stats.Rejected();
                rejected.Write($"no-machine|{sender}", $"[ingest] line from {sender} rejected: no \"machine_id\"");
                return;
            }

            // When it happened on the machine; arrival time if it sent none.
            DateTimeOffset? eventTime =
                TakeString(rawTags, "timestamp") is { } stamp &&
                DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at)
                    ? at
                    : null;

            var config = _configStore.Current;
            var qualifier = rawTags.TryGetValue("topic_qualifier", out var q) ? q?.ToString() : null;
            var buckets = config.Normalizer.Sort(rawTags);

            foreach (var (dataGroup, fields) in buckets)
            {
                if (TwinRouter.IsTwinOnly(dataGroup))
                {
                    _ = twinForwarder.PublishRecordAsync(machineId, dataGroup, qualifier, fields);
                    continue;
                }

                intervalSender.Accept(machineId, dataGroup, fields, eventTime);
            }
        }

        var tcpServer = new TcpIngestServer(machine.TcpPort, OnMessage);

        // Own token for the parts, so one failing part can stop the others
        // without looking like a Restart().
        using var parts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
        var tasks = new[]
        {
            tcpServer.RunAsync(parts.Token),       // task on fetching from machine
            intervalSender.RunAsync(parts.Token),  // task on releasing MES data per group interval
            mesForwarder.RunAsync(parts.Token),    // task on sending to mes
            twinForwarder.RunAsync(parts.Token),   // task on publishing to the digital twin
            ReportStatsAsync(outbox, parts.Token)  // one [stats] line every 10 s
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
            intervalSender.FlushAll(); // lines the TCP workers drained after the sender stopped

            try { await mqttPublisher.DisconnectAsync(); }
            catch { /* shutting this run down anyway */ }
        }
    }

    // Instead of a line per message: one summary every StatsEvery, only when
    // something is moving. (Avoids the words the log treats as problems.)
    private async Task ReportStatsAsync(Outbox outbox, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(StatsEvery, ct);

            long backlog;
            try { backlog = outbox.CountUnsent(); }
            catch { backlog = -1; } // outbox busy: report the rest anyway

            var r = Stats.Sample(StatsEvery, backlog);
            if (r.InPerSec == 0 && r.MqttPerSec == 0 && r.MesDeliveredPerSec == 0 && r.Backlog <= 0)
                continue;

            Console.WriteLine(
                $"[stats] in {r.InPerSec:0}/s" +
                (r.RejectedPerSec > 0 ? $" ({r.RejectedPerSec:0}/s without machine_id)" : "") +
                $" · mqtt {r.MqttPerSec:0}/s" +
                (r.MqttLostPerSec > 0 ? $" ({r.MqttLostPerSec:0}/s lost)" : "") +
                $" · mes queued {r.MesQueuedPerSec:0}/s, delivered {r.MesDeliveredPerSec:0}/s" +
                $" · backlog {(r.Backlog < 0 ? "?" : r.Backlog.ToString("N0", CultureInfo.InvariantCulture))}");
        }
    }

    // Removes key (any case) from the tags and returns its value as text.
    private static string? TakeString(Dictionary<string, object?> tags, string key)
    {
        var name = tags.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return null;

        var value = tags[name];
        tags.Remove(name);
        return value switch
        {
            null => null,
            string s => s,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
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

using System.Collections.Concurrent;
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

    private readonly MachineRegistry _machines;           // shared config + machines with their own mapping
    private readonly GatewayConfigStore _configStore;     // the shared one (also machine-config / mqtt-config)
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _sync = new();
    private CancellationTokenSource? _currentRun;

    private readonly TrafficRegistry? _traffic;
    private readonly MessageIdFilter _messageIds;

    public GatewayHost(MachineRegistry machines, JsonSerializerOptions jsonOptions, TrafficRegistry? traffic = null,
        MessageIdFilter? messageIds = null)
    {
        _machines = machines;
        _configStore = machines.Shared;
        _jsonOptions = jsonOptions;
        _traffic = traffic;
        _messageIds = messageIds ?? new MessageIdFilter();
    }

    // starting | running | restarting | error
    public string State { get; private set; } = "starting";
    public string? LastError { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public int RunNumber { get; private set; }
    public MachineConfig Machine { get; private set; } = new();

    private TcpIngestServer? _tcp; // the current run's

    // Closes one machine connection (ip:port) so its adapter reconnects and
    // resends what got no reply (e.g. right after a held machine was registered).
    public bool Disconnect(string endpoint) => _tcp?.Disconnect(endpoint) ?? false;

    // Throughput; sampled every StatsEvery into a [stats] line and /api/status.
    public GatewayStats Stats { get; } = new();
    private static readonly TimeSpan StatsEvery = TimeSpan.FromSeconds(10);

    // A machine timestamp further than this from arrival is warned about.
    private static readonly TimeSpan ClockWarning = TimeSpan.FromHours(1);

    // "message_id":"..." in a line that is not valid JSON (for its nack).
    private static readonly System.Text.RegularExpressions.Regex BrokenLineId =
        new("\"message_id\"\\s*:\\s*\"([^\"\\\\]{1,200})\"", System.Text.RegularExpressions.RegexOptions.Compiled);

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
        _machines.ReloadAll(); // point-map.json + timestamp-rules.json, shared and every machine's own

        var envelopeBuilder = new EnvelopeBuilder(machine.GatewayId, machine.DataSource, machine.ProtocolLabel);

        var mqttPublisher = new MqttPublisherService(mqttOptions);
        var twinForwarder = new TwinForwarder(mqttPublisher, mqttOptions, machine.GatewayId); // its keepalive: the gateway's own
        await using var mqttRelay = new MqttRelay(relayConfig, Stats);

        // 16 parallel sends; a hung MES must not hold a slot for the default 100 s.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Own token for the parts, so one failing part can stop the others
        // without looking like a Restart().
        using var parts = CancellationTokenSource.CreateLinkedTokenSource(runToken);

        // MES paths: the shared one (outbox/) and one per machine with its own
        // mapping (outbox/<machine_id>/). Each: outbox file right away, then to MES
        // per data group on its interval (sendEveryMs, read live). Outbox folders
        // are next to the exe (not the working directory: a service starts in System32).
        var outboxRoot = Path.Combine(AppContext.BaseDirectory, "outbox");
        var pipelines = new ConcurrentDictionary<string, Lazy<MesPipeline>>(StringComparer.OrdinalIgnoreCase);
        var pipelineFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        MesPipeline NewPipeline(string? machineId)
        {
            var pipeline = new MesPipeline(machineId, machineId is null ? outboxRoot : Path.Combine(outboxRoot, machineId),
                machine.OutboxKeepSentDays, http, machine.MesUrl, envelopeBuilder, Stats, parts.Token);
            // A MES sender that dies stops this run (as the shared one always did), so it is seen.
            _ = pipeline.Running.ContinueWith(t => pipelineFailed.TrySetException(t.Exception!.InnerExceptions),
                TaskContinuationOptions.OnlyOnFaulted);
            return pipeline;
        }

        MesPipeline? shared = null; // created in the try below, before any line arrives
        MesPipeline OwnPipeline(string machineId) =>
            pipelines.GetOrAdd(machineId, id => new Lazy<MesPipeline>(() => NewPipeline(id))).Value;

        List<MesPipeline> CreatedPipelines()
        {
            var created = pipelines.Values.Where(p => p.IsValueCreated).Select(p => p.Value).ToList();
            if (shared is not null)
                created.Insert(0, shared);
            return created;
        }

        // Replies to the machines on their own connections: {"ack":id} once a
        // MES message is on disk, {"nack":id,"reason":...} when it never will be.
        var replies = new ReplyHub();
        var acks = new AckTracker(replies, _messageIds);

        var rejected = new ThrottledLog(TimeSpan.FromSeconds(60));
        var unknownTags = new ThrottledLog(TimeSpan.FromSeconds(60)); // unmappedTags = send: once a minute per machine

        // One line from one machine, parsed once: IO/motion lines go to MQTT,
        // everything else to MES. "message_id", "machine_id", "data_source" and
        // "timestamp" describe the message; they are not data tags.
        void OnMessage(string raw, string sender)
        {
            Stats.Received();
            AckTracker.Ticket? ticket = null; // MES lines with a message_id: answered once on disk
            try
            {
                Handle(raw, sender, ref ticket);
            }
            catch (Exception ex)
            {
                // Not (fully) handled: no reply; the id was never marked saved, so
                // the adapter's resend is processed again.
                ticket?.Drop();

                // A line that isn't valid JSON never will be: if its message_id can still be
                // read, say so, or the adapter resends the same broken line forever.
                // (Not when that id is already saved: a good copy got its ack.)
                if (ticket is null && ex is JsonException && BrokenLineId.Match(raw) is { Success: true } id
                    && !_messageIds.Contains(id.Groups[1].Value))
                    replies.Nack(sender, id.Groups[1].Value, "not valid JSON");
                throw;
            }
        }

        void Handle(string raw, string sender, ref AckTracker.Ticket? ticket)
        {
            Dictionary<string, object?> rawTags;
            string? messageId;
            var text = raw.AsSpan().TrimStart();
            if (text.Length > 0 && text[0] == '{')
            {
                using var doc = JsonDocument.Parse(raw);
                var twin = MqttRelay.IsTwinLine(doc.RootElement); // IO/motion: live data, never answered

                // First of all: the same message_id within 10 min = the same line again.
                // MES lines: answered by the tracker (now, or with the first copy's answer).
                messageId = TopLevelString(doc.RootElement, "message_id");
                if (messageId is not null)
                {
                    var repeat = twin ? !_messageIds.TryAccept(messageId) : (ticket = acks.Begin(messageId, sender)) is null;
                    if (repeat)
                    {
                        Duplicate(sender, TopLevelString(doc.RootElement, "machine_id") ?? MachineFromTopic(doc.RootElement), messageId);
                        return;
                    }
                }

                if (mqttRelay.TryRelay(doc.RootElement))
                {
                    _traffic?.Message(MachineFromTopic(doc.RootElement), sender, "MQTT", null);
                    return;
                }
                rawTags = PayloadParser.Parse(doc.RootElement);
            }
            else
            {
                rawTags = PayloadParser.ParseCsv(raw);
                messageId = PeekString(rawTags, "message_id");
                if (messageId is not null && (ticket = acks.Begin(messageId, sender)) is null)
                {
                    Duplicate(sender, PeekString(rawTags, "machine_id"), messageId);
                    return;
                }
            }

            TakeString(rawTags, "message_id"); // checked above; not a data tag

            // Shared server: data must never be filed under the wrong machine, so
            // a line that doesn't say which machine it is from is rejected.
            var machineId = TakeString(rawTags, "machine_id")
                ?? (rawTags.TryGetValue("topic_machine_id", out var m) ? m as string : null);
            if (string.IsNullOrWhiteSpace(machineId))
            {
                Reject(sender, ticket, "no machine_id", "no-machine_id");
                return;
            }

            // Without a message_id a resent line could not be told apart: required.
            // (Nothing to put in a reply either, so it gets none.)
            if (ticket is null)
            {
                Reject(sender, null, "no message_id", "no-message_id");
                return;
            }

            // The machine's own mapping (Config/machines/<machine_id>/) and its own
            // MES path, if it has one. Never the shared mapping for a machine that
            // has its own: same field names may mean something else there.
            // No tab: held (default) until someone registers it, or the shared
            // mapping when GatewayConfig unregisteredMachines = "shared".
            GatewayConfigStore store;
            MesPipeline pipeline;
            if (_machines.TryGetOwn(machineId, out var own, out _))
            {
                if (own is null)
                {
                    Hold(sender, ticket, machineId, rawTags, "has an own mapping that can't be read");
                    return;
                }
                store = own;
                pipeline = OwnPipeline(machineId);
            }
            else if (machine.HoldUnregistered)
            {
                Hold(sender, ticket, machineId, rawTags, "is not registered (no tab)");
                return;
            }
            else
            {
                store = _configStore;
                pipeline = shared!;
            }

            // Which source system sent it; the configured dataSource if none.
            var dataSource = TakeString(rawTags, "data_source");

            // When it happened on the machine; arrival time if it sent none.
            // No zone in the text: timestampsWithoutZone (UTC by default).
            var zoneless = machine.ZonelessIsUtc ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal;
            DateTimeOffset? eventTime = null;
            if (TakeString(rawTags, "timestamp") is { } stamp)
            {
                // Unreadable or far off: still used (arrival time / as sent), but shown,
                // or wrong record times reach MES and nobody notices.
                if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, zoneless, out var at))
                    BadTime(machineId, $"timestamp \"{stamp}\" can't be read, the arrival time is used");
                else
                {
                    var off = at - DateTimeOffset.Now;
                    if (off.Duration() > ClockWarning)
                        BadTime(machineId, $"timestamp {stamp} is {off.Duration().TotalHours:0.0} h {(off > TimeSpan.Zero ? "ahead of" : "behind")} its arrival " +
                                           "(the machine's clock or timestampsWithoutZone is wrong, or an old message was resent): used as sent");
                    eventTime = at;
                }
            }

            // A tag the mapping doesn't know (GatewayConfig unmappedTags):
            //   "send" (default): the known tags go on, the unknown ones are left out of
            //          MES (they are on the tab's Unidentified list) and logged, so one
            //          new tag doesn't stop the machine's data;
            //   "hold": the whole message is held until every tag is mapped or ignored.
            var config = store.Current;

            // NXT sends alarm on/off as two data_sources (event names) and no alarm_status
            // tag. One source + alarm_status 1/0 lets the ALARM span close (start and clear
            // time). Only when the mapping knows alarm_status, so nothing is held for it.
            // shortcut: event names hard-coded, move to config when another machine type does this
            if (dataSource is "MCALARMON" or "MCALARMOFF" && config.Normalizer.IsMapped("alarm_status"))
            {
                rawTags["alarm_status"] = dataSource == "MCALARMON" ? 1.0 : 0.0;
                dataSource = "MCALARM";
            }

            var unmapped = config.Normalizer.Unmapped(rawTags);
            if (unmapped.Count > 0)
            {
                if (machine.HoldUnmappedTags)
                {
                    HoldUnmapped(sender, ticket, machineId, store.Label, unmapped, dataSource);
                    return;
                }
                unknownTags.Write($"unknown|{machineId}",
                    $"[ingest] {machineId} sent tags its mapping doesn't know ({string.Join(", ", unmapped)}): the other tags were sent, these are left out " +
                    $"until they are mapped or set to ignore on the {store.Label ?? "Template"} tab (PointMap > Unidentified)");
            }
            _traffic?.Message(machineId, sender, "MES", dataSource, messageId);

            var interval = pipeline.SenderFor(store);
            var qualifier = rawTags.TryGetValue("topic_qualifier", out var q) ? q?.ToString() : null;
            var buckets = config.Normalizer.Sort(rawTags);

            foreach (var (dataGroup, fields) in buckets)
            {
                // "ignore" tags are known but never sent; unknown tags never go to MES.
                if (dataGroup.Equals(Normalizer.IgnoredGroup, StringComparison.OrdinalIgnoreCase)
                    || dataGroup.Equals(Normalizer.UnidentifiedGroup, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (TwinRouter.IsTwinOnly(dataGroup))
                {
                    _ = twinForwarder.PublishRecordAsync(machineId, dataGroup, qualifier, fields);
                    continue;
                }

                // One part per row; a group with nothing new needs no saving.
                ticket.AddPart();
                if (!interval.Accept(machineId, dataGroup, fields, eventTime, dataSource, messageId, ticket.PartDone))
                    ticket.PartDone();
            }

            ticket.Release(); // answered now if nothing needed saving, else once the last row is on disk
        }

        var held = new ThrottledLog(TimeSpan.FromSeconds(60));

        // Not processed and not answered: the adapter keeps the message and sends
        // it again (after a reconnect) once the machine has a working tab. The id
        // was never marked saved, so that resend is processed, not taken as a repeat.
        // The config page lists the machine with the tags it sent; the log line
        // (once a minute per machine, with a count) pops up there.
        void Hold(string sender, AckTracker.Ticket ticket, string machineId, Dictionary<string, object?> tags, string why)
        {
            ticket.Drop();

            var dataSource = PeekString(tags, "data_source");
            var dataTags = tags.Where(t => !t.Key.Equals("data_source", StringComparison.OrdinalIgnoreCase)
                                        && !t.Key.Equals("timestamp", StringComparison.OrdinalIgnoreCase))
                               .ToDictionary(t => t.Key, t => t.Value);
            _machines.Held.Hold(machineId, sender, dataSource, dataTags);
            _traffic?.Held(machineId, sender, ticket.MessageId);
            held.Write($"held|{machineId}",
                $"[ingest] {machineId} {why}: its data cannot be used until it has a tab on the config page (held: not processed, not answered, the adapter keeps it)");
        }

        var heldUnmapped = new ThrottledLog(TimeSpan.FromSeconds(60));

        // Like Hold, but the machine is registered: its mapping lacks tags it sent.
        // Released (connection closed, adapter resends) once every one of them is
        // mapped or set to ignore on its tab.
        void HoldUnmapped(string sender, AckTracker.Ticket ticket, string machineId, string? mapping, List<string> tags, string? dataSource)
        {
            ticket.Drop();
            _machines.Unmapped.Hold(machineId, mapping, sender, tags);
            _traffic?.Held(machineId, sender, ticket.MessageId);
            heldUnmapped.Write($"unmapped|{machineId}",
                $"[ingest] {machineId} sent tags its mapping doesn't know ({string.Join(", ", tags)}): its data cannot be used until they are mapped or set to ignore on the {mapping ?? "Template"} tab (held, not answered, the adapter keeps it)");
        }

        var badTimes = new ThrottledLog(TimeSpan.FromSeconds(60));

        // Counted per machine (Machines page, "time warnings"); logged once a minute per machine.
        void BadTime(string machineId, string problem)
        {
            _traffic?.BadTime(machineId);
            badTimes.Write($"time|{machineId}", $"[ingest] {machineId}: {problem}");
        }

        var duplicates = new ThrottledLog(TimeSpan.FromSeconds(60));

        // Counting and logging only: a MES repeat is answered by the AckTracker
        // (like its first copy); IO/motion lines never are.
        void Duplicate(string sender, string? machineId, string messageId)
        {
            Stats.Duplicate();
            _traffic?.Duplicate(sender, machineId);
            duplicates.Write($"dup|{sender}", $"[ingest] line from {sender} ignored: message_id {messageId} already received");
        }

        // Answered with {"nack":id,"reason":reason}; the id was never marked saved,
        // so the corrected line can be sent with the same message_id.
        void Reject(string sender, AckTracker.Ticket? ticket, string reason, string logKey)
        {
            ticket?.Reject(reason);
            Stats.Rejected();
            _traffic?.Rejected(sender);
            rejected.Write($"{logKey}|{sender}", $"[ingest] line from {sender} rejected: {reason}");
        }

        var tcpServer = new TcpIngestServer(System.Net.IPAddress.Parse(machine.ListenAddress), machine.TcpPort, OnMessage, _traffic, replies);
        _tcp = tcpServer;

        var tasks = Array.Empty<Task>();
        try
        {
            // MES paths started now: the shared one, every machine with its own
            // mapping, and every machine outbox folder (its rows still go to MES,
            // even if the mapping was removed). Others start when needed.
            shared = NewPipeline(null);
            foreach (var id in _machines.Ids)
                OwnPipeline(id);
            if (Directory.Exists(outboxRoot))
                foreach (var folder in Directory.GetDirectories(outboxRoot))
                    if (MachineRegistry.IsValidId(Path.GetFileName(folder)))
                        OwnPipeline(Path.GetFileName(folder));

            tasks = new[]
            {
                tcpServer.RunAsync(parts.Token),       // task on fetching from machine
                pipelineFailed.Task,                   // a MES sender failed (they run in their pipelines)
                twinForwarder.RunAsync(parts.Token),   // task on publishing to the digital twin
                ReportStatsAsync(() => CreatedPipelines().Sum(p => p.Outbox.CountUnsent()), parts.Token) // one [stats] line every 10 s
            };

            // The TCP listener binds before its first await, so a busy port shows up right here.
            if (!tasks[0].IsFaulted)
            {
                Machine = machine;
                RunNumber++;
                StartedAt = DateTimeOffset.Now;
                State = "running";
                Console.WriteLine($"[gateway] running: gateway {machine.GatewayId}, tcp {machine.ListenAddress}:{machine.TcpPort}");
            }

            await await Task.WhenAny(tasks);
        }
        finally
        {
            parts.Cancel();
            pipelineFailed.TrySetResult(); // stopping is not a failure
            await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(StopTimeout));

            // The TCP workers are done: every record is in an outbox queue. Stop the
            // MES senders, then each outbox writes what it still holds.
            var created = CreatedPipelines();
            await Task.WhenAny(Task.WhenAll(created.Select(p => p.Running)), Task.Delay(StopTimeout));
            foreach (var pipeline in created)
                await pipeline.DisposeAsync();

            try { await mqttPublisher.DisconnectAsync(); }
            catch { /* shutting this run down anyway */ }
        }
    }

    // Instead of a line per message: one summary every StatsEvery, only when
    // something is moving. (Avoids the words the log treats as problems.)
    // countUnsent: rows not yet delivered, all outboxes together.
    private async Task ReportStatsAsync(Func<long> countUnsent, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(StatsEvery, ct);

            long backlog;
            try { backlog = countUnsent(); }
            catch { backlog = -1; } // outbox busy: report the rest anyway

            var r = Stats.Sample(backlog);
            if (r.InPerSec == 0 && r.MqttPerSec == 0 && r.MesDeliveredPerSec == 0 && r.Backlog <= 0)
                continue;

            Console.WriteLine(
                $"[stats] in {Rate(r.InPerSec)}/s" +
                (r.RejectedPerSec > 0 ? $" ({Rate(r.RejectedPerSec)}/s without machine_id or message_id)" : "") +
                (r.DuplicatePerSec > 0 ? $" ({Rate(r.DuplicatePerSec)}/s repeated message_id, ignored)" : "") +
                $" · mqtt {Rate(r.MqttPerSec)}/s" +
                (r.MqttLostPerSec > 0 ? $" ({Rate(r.MqttLostPerSec)}/s lost)" : "") +
                $" · mes queued {Rate(r.MesQueuedPerSec)}/s, delivered {Rate(r.MesDeliveredPerSec)}/s" +
                $" · backlog {(r.Backlog < 0 ? "?" : r.Backlog.ToString("N0", CultureInfo.InvariantCulture))}");
        }
    }

    // One decimal below 10/s, so 1 message in 10 s shows as 0.1/s, not 0/s.
    private static string Rate(double perSecond) =>
        perSecond.ToString(perSecond < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);

    // Machine of an MQTT line: the topic segment before the fixed two-part
    // ending, e.g. svi/TEST/NXT102/IO/input -> NXT102 (only 2 segments: the first).
    private static string MachineFromTopic(JsonElement root)
    {
        if (!root.TryGetProperty("topic", out var t) || t.ValueKind != JsonValueKind.String)
            return "(no topic)";
        var segments = (t.GetString() ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 ? segments[^3] : segments.Length > 0 ? segments[0] : "(no topic)";
    }

    // A top-level property (any case) of a JSON line as text: a string, or a
    // number as written. Empty or missing = null.
    private static string? TopLevelString(JsonElement root, string key)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var p in root.EnumerateObject())
        {
            if (!p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            var value = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString(),
                JsonValueKind.Number => p.Value.GetRawText(),
                _ => null
            };
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        return null;
    }

    // Like TakeString, but leaves the tag in place. Empty = null.
    private static string? PeekString(Dictionary<string, object?> tags, string key)
    {
        var name = tags.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        var value = name is null ? null : Convert.ToString(tags[name], CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

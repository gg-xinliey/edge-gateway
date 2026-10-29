using EdgeGateway.Models;
using EdgeGateway.Services;
using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EdgeGateway.Admin;

/// <summary>
/// Tiny localhost-only admin web server. It intentionally uses HttpListener so the
/// config UI can be dropped into the existing console/server project without adding
/// ASP.NET packages or replacing the current Program.cs architecture.
/// It runs outside GatewayHost, so it stays up while the gateway restarts.
/// </summary>
public sealed class AdminWebServer
{
    private readonly HttpListener _listener = new();
    private readonly string _wwwRoot;
    private readonly string _configRoot;
    private readonly MachineRegistry _machines;
    private readonly GatewayConfigStore _configStore;
    private readonly GatewayHost _gateway;
    private readonly UnidentifiedTagTracker _unidentified;
    private readonly GatewayLog _log;
    private readonly TrafficRegistry? _traffic;
    private readonly MessageLog? _messageLog;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly int _port;
    private readonly bool _allIps; // in a container: listen on every address, check the browser's host name instead

    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly Dictionary<string, string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["point-map"] = "point-map.json",
        ["timestamp-rules"] = "timestamp-rules.json",
        ["mqtt-config"] = "mqtt-config.json",
        ["machine-config"] = "machine-config.json",
        ["mes-schema"] = "mes-schema.json" // the MES format: columns per data group, for every machine
    };

    // Only one of these exists, for all machines (no ?machine=).
    private static readonly HashSet<string> SharedOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "mqtt-config",
        "machine-config",
        "mes-schema"
    };

    // Read only when the gateway (re)starts; the other two are applied live on save.
    private static readonly HashSet<string> RestartToApply = new(StringComparer.OrdinalIgnoreCase)
    {
        "mqtt-config",
        "machine-config"
    };

    public AdminWebServer(
        int port,
        string webRoot,
        MachineRegistry machines,
        GatewayHost gateway,
        GatewayLog log,
        JsonSerializerOptions jsonOptions,
        TrafficRegistry? traffic = null,
        bool allIps = false,
        MessageLog? messageLog = null)
    {
        _messageLog = messageLog;
        _allIps = allIps;
        _log = log;
        _traffic = traffic;
        _port = port;
        _wwwRoot = Path.IsPathRooted(webRoot)
            ? Path.GetFullPath(webRoot)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, webRoot));
        _machines = machines;
        _configStore = machines.Shared;
        _configRoot = _configStore.ConfigRoot; // same folder the runtime reloads from
        _gateway = gateway;
        _unidentified = machines.SharedUnidentified;
        _jsonOptions = jsonOptions;
    }

    // ?machine=<id> on PointMap / TimeStamp / Unidentified / Outbox calls: that
    // machine's own mapping (or outbox folder). Empty = the shared one.
    private static string? MachineParam(HttpListenerContext context)
    {
        var value = context.Request.QueryString["machine"];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_wwwRoot);
        Directory.CreateDirectory(_configRoot);

        var prefix = _allIps ? $"http://+:{_port}/" : $"http://localhost:{_port}/";
        _listener.Prefixes.Add(prefix);
        _listener.Start();

        Console.WriteLine($"[config] UI: {prefix}config/");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            if (_listener.IsListening)
                _listener.Stop();
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";

            var refused = Refused(context.Request);
            if (refused is not null)
            {
                await WriteJsonAsync(context.Response, 403, new { error = refused });
                return;
            }

            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleApiAsync(context, path);
                return;
            }

            await ServeStaticAsync(context, path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] request error: {ex.Message}");
            await WriteJsonAsync(context.Response, 500, new { error = ex.Message });
        }
        finally
        {
            context.Response.Close();
        }
    }

    // Only this PC, and changes only from the config page itself (issue #12): a web
    // page from anywhere else, opened in a browser on this PC, could otherwise POST to
    // the API (discard failed messages, restart, add tabs). A browser marks such a
    // request with Sec-Fetch-Site / Origin; scripts on this PC send neither and still work.
    // Null = allowed, else why not.
    private string? Refused(HttpListenerRequest request) =>
        Refused(request.RemoteEndPoint?.Address, request.HttpMethod, request.ContentType,
            request.Headers["Sec-Fetch-Site"], request.Headers["Origin"], request.Url, _allIps);

    // allIps (in a container): Docker's port mapping hands the request over, so the peer
    // is never loopback. The page counts as "this PC only" when Docker publishes it on the
    // host's 127.0.0.1 only (docker-compose.yml), and the browser must have asked for
    // localhost / 127.0.0.1: a page under another name (DNS rebinding) is refused.
    public static string? Refused(IPAddress? remote, string method, string? contentType, string? site, string? origin, Uri? url, bool allIps)
    {
        if (allIps
                ? url?.Host is not ("localhost" or "127.0.0.1" or "[::1]")
                : remote is not null && !IPAddress.IsLoopback(remote))
            return "The config page only works on the gateway PC itself.";
        if (method is "GET" or "HEAD")
            return null;

        // JSON forces a browser to ask first (CORS preflight), which this server never
        // allows: a second layer if a browser sends neither header below.
        if (!(contentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            return "Changes are only accepted as JSON (Content-Type: application/json).";

        // Same origin = the address the browser used for this request (also behind a
        // Docker port mapping such as localhost:8080 -> 5100).
        var self = url?.GetLeftPart(UriPartial.Authority);
        var fromOtherSite = (site is not null && site is not ("same-origin" or "none"))
            || (origin is not null && !origin.Equals(self, StringComparison.OrdinalIgnoreCase));
        return fromOtherSite ? "Changes are only accepted from the config page itself." : null;
    }

    private async Task HandleApiAsync(HttpListenerContext context, string path)
    {
        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, 200, new { ok = true, service = "EdgeGateway.ConfigUI" });
            return;
        }

        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, 200, new
            {
                state = _gateway.State,
                lastError = _gateway.LastError,
                startedAt = _gateway.StartedAt,
                run = _gateway.RunNumber,
                gatewayId = _gateway.Machine.GatewayId,
                tcpPort = _gateway.Machine.TcpPort,
                held = _machines.Held.Count,         // machines sending without a tab (data held)
                unmapped = _machines.Unmapped.Count, // machines whose data is held for tags not mapped yet
                failed = FailedCount(),              // messages MES rejected as invalid, waiting for re-send / discard
                mqttConnected = _gateway.MqttConnected, // null while no run is going
                adminPort = _port,
                stats = _gateway.Stats.Latest // per-second rates over the last 10 s
            });
            return;
        }

        if (path.Equals("/api/gateway/restart", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethodsEqual(context, "POST"))
            {
                await WriteJsonAsync(context.Response, 405, new { error = "Method not allowed" });
                return;
            }

            _gateway.Restart();
            await WriteJsonAsync(context.Response, 202, new { ok = true, message = "Restarting gateway." });
            return;
        }

        // Machines view: connections + machines with status and message counts.
        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/machines", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, 200, _traffic?.Snapshot() ?? new { machines = Array.Empty<object>(), connections = Array.Empty<object>() });
            return;
        }

        // Machines sending MES data without a tab: their data is held until registered.
        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/held", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, 200, _machines.Held.Snapshot());
            return;
        }

        // Machines whose data is held for tags their mapping doesn't know yet.
        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/unmapped", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, 200, _machines.Unmapped.Snapshot());
            return;
        }

        // Machines with their own mapping: list, add (copying a mapping), remove.
        // An automatic tab was checked by a person: no longer marked auto.
        if (path.Equals("/api/machine-configs/confirm", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethodsEqual(context, "POST"))
            {
                await WriteJsonAsync(context.Response, 405, new { error = "Method not allowed" });
                return;
            }
            var id = MachineParam(context);
            if (id is not null && _machines.ConfirmAuto(id))
                await WriteJsonAsync(context.Response, 200, new { ok = true });
            else
                await WriteJsonAsync(context.Response, 404, new { error = "That machine has no automatic tab to confirm." });
            return;
        }

        // Another mapping for a machine's tab: a copy of copyFrom's (another tab, or the template when
        // empty). A confirmed tab needs the password (#31), an automatic one doesn't (#30).
        if (path.Equals("/api/machine-configs/reassign", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethodsEqual(context, "POST"))
            {
                await WriteJsonAsync(context.Response, 405, new { error = "Method not allowed" });
                return;
            }
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
            string? Text(string name) => body.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var problem = _machines.Reassign(MachineParam(context) ?? "", Text("copyFrom")?.Trim(), Text("password"));
            if (problem is null)
                await WriteJsonAsync(context.Response, 200, new { ok = true });
            else
                await WriteJsonAsync(context.Response, problem == "Wrong password." ? 403 : 400, new { error = problem });
            return;
        }

        if (path.Equals("/api/machine-configs", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethodsEqual(context, "GET"))
            {
                await WriteJsonAsync(context.Response, 200, _machines.List().Select(m => new
                {
                    machineId = m.Id, readable = m.Readable, error = m.Error,
                    autoCopiedFrom = _machines.AutoCopiedFrom(m.Id), // null = not created automatically; "" = from the template
                }));
                return;
            }

            if (HttpMethodsEqual(context, "POST"))
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                string? Text(string name) => body.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

                var id = Text("machineId") ?? "";
                var heldOn = _machines.Held.ConnectionOf(id); // was it waiting (held)?
                var problem = _machines.Create(id, Text("copyFrom"));
                if (problem is not null)
                {
                    await WriteJsonAsync(context.Response, 400, new { error = problem });
                    return;
                }

                // Its adapter has been keeping the held messages: close its
                // connection so it reconnects and sends them again, now processed.
                var reconnected = heldOn is not null && _gateway.Disconnect(heldOn);
                await WriteJsonAsync(context.Response, 200, new { ok = true, wasHeld = heldOn is not null, reconnected });
                return;
            }

            if (HttpMethodsEqual(context, "DELETE"))
            {
                var id = MachineParam(context);
                if (id is not null && _machines.Remove(id))
                    await WriteJsonAsync(context.Response, 200, new { ok = true });
                else
                    await WriteJsonAsync(context.Response, 404, new { error = "That machine has no own mapping." });
                return;
            }

            await WriteJsonAsync(context.Response, 405, new { error = "Method not allowed" });
            return;
        }

        // Outbox view (read-only): days, one day's rows (filters + paging), one row in full.
        // ?scope=<machine_id> = that machine's own outbox folder; empty = the shared one.
        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/outbox/scopes", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, 200, OutboxReader.Scopes());
            return;
        }

        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/outbox/days", StringComparison.OrdinalIgnoreCase))
        {
            var days = OutboxReader.Days(context.Request.QueryString["scope"]);
            if (days is null)
                await WriteJsonAsync(context.Response, 404, new { error = "No such outbox folder." });
            else
                await WriteJsonAsync(context.Response, 200, days);
            return;
        }

        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/outbox", StringComparison.OrdinalIgnoreCase))
        {
            var q = context.Request.QueryString;
            var before = long.TryParse(q["before"], out var b) ? b : (long?)null;
            var limit = int.TryParse(q["limit"], out var l) ? l : 100;
            var page = OutboxReader.Query(q["scope"], q["day"] ?? "", q["machine"], q["group"], q["source"], q["status"], before, limit);
            if (page is null)
                await WriteJsonAsync(context.Response, 404, new { error = "No outbox file for that day (older than 7 days, or not a yyyy-MM-dd day)." });
            else
                await WriteJsonAsync(context.Response, 200, page);
            return;
        }

        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/outbox/row", StringComparison.OrdinalIgnoreCase))
        {
            var q = context.Request.QueryString;
            var body = long.TryParse(q["id"], out var id) ? OutboxReader.Row(q["scope"], q["day"] ?? "", id) : null;
            if (body is null)
            {
                await WriteJsonAsync(context.Response, 404, new { error = "Row not found." });
                return;
            }

            using var envelope = JsonDocument.Parse(body);
            await WriteJsonAsync(context.Response, 200, envelope.RootElement);
            return;
        }

        // Failed messages (MES rejected them as invalid), across all outboxes:
        // GET lists them (?limit=), POST /resend and /discard act on
        // {"rows":[{"scope","day","id"}]} or {"all":true}; discard needs a "reason".
        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/failed", StringComparison.OrdinalIgnoreCase))
        {
            var limit = int.TryParse(context.Request.QueryString["limit"], out var l) ? l : 200;
            await WriteJsonAsync(context.Response, 200, OutboxReader.ListFailed(limit));
            return;
        }

        if (path.Equals("/api/failed/resend", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/failed/discard", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethodsEqual(context, "POST"))
            {
                await WriteJsonAsync(context.Response, 405, new { error = "Method not allowed" });
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
            var root = body.RootElement;
            var all = root.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.True;
            var rows = new List<OutboxReader.FailedRef>();
            if (root.TryGetProperty("rows", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in list.EnumerateArray())
                {
                    if (row.ValueKind == JsonValueKind.Object
                        && row.TryGetProperty("day", out var day) && day.ValueKind == JsonValueKind.String
                        && row.TryGetProperty("id", out var id) && id.TryGetInt64(out var rowId))
                    {
                        var scope = row.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                        rows.Add(new OutboxReader.FailedRef(scope, day.GetString()!, rowId));
                    }
                }
            }

            if (!all && rows.Count == 0)
            {
                await WriteJsonAsync(context.Response, 400, new { error = "Nothing chosen: give rows or all." });
                return;
            }

            var which = all ? "all failed messages" : $"{rows.Count} failed message(s)";
            if (path.EndsWith("/resend", StringComparison.OrdinalIgnoreCase))
            {
                var count = OutboxReader.Resend(rows, all);
                Console.WriteLine($"[failed] re-send asked for {which}: {count} put back in the send queue");
                await WriteJsonAsync(context.Response, 200, new { ok = true, count });
                return;
            }

            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(reason))
            {
                await WriteJsonAsync(context.Response, 400, new { error = "Discard needs a reason (it is logged and kept with the rows)." });
                return;
            }

            var discarded = OutboxReader.Discard(rows, all, reason);
            var ids = all ? "" : " (" + string.Join(", ", rows.Take(20).Select(x => $"{(string.IsNullOrEmpty(x.Scope) ? "shared" : x.Scope)}/{x.Day}#{x.Id}")) + (rows.Count > 20 ? ", ..." : "") + ")";
            Console.WriteLine($"[failed] discarded {discarded} of {which}{ids}, reason: {reason}");
            await WriteJsonAsync(context.Response, 200, new { ok = true, count = discarded });
            return;
        }

        // Log lines newer than ?after=<id> (all kept lines when omitted), plus the
        // total number of problem lines since the gateway started.
        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/logs", StringComparison.OrdinalIgnoreCase))
        {
            _ = long.TryParse(context.Request.QueryString["after"], out var after);
            await WriteJsonAsync(context.Response, 200, new { lines = _log.Since(after), errorCount = _log.ErrorCount });
            return;
        }

        // Message log: how many lines arrived from ?from to ?to (local time, e.g. 2026-10-28T08:00),
        // all machines or ?machine=, with a count per machine and per hour.
        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/messages", StringComparison.OrdinalIgnoreCase))
        {
            var q = context.Request.QueryString;
            if (_messageLog is null)
                await WriteJsonAsync(context.Response, 404, new { error = "The message log is not running." });
            else if (!DateTime.TryParse(q["from"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var from)
                     || !DateTime.TryParse(q["to"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var to) || to <= from)
                await WriteJsonAsync(context.Response, 400, new { error = "from and to must be times like 2026-10-28T08:00, with to after from." });
            else
                await WriteJsonAsync(context.Response, 200, _messageLog.Count(from, to, string.IsNullOrWhiteSpace(q["machine"]) ? null : q["machine"]!.Trim()));
            return;
        }

        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/unidentified", StringComparison.OrdinalIgnoreCase))
        {
            var id = MachineParam(context);
            var tracker = id is null ? _unidentified : _machines.UnidentifiedFor(id);
            var config = (id is null ? _configStore : _machines.StoreFor(id))?.Current;
            var synonyms = NameMatcher.WithLearned(NameMatcher.ParseSynonyms(_gateway.Machine.NameMatchSynonyms),
                _machines.OtherPointMaps(null).Prepend(_configStore.Current.PointMap).SelectMany(m => m));
            if (tracker is null)
                await WriteJsonAsync(context.Response, 404, new { error = $"{id} has no own mapping." });
            else // each with its best MES column by name, for the page to suggest
                await WriteJsonAsync(context.Response, 200, tracker.Snapshot().Select(u => new
                {
                    u.Tag, u.LastValue, u.Count, u.FirstSeen, u.LastSeen,
                    suggestion = config is null ? null : NameMatcher.Seen(u.Tag, _machines.OtherPointMaps(id), config.PointMap)
                                                         ?? NameMatcher.Best(u.Tag, config.MesColumns, synonyms, config.PointMap, u.LastValue),
                }));
            return;
        }

        if (path.Equals("/api/config/files", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethodsEqual(context, "GET"))
            {
                await WriteJsonAsync(context.Response, 405, new { error = "Method not allowed" });
                return;
            }

            var files = AllowedFiles.Select(x => new
            {
                key = x.Key,
                fileName = x.Value,
                path = Path.Combine("Config", x.Value),
                restartToApply = RestartToApply.Contains(x.Key)
            });

            await WriteJsonAsync(context.Response, 200, files);
            return;
        }

        if (path.Equals("/api/config/reload", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethodsEqual(context, "POST"))
            {
                await WriteJsonAsync(context.Response, 405, new { error = "Method not allowed" });
                return;
            }

            _configStore.Reload();
            await WriteJsonAsync(context.Response, 200, new { ok = true, message = "Configuration reloaded." });
            return;
        }

        const string prefix = "/api/config/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, 404, new { error = "Not found" });
            return;
        }

        var key = path[prefix.Length..].Trim('/');
        if (!AllowedFiles.TryGetValue(key, out var fileName))
        {
            await WriteJsonAsync(context.Response, 404, new { error = "Unknown configuration file." });
            return;
        }

        // ?machine=<id>: that machine's own PointMap / TimeStamp. MQTT and machine
        // settings exist only once (shared).
        var machineId = MachineParam(context);
        var folder = _configRoot;
        if (machineId is not null)
        {
            if (SharedOnly.Contains(key))
            {
                await WriteJsonAsync(context.Response, 400, new { error = $"{fileName} is shared by all machines." });
                return;
            }
            var own = _machines.FolderFor(machineId);
            if (own is null)
            {
                await WriteJsonAsync(context.Response, 404, new { error = $"{machineId} has no own mapping." });
                return;
            }
            folder = own;
        }

        var filePath = Path.Combine(folder, fileName);

        if (HttpMethodsEqual(context, "GET"))
        {
            var fromFile = File.Exists(filePath) ? JsonNode.Parse(await File.ReadAllTextAsync(filePath)) : null;
            var result = WithDefaults(key, fromFile);
            if (key.Equals("mqtt-config", StringComparison.OrdinalIgnoreCase))
                MaskPassword(result);
            if (result is null)
            {
                await WriteJsonAsync(context.Response, 404, new { error = $"Missing Config/{fileName}" });
                return;
            }

            await WriteJsonAsync(context.Response, 200, result);
            return;
        }

        if (HttpMethodsEqual(context, "PUT"))
        {
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                var raw = await reader.ReadToEndAsync();
                if (key.Equals("mqtt-config", StringComparison.OrdinalIgnoreCase))
                    raw = KeepStoredPassword(raw, filePath);
                using var document = JsonDocument.Parse(raw);

                // Load it the way the gateway will, before touching the file.
                var problem = ConfigValidator.Validate(key, raw, _jsonOptions, _port);
                if (problem is not null)
                {
                    await WriteJsonAsync(context.Response, 400, new { error = problem });
                    return;
                }

                // Re-serialize through System.Text.Json to validate and normalize formatting.
                var normalized = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                var backupPath = filePath + ".bak";
                if (File.Exists(filePath))
                    File.Copy(filePath, backupPath, overwrite: true);

                // Written in full next to it (on disk), then swapped in: a power cut while
                // saving leaves the old file or the new one, never half of one (#17).
                var tempPath = filePath + ".tmp";
                await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                await using (var writer = new StreamWriter(file, Encoding.UTF8))
                {
                    await writer.WriteAsync(normalized + Environment.NewLine);
                    await writer.FlushAsync();
                    file.Flush(flushToDisk: true);
                }
                File.Move(tempPath, filePath, overwrite: true);

                var restartToApply = RestartToApply.Contains(key);

                // Point map and timestamp rules take effect right away (the machine's own, or the shared);
                // the MES format right away for every machine.
                var reconnected = 0;
                if (!restartToApply)
                {
                    if (key.Equals("mes-schema", StringComparison.OrdinalIgnoreCase))
                        _machines.ReloadAll();
                    else if (machineId is null)
                        _configStore.Reload();
                    else
                        _machines.Reload(machineId);

                    // Machines held for tags this mapping now knows (mapped or ignore):
                    // close their connections so the adapters resend the held messages.
                    if (key.Equals("point-map", StringComparison.OrdinalIgnoreCase)
                        && (machineId is null ? _configStore : _machines.StoreFor(machineId)) is { } saved)
                    {
                        var normalizer = saved.Current.Normalizer;
                        foreach (var connection in _machines.Unmapped.Resolve(machineId, normalizer.IsMapped))
                            if (_gateway.Disconnect(connection))
                                reconnected++;
                    }
                }

                await WriteJsonAsync(context.Response, 200, new
                {
                    ok = true,
                    file = fileName,
                    reloaded = !restartToApply,
                    restartToApply,
                    reconnected, // connections closed so held messages are sent again
                    backup = machineId is null ? $"Config/{fileName}.bak" : $"Config/{MachineRegistry.FolderName}/{machineId}/{fileName}.bak"
                });
            }
            catch (JsonException ex)
            {
                await WriteJsonAsync(context.Response, 400, new { error = $"Invalid JSON: {ex.Message}" });
            }

            return;
        }

        await WriteJsonAsync(context.Response, 405, new { error = "Method not allowed" });
    }

    // The MQTT password never goes back to the page: it gets PasswordMask instead, and
    // saving the mask unchanged keeps the stored password (anyone who can open the page,
    // in Docker anyone on the network, could otherwise read it).
    public const string PasswordMask = "••••••••";

    public static void MaskPassword(JsonNode? config)
    {
        if (config is JsonObject settings && PasswordKey(settings) is { } name
            && settings[name] is JsonValue value && value.TryGetValue<string>(out var password) && password.Length > 0)
            settings[name] = PasswordMask;
    }

    public static string KeepStoredPassword(string raw, string filePath)
    {
        if (JsonNode.Parse(raw) is not JsonObject incoming || PasswordKey(incoming) is not { } name
            || !(incoming[name] is JsonValue value && value.TryGetValue<string>(out var sent) && sent == PasswordMask))
            return raw;

        var stored = File.Exists(filePath) && JsonNode.Parse(File.ReadAllText(filePath)) is JsonObject file
                     && PasswordKey(file) is { } fileName && file[fileName] is JsonValue saved && saved.TryGetValue<string>(out var text)
            ? text
            : "";
        incoming[name] = stored;
        return incoming.ToJsonString();
    }

    private static string? PasswordKey(JsonObject settings) =>
        settings.Select(p => p.Key).FirstOrDefault(k => k.Equals("password", StringComparison.OrdinalIgnoreCase));

    // MQTT and machine settings: every setting the gateway reads, with the
    // built-in default where the file leaves it out, so the page can show
    // (and save) all of them. Other files are returned exactly as on disk.
    private static JsonNode? WithDefaults(string key, JsonNode? fromFile)
    {
        JsonObject? defaults = key.ToLowerInvariant() switch
        {
            // Relay settings first: for keys both classes read, the relay's default wins.
            "mqtt-config" => Merge(ToObject(new MqttConnectionConfig()), ToObject(new MqttOptions()), overwrite: false),
            "machine-config" => ToObject(new MachineConfig()),
            _ => null
        };

        if (defaults is null)
            return fromFile;

        return fromFile is JsonObject file ? Merge(defaults, file, overwrite: true) : defaults;
    }

    private static JsonObject ToObject<T>(T value) =>
        JsonSerializer.SerializeToNode(value, ApiJson)!.AsObject();

    // Copies source's keys into target (matching keys ignoring case).
    private static JsonObject Merge(JsonObject target, JsonObject source, bool overwrite)
    {
        foreach (var (name, value) in source)
        {
            var existing = target.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                target[name] = value?.DeepClone();
            else if (overwrite)
                target[existing] = value?.DeepClone();
        }

        return target;
    }

    private async Task ServeStaticAsync(HttpListenerContext context, string path)
    {
        var relative = path switch
        {
            "/" => "/config/index.html",
            "/config" => "/config/index.html",
            "/config/" => "/config/index.html",
            _ => path
        };

        var combined = Path.GetFullPath(Path.Combine(_wwwRoot, relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(_wwwRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) // not a sibling like wwwroot-private
        {
            await WriteJsonAsync(context.Response, 403, new { error = "Forbidden" });
            return;
        }

        if (!File.Exists(combined))
        {
            Console.WriteLine($"[config] static file not found: {combined}");
            Console.WriteLine($"[config] wwwroot: {_wwwRoot}");
            Console.WriteLine($"[config] base directory: {AppContext.BaseDirectory}");
            await WriteJsonAsync(context.Response, 404, new { error = "Not found", path = combined });
            return;
        }

        var mime = Path.GetExtension(combined).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => MediaTypeNames.Application.Json,
            _ => "application/octet-stream"
        };

        var bytes = await File.ReadAllBytesAsync(combined);
        context.Response.ContentType = mime;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.StatusCode = 200;
        // The page changes with the gateway build; never serve a stale copy.
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.OutputStream.WriteAsync(bytes);
    }

    private static bool HttpMethodsEqual(HttpListenerContext context, string method) =>
        string.Equals(context.Request.HttpMethod, method, StringComparison.OrdinalIgnoreCase);

    // The page asks for the status every few seconds: count failed messages at
    // most every 3 s (it opens every outbox file).
    private long _failedCount;
    private DateTime _failedCountAt = DateTime.MinValue;

    private long FailedCount()
    {
        if (DateTime.UtcNow - _failedCountAt > TimeSpan.FromSeconds(3))
        {
            try { _failedCount = OutboxReader.CountFailed(); } catch { /* keep the last count */ }
            _failedCountAt = DateTime.UtcNow;
        }
        return _failedCount;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload, ApiJson);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}

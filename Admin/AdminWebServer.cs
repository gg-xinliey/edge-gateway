using EdgeGateway.Models;
using EdgeGateway.Services;
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
    private readonly GatewayConfigStore _configStore;
    private readonly GatewayHost _gateway;
    private readonly UnidentifiedTagTracker _unidentified;
    private readonly GatewayLog _log;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly int _port;

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
        ["machine-config"] = "machine-config.json"
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
        GatewayConfigStore configStore,
        GatewayHost gateway,
        UnidentifiedTagTracker unidentified,
        GatewayLog log,
        JsonSerializerOptions jsonOptions)
    {
        _log = log;
        _port = port;
        _wwwRoot = Path.IsPathRooted(webRoot)
            ? Path.GetFullPath(webRoot)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, webRoot));
        _configRoot = configStore.ConfigRoot; // same folder the runtime reloads from
        _configStore = configStore;
        _gateway = gateway;
        _unidentified = unidentified;
        _jsonOptions = jsonOptions;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_wwwRoot);
        Directory.CreateDirectory(_configRoot);

        var prefix = $"http://localhost:{_port}/";
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
                machineId = _gateway.Machine.MachineId,
                tcpPort = _gateway.Machine.TcpPort,
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

        // Log lines newer than ?after=<id> (all kept lines when omitted), plus the
        // total number of problem lines since the gateway started.
        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/logs", StringComparison.OrdinalIgnoreCase))
        {
            _ = long.TryParse(context.Request.QueryString["after"], out var after);
            await WriteJsonAsync(context.Response, 200, new { lines = _log.Since(after), errorCount = _log.ErrorCount });
            return;
        }

        if (HttpMethodsEqual(context, "GET") && path.Equals("/api/unidentified", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(context.Response, 200, _unidentified.Snapshot());
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

        var filePath = Path.Combine(_configRoot, fileName);

        if (HttpMethodsEqual(context, "GET"))
        {
            var fromFile = File.Exists(filePath) ? JsonNode.Parse(await File.ReadAllTextAsync(filePath)) : null;
            var result = WithDefaults(key, fromFile);
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

                await File.WriteAllTextAsync(filePath, normalized + Environment.NewLine, Encoding.UTF8);

                var restartToApply = RestartToApply.Contains(key);

                // Point map and timestamp rules take effect right away.
                if (!restartToApply)
                    _configStore.Reload();

                await WriteJsonAsync(context.Response, 200, new
                {
                    ok = true,
                    file = fileName,
                    reloaded = !restartToApply,
                    restartToApply,
                    backup = $"Config/{fileName}.bak"
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
        if (!combined.StartsWith(_wwwRoot, StringComparison.OrdinalIgnoreCase))
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

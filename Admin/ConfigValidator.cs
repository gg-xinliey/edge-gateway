using System.Text.Json;
using EdgeGateway.Models;

namespace EdgeGateway.Admin;

// Checks an edited config file the way the gateway will read it, BEFORE it
// replaces the file on disk. A file that passes here cannot stop the
// gateway from (re)starting. Returns null when fine, otherwise a message the
// config page shows as-is.
public static class ConfigValidator
{
    public static string? Validate(string key, string json, JsonSerializerOptions jsonOptions, int adminPort)
    {
        try
        {
            return key switch
            {
                "point-map" => PointMap(JsonSerializer.Deserialize<List<PointMapEntry>>(json, jsonOptions)),
                "timestamp-rules" => TimestampRules(JsonSerializer.Deserialize<Dictionary<string, TimestampRule>>(json, jsonOptions)),
                "mqtt-config" => Mqtt(
                    JsonSerializer.Deserialize<MqttConnectionConfig>(json, jsonOptions),
                    JsonSerializer.Deserialize<MqttOptions>(json, jsonOptions)),
                "machine-config" => Machine(JsonSerializer.Deserialize<MachineConfig>(json, jsonOptions), adminPort),
                _ => $"Unknown configuration file '{key}'."
            };
        }
        catch (JsonException ex)
        {
            return $"Wrong value type: {ex.Message}";
        }
    }

    private static string? PointMap(List<PointMapEntry>? entries)
    {
        if (entries is null)
            return "The point map must be a list.";

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Tag))
                return $"A row in {entry.DataGroup} has an empty incoming tag.";
            if (string.IsNullOrWhiteSpace(entry.DataGroup))
                return $"Tag '{entry.Tag}' has no data group.";
            if (!seen.Add(entry.Tag))
                return $"Tag '{entry.Tag}' is listed twice. Tags must be unique (ignoring case).";
        }

        return null;
    }

    private static string? TimestampRules(Dictionary<string, TimestampRule>? rules)
    {
        if (rules is null)
            return "Timestamp rules must be an object of data groups.";

        foreach (var (group, rule) in rules)
        {
            if (rule is null)
                return $"{group}: the rule is empty.";

            if (rule.SendEveryMs is < 0 or > 3_600_000)
                return $"{group}: sendEveryMs must be between 0 and 3600000 (1 hour).";

            if (rule.Mode == TimestampMode.Span &&
                (string.IsNullOrWhiteSpace(rule.TriggerField) ||
                 string.IsNullOrWhiteSpace(rule.StartField) ||
                 string.IsNullOrWhiteSpace(rule.EndField)))
                return $"{group}: Span needs triggerField, startField and endField.";
        }

        return null;
    }

    private static string? Mqtt(MqttConnectionConfig? relay, MqttOptions? options)
    {
        if (relay is null || options is null)
            return "MQTT config must be an object.";
        if (string.IsNullOrWhiteSpace(relay.BrokerAddress))
            return "brokerAddress is empty.";
        if (relay.BrokerPort is < 1 or > 65535)
            return "brokerPort must be between 1 and 65535.";
        if (string.IsNullOrWhiteSpace(relay.ClientIdSuffix))
            return "clientIdSuffix is empty.";
        if (relay.ReconnectCooldownSeconds < 0)
            return "reconnectCooldownSeconds cannot be negative.";
        if (string.IsNullOrWhiteSpace(options.Area))
            return "area is empty.";
        if (options.KeepaliveIntervalSeconds < 1)
            return "keepaliveIntervalSeconds must be at least 1.";
        if (options.MotionDeadband < 0)
            return "motionDeadband cannot be negative.";
        if (options.MaxPublishRateMs < 1)
            return "maxPublishRateMs must be at least 1.";

        return null;
    }

    private static string? Machine(MachineConfig? machine, int adminPort)
    {
        if (machine is null)
            return "Machine config must be an object.";
        if (string.IsNullOrWhiteSpace(machine.GatewayId))
            return "gatewayId is empty.";
        if (string.IsNullOrWhiteSpace(machine.MachineId))
            return "machineId is empty.";
        if (!System.Net.IPAddress.TryParse(machine.ListenAddress, out var listen))
            return "listenAddress must be an IP address, e.g. 0.0.0.0 (all) or 192.168.250.78.";
        if (!listen.Equals(System.Net.IPAddress.Any) && !System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(listen)))
            return $"listenAddress {machine.ListenAddress} is not an address of this PC.";
        if (machine.TcpPort is < 1 or > 65535)
            return "tcpPort must be between 1 and 65535.";
        if (machine.TcpPort == adminPort)
            return $"tcpPort cannot be {adminPort}; the config page uses it.";
        if (!Uri.TryCreate(machine.MesUrl, UriKind.Absolute, out var mes) || (mes.Scheme != "http" && mes.Scheme != "https"))
            return "mesUrl must be a full http:// or https:// address.";
        if (machine.OutboxKeepSentDays < 1)
            return "outboxKeepSentDays must be at least 1.";
        if (machine.TimestampsWithoutZone.Trim().ToLowerInvariant() is not ("utc" or "local"))
            return "timestampsWithoutZone must be UTC or local.";

        return null;
    }
}

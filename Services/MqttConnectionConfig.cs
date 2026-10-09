namespace EdgeGateway.Models;

// Settings for the Digital Twin MQTT path. Read from Config/mqtt-config.json.
// This path is completely separate from the MES path (no parser, no envelope,
// no outbox): a line goes in, its SpotWelding-shaped payload goes out on the
// line's own topic, QoS 0. Which lines count as twin data is fixed by the
// topic ending (see MqttRelay), so there is nothing to configure for that.
public class MqttConnectionConfig
{
    public string BrokerAddress { get; set; } = "192.168.251.189";
    public int BrokerPort { get; set; } = 1883;

    // Client id = <this PC's name>_<suffix>. Must not match the client id the
    // production software uses (it uses <PC name>_<its sectionName>).
    public string ClientIdSuffix { get; set; } = "EdgeGateway";

    // After a failed connect, wait this long before trying again.
    // Messages that arrive in the meantime are dropped, not queued.
    public int ReconnectCooldownSeconds { get; set; } = 5;
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

// Connection and tuning settings for the twin-bound MQTT path (slide 6
// block 7 / slide 10). Loaded once at startup from Config/mqtt-config.json
// — same pattern as point-map.json and timestamp-rules.json.
public class MqttOptions
{
    public string BrokerAddress { get; set; } = "127.0.0.1";
    public int BrokerPort { get; set; } = 1883;

    // Same key as MqttConnectionConfig.ClientIdSuffix. The twin connection's
    // client id is <PC name>_<suffix>_TwinPublisher, so two gateways on one PC
    // with different suffixes don't kick each other off the broker.
    public string ClientIdSuffix { get; set; } = "EdgeGateway";

    // Same keys as MqttConnectionConfig.Username / Password (one broker login for both connections).
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    // Topic prefix for topics the gateway builds itself: svi/<area>/<machine_id>/...
    public string Area { get; set; } = "TEST";

    // How often the keepalive heartbeat publishes, independent of data activity.
    public int KeepaliveIntervalSeconds { get; set; } = 30;

    // Minimum change in a motion value (world/pulse/speed/force units) before
    // it's worth publishing again.
    public double MotionDeadband { get; set; } = 0.01;

    // Hard ceiling on publishes per topic, regardless of how fast the
    // source is changing: at least this many ms between two publishes
    // (50 = at most 20 per second).
    public int MaxPublishRateMs { get; set; } = 50;
}


using System.Text.Json;
using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Twin-bound counterpart to MesForwarder — but best-effort, not reliable.
// Applies deadband/rate limiting, builds the topic, and publishes at QoS 0.
// Also owns the keepalive heartbeat for this gateway. Nothing here touches
// the Outbox: per slide 10, a lost twin message is replaced by the next
// one, not retried.
public class TwinForwarder
{
    private readonly IMqttPublisher _mqtt;
    private readonly MqttOptions _options;
    private readonly string _machineId;
    private readonly DeadbandRateLimiter _limiter = new();
    private readonly TimeSpan _minInterval;

    public TwinForwarder(IMqttPublisher mqtt, MqttOptions options, string machineId)
    {
        _mqtt = mqtt;
        _options = options;
        _machineId = machineId;
        _minInterval = TimeSpan.FromMilliseconds(Math.Max(1, options.MaxPublishRateMs));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await _mqtt.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[twin] initial mqtt connect failed: {MqttPublisherService.Why(ex)} — will keep retrying");
        }

        while (!ct.IsCancellationRequested)
        {
            if (!_mqtt.IsConnected)
            {
                try
                {
                    await _mqtt.ConnectAsync(ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[twin] mqtt reconnect failed: {MqttPublisherService.Why(ex)}");
                }
            }

            if (_mqtt.IsConnected)
                await _mqtt.PublishAsync(MqttTopicBuilder.Keepalive(_options.Area, _machineId), "{\"alive\":true}", ct);

            await Task.Delay(TimeSpan.FromSeconds(_options.KeepaliveIntervalSeconds), ct);
        }
    }

    // Called from OnMessage for any record TwinRouter.IsTwinOnly() accepted.
    // qualifier is the topic's 3rd segment and picks one of the four fixed
    // endings: "output" -> IO/output (else IO/input), "status" ->
    // motion/status (else motion/position). Every axis shares
    // motion/position, so the payload's "axis" keeps their deadbands apart.
    public async Task PublishRecordAsync(string machineId, string dataGroup, string? qualifier, Dictionary<string, object?> fields, CancellationToken ct = default)
    {
        try
        {
            var isMotion = dataGroup.Equals("MOTION", StringComparison.OrdinalIgnoreCase);
            var isPosition = isMotion && !string.Equals(qualifier, "status", StringComparison.OrdinalIgnoreCase);

            var topic = isMotion
                ? isPosition
                    ? MqttTopicBuilder.MotionPosition(_options.Area, machineId)
                    : MqttTopicBuilder.MotionStatus(_options.Area, machineId)
                : string.Equals(qualifier, "output", StringComparison.OrdinalIgnoreCase)
                    ? MqttTopicBuilder.IoOutput(_options.Area, machineId)
                    : MqttTopicBuilder.IoInput(_options.Area, machineId);
            var key = isPosition
                ? $"{machineId}:{topic}:{fields.GetValueOrDefault("axis") ?? qualifier}"
                : $"{machineId}:{topic}";

            var worthPublishing = isPosition
                ? _limiter.ShouldPublishMotion(key, fields, _options.MotionDeadband, _minInterval)
                : _limiter.ShouldPublishOnChange(key, fields, _minInterval);

            if (!worthPublishing)
                return;

            await _mqtt.PublishAsync(topic, JsonSerializer.Serialize(fields), ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[twin] failed to publish {dataGroup} for {machineId}: {ex.Message}");
        }
    }
}
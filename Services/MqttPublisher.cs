using System.Collections.Concurrent;
using System.Text;
using EdgeGateway.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace EdgeGateway.Services;

// Fire-and-forget MQTT publisher for the Digital Twin lanes (motion / IO).
//
// Rules this class follows:
//   * QoS 0, never retained. If a message is lost, the next one replaces it.
//   * Fresh beats complete: when the broker is down, or a connect attempt is
//     already running, the message is DROPPED. It is never queued, because a
//     position from 10 seconds ago is worse than no position.
//   * It never throws into the caller. A broken broker must not be able to
//     hurt the MES path.
public sealed class MqttPublisher : IAsyncDisposable
{
    private readonly MqttConnectionConfig _config;
    private readonly IMqttClient _client;

    // Only one caller at a time is allowed to run the (slow) connect.
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private readonly ConcurrentDictionary<string, bool> _firstPublishLogged = new();

    private long _retryAfterTicks;      // UTC ticks; no connect attempts before this
    private long _lastErrorLogTicks;    // keeps the publish-error log from spamming at 50 msg/s

    public MqttPublisher(MqttConnectionConfig config)
    {
        _config = config;
        _client = new MqttFactory().CreateMqttClient();
    }

    public bool IsConnected => _client.IsConnected;

    // Publishes json on topic as given; MqttRelay has already checked the
    // topic and put the payload in SpotWelding shape.
    public async Task PublishRawAsync(string topic, string json, CancellationToken ct = default)
    {
        try
        {
            if (!await EnsureConnectedAsync(ct))
                return; // broker not available right now -> drop

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(json))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .WithRetainFlag(false)
                .Build();

            await _client.PublishAsync(message, ct);

            if (_firstPublishLogged.TryAdd(topic, true))
                Console.WriteLine($"[mqtt] first message published -> {topic}");
        }
        catch (Exception ex)
        {
            LogPublishError(ex);
        }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected)
            return true;

        // Cooling down after a failed connect.
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _retryAfterTicks))
            return false;

        // Somebody else is already connecting: drop this message instead of
        // piling up behind them (at 50 msg/s that pile would be stale on arrival).
        if (!await _connectGate.WaitAsync(0, ct))
            return false;

        try
        {
            if (_client.IsConnected)
                return true;

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.BrokerAddress, _config.BrokerPort)
                .WithClientId($"{Environment.MachineName}_{_config.ClientIdSuffix}")
                .WithCleanSession()
                .WithTimeout(TimeSpan.FromSeconds(3))
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
                .Build();

            await _client.ConnectAsync(options, ct);
            Console.WriteLine($"[mqtt] connected to {_config.BrokerAddress}:{_config.BrokerPort}");
            return true;
        }
        catch (Exception ex)
        {
            var wait = TimeSpan.FromSeconds(_config.ReconnectCooldownSeconds);
            Interlocked.Exchange(ref _retryAfterTicks, DateTime.UtcNow.Add(wait).Ticks);
            Console.WriteLine(
                $"[mqtt] cannot connect to {_config.BrokerAddress}:{_config.BrokerPort} ({ex.Message}) " +
                $"- messages are dropped, next try in {wait.TotalSeconds:0}s");
            return false;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    // At most one "publish failed" line every 5 seconds.
    private void LogPublishError(Exception ex)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastErrorLogTicks);
        if (now - last < TimeSpan.TicksPerSecond * 5)
            return;
        if (Interlocked.CompareExchange(ref _lastErrorLogTicks, now, last) != last)
            return;

        Console.WriteLine($"[mqtt] publish failed: {ex.Message}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync();
        }
        catch
        {
            // shutting down anyway
        }

        _client.Dispose();
        _connectGate.Dispose();
    }
}

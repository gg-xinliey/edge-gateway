using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using EdgeGateway.Models;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace EdgeGateway.Services;

// MQTT publisher for the Digital Twin lanes (motion / IO).
//
// Rules this class follows:
//   * QoS 0, never retained. If a message is lost, the next one replaces it.
//   * Fresh beats complete: when the broker is down, or a connect attempt is
//     already running, the message is DROPPED, because a position from
//     10 seconds ago is worse than no position.
//   * It never throws into the caller and never makes it wait. A broken broker
//     must not be able to hurt the MES path.
//   * Enqueue() puts the message on a short queue; ONE sender publishes them
//     in order. (Thousands of separate publish tasks per second piled up on
//     the client and stalled MQTT for 20 s under load.) If the queue fills
//     up, the oldest messages are dropped and counted.
public sealed class MqttPublisher : IAsyncDisposable
{
    private const int QueueCapacity = 20_000; // a few seconds at full load

    private readonly MqttConnectionConfig _config;
    private readonly IMqttClient _client;
    private readonly Channel<(string Topic, string Json)> _queue;
    private readonly Task _sender;
    private long _dropped;
    private DateTime _nextDropReport = DateTime.MinValue;

    // Only one caller at a time is allowed to run the (slow) connect.
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private readonly ConcurrentDictionary<string, bool> _firstPublishLogged = new();

    private long _retryAfterTicks;      // UTC ticks; no connect attempts before this
    private long _lastErrorLogTicks;    // keeps the publish-error log from spamming at 50 msg/s

    private readonly GatewayStats? _stats;

    public MqttPublisher(MqttConnectionConfig config, GatewayStats? stats = null)
    {
        _config = config;
        _stats = stats;
        _client = new MqttFactory().CreateMqttClient();
        _queue = Channel.CreateBounded<(string, string)>(
            new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            _ => { Interlocked.Increment(ref _dropped); _stats?.MqttLost(); });
        _sender = Task.Run(SendLoopAsync);
    }

    public bool IsConnected => _client.IsConnected;

    // Queues json for topic as given; MqttRelay has already checked the topic
    // and put the payload in SpotWelding shape. Returns at once.
    public void Enqueue(string topic, string json) => _queue.Writer.TryWrite((topic, json));

    private async Task SendLoopAsync()
    {
        await foreach (var (topic, json) in _queue.Reader.ReadAllAsync())
        {
            try
            {
                if (!await EnsureConnectedAsync(CancellationToken.None))
                {
                    Interlocked.Increment(ref _dropped); // broker not available right now -> drop
                    _stats?.MqttLost();
                }
                else
                {
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(topic)
                        .WithPayload(Encoding.UTF8.GetBytes(json))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                        .WithRetainFlag(false)
                        .Build();

                    await _client.PublishAsync(message);
                    _stats?.MqttPublished();

                    if (_firstPublishLogged.TryAdd(topic, true))
                        Console.WriteLine($"[mqtt] first message published -> {topic}");
                }
            }
            catch (Exception ex)
            {
                LogPublishError(ex);
            }

            // At most one line a minute, with the total since the last line.
            if (DateTime.UtcNow >= _nextDropReport)
            {
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                {
                    Console.WriteLine($"[mqtt] {dropped} messages dropped (broker down or too slow)");
                    _nextDropReport = DateTime.UtcNow.AddMinutes(1);
                }
            }
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
        _queue.Writer.TryComplete();
        await Task.WhenAny(_sender, Task.Delay(TimeSpan.FromSeconds(2))); // send what is queued, briefly

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

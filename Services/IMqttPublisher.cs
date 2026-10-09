namespace EdgeGateway.Services;

public interface IMqttPublisher
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct = default);
    Task PublishAsync(string topic, string payload, CancellationToken ct = default);
    Task DisconnectAsync();
}

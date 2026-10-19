using EdgeGateway.Models;

namespace EdgeGateway.Services;

// Category 10, System / Connectivity — wraps one category's payload
// for delivery to MES.
public class EnvelopeBuilder
{
    private readonly string _gatewayId;
    private readonly string _dataSource;
    private readonly string _protocol;

    public EnvelopeBuilder(string gatewayId, string dataSource, string protocol)
    {
        _gatewayId = gatewayId;
        _dataSource = dataSource;
        _protocol = protocol;
    }

    // dataSource: the message's own "data_source"; the configured one when it sent none.
    public Envelope Build(string machineId, string dataGroup, Dictionary<string, object?> payload, string? dataSource = null)
    {
        return new Envelope
        {
            event_id = Guid.NewGuid().ToString(),
            gateway_id = _gatewayId,
            machine_id = machineId,
            data_source = string.IsNullOrWhiteSpace(dataSource) ? _dataSource : dataSource,
            protocol = _protocol,
            data_group = dataGroup,
            publish_timestamp = TimeStamper.FormatIso(DateTimeOffset.Now),
            data_quality_status = "VALID",
            schema_version = "1.0",
            payload = payload
        };
    }
}

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
    // eventTime: the machine's "timestamp" (arrival time when it sent none).
    // mesColumns: the group's MES table columns (Config/mes-schema.json), or null.
    // messageId: the incoming message_id; event_id is made from it (see EventId).
    public Envelope Build(string machineId, string dataGroup, Dictionary<string, object?> payload, string? dataSource,
        DateTimeOffset eventTime, IReadOnlyList<string>? mesColumns, string? messageId = null)
    {
        return new Envelope
        {
            event_id = EventId(messageId, dataGroup),
            gateway_id = _gatewayId,
            machine_id = machineId,
            data_source = string.IsNullOrWhiteSpace(dataSource) ? _dataSource : dataSource,
            protocol = _protocol,
            data_group = dataGroup,
            event_time = TimeStamper.FormatIso(eventTime),
            publish_timestamp = TimeStamper.FormatIso(DateTimeOffset.Now),
            data_quality_status = "VALID",
            schema_version = "1.0",
            payload = Complete(payload, mesColumns)
        };
    }

    // Same message + group = same event_id, always (a GUID made from them). If
    // the gateway crashes after saving but before its reply reaches the
    // adapter, the adapter sends the message again: it then gets the same
    // event_id, and MES (event_id is unique there) can drop the copy.
    // One message gives at most one envelope per group, so they never clash.
    private static string EventId(string? messageId, string dataGroup)
    {
        if (string.IsNullOrEmpty(messageId))
            return Guid.NewGuid().ToString();

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(messageId + "\n" + dataGroup));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8: name-based, custom hash
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true).ToString();
    }

    // MES wants every column of the group's table present, null when the machine
    // did not send it, in the table's order. Fields MES has no column for follow
    // after them (MES keeps the whole payload in payload_json). A field that
    // matches a column in another case gets the column's spelling.
    private static Dictionary<string, object?> Complete(Dictionary<string, object?> record, IReadOnlyList<string>? mesColumns)
    {
        if (mesColumns is null || mesColumns.Count == 0)
            return record;

        var payload = new Dictionary<string, object?>(mesColumns.Count + record.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in mesColumns)
        {
            var name = record.ContainsKey(column)
                ? column
                : record.Keys.FirstOrDefault(k => k.Equals(column, StringComparison.OrdinalIgnoreCase));
            payload[column] = name is null ? null : record[name];
            if (name is not null)
                used.Add(name);
        }

        foreach (var (name, value) in record)
            if (!used.Contains(name) && !payload.ContainsKey(name))
                payload[name] = value;

        return payload;
    }
}

namespace EdgeGateway.Models;

// Category 10, System / Connectivity — the envelope every record
// travels in on its way to MES. Property names are lowercase snake_case
// on purpose: they must match the wire contract exactly, with no
// [JsonPropertyName] attributes needed.
public class Envelope
{
    public string event_id { get; set; } = Guid.NewGuid().ToString();
    public string gateway_id { get; set; } = "";
    public string machine_id { get; set; } = "";
    public string data_source { get; set; } = "";
    public string protocol { get; set; } = "";
    public string data_group { get; set; } = "";
    public string publish_timestamp { get; set; } = "";
    public string data_quality_status { get; set; } = "VALID";
    public string schema_version { get; set; } = "1.0";
    public Dictionary<string, object?> payload { get; set; } = new();
}

namespace EdgeGateway.Models;

// One row of the point map: which incoming tag becomes which
// Data Dictionary field, inside which of the nine MES categories.
public class PointMapEntry
{
    // Tag name exactly as it arrives from the sender
    // (a JSON key, or the first column of a CSV line).
    public string Tag { get; set; } = "";

    // One of: MACHINE_STATUS, PRODUCTION, PROCESS, QUALITY,
    // TRACEABILITY, MATERIAL, OPERATOR, ALARM, MAINTENANCE.
    public string DataGroup { get; set; } = "";

    // The exact field name the Data Dictionary expects inside "payload".
    public string FieldName
    { get; set; } = "";

    // Set by name matching (NameMatcher): the entry was added automatically, with this
    // score; nobody has checked it yet. A person saving or changing it on the config
    // page removes the mark. null = set by a person.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? Auto { get; set; }
}

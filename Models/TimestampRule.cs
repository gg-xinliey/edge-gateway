namespace EdgeGateway.Models;

public enum TimestampMode
{
   
    SinglePoint,

    Span
}

// How one MES data group wants to be timestamped. Kept separate from
// PointMapEntry because this is a per-category rule, not a per-tag one.
public class TimestampRule
{
    public TimestampMode Mode { get; set; } = TimestampMode.SinglePoint;

    // SinglePoint only: the Data Dictionary field name to stamp with "now".
    public string? TimestampField { get; set; }

    // Span only: the field whose value change opens/closes a span,
    // and the Data Dictionary field names for the start/end pair.
    public string? TriggerField { get; set; }
    public string? StartField { get; set; }
    public string? EndField { get; set; }

    // Span only, optional: where the value before the change goes
    // (MES MACHINE_STATUS: "previous_status"). Not set = "previous_<triggerField>".
    public string? PreviousField { get; set; }

    // How often this group's changes are sent to MES, per machine.
    // Every change is sent (in order, at the next tick); no change, no send.
    // 0 = right away. Not set = IntervalSender.DefaultSendEveryMs.
    public int? SendEveryMs { get; set; }
}

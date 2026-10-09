namespace EdgeGateway.Services;

// Slide 3: "No IO or motion stream is ever sent to MES." These two data
// groups are twin-only — OnMessage must check this before the record ever
// reaches EnvelopeBuilder / Outbox / MesForwarder.
public static class TwinRouter
{
    private static readonly HashSet<string> TwinOnlyGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "MOTION",
        "IO"
    };

    public static bool IsTwinOnly(string dataGroup) => TwinOnlyGroups.Contains(dataGroup);
}

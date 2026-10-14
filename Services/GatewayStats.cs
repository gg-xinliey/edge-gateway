namespace EdgeGateway.Services;

// Throughput counters. Every part adds to them; GatewayHost turns them into
// per-second rates every 10 s, prints one [stats] line and keeps the latest
// for the config page (/api/status). Lives as long as the GatewayHost, so a
// restart from the page does not reset the totals.
public sealed class GatewayStats
{
    private long _received, _rejected, _mqttPublished, _mqttLost, _mesQueued, _mesDelivered;

    public void Received() => Interlocked.Increment(ref _received);
    public void Rejected() => Interlocked.Increment(ref _rejected);
    public void MqttPublished() => Interlocked.Increment(ref _mqttPublished);
    public void MqttLost(long count = 1) => Interlocked.Add(ref _mqttLost, count);
    public void MesQueued() => Interlocked.Increment(ref _mesQueued);
    public void MesDelivered(long count) => Interlocked.Add(ref _mesDelivered, count);

    public StatsRates Latest { get; private set; } = new(0, 0, 0, 0, 0, 0, 0, null);

    // Rates since the previous call; resets the counters.
    public StatsRates Sample(TimeSpan period, long backlog)
    {
        double Take(ref long counter) => Interlocked.Exchange(ref counter, 0) / period.TotalSeconds;

        Latest = new StatsRates(
            Take(ref _received), Take(ref _rejected),
            Take(ref _mqttPublished), Take(ref _mqttLost),
            Take(ref _mesQueued), Take(ref _mesDelivered),
            backlog, DateTimeOffset.Now);
        return Latest;
    }
}

// Per-second rates over the last period, plus the outbox rows not yet delivered.
public sealed record StatsRates(
    double InPerSec, double RejectedPerSec,
    double MqttPerSec, double MqttLostPerSec,
    double MesQueuedPerSec, double MesDeliveredPerSec,
    long Backlog, DateTimeOffset? At);
